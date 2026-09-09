using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Pipeline;

namespace Babel.Player.Services;

public sealed partial class SessionWorkflowCoordinator
{
    public Task SeparateVocalsAsync(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        SeparateVocalsAsync(progress, stageContext: null, cancellationToken);

    internal async Task<string> SeparateVocalsAsync(
        IProgress<double>? progress,
        PipelineStageContext? stageContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(CurrentSession.IngestedMediaPath))
            throw new InvalidOperationException("No ingested media is available for vocal separation.");
        if (!File.Exists(CurrentSession.IngestedMediaPath))
            throw new FileNotFoundException($"Ingested media file not found: {CurrentSession.IngestedMediaPath}");

        if (!string.IsNullOrWhiteSpace(CurrentSession.VocalsAudioPath)
            && !string.IsNullOrWhiteSpace(CurrentSession.AmbianceAudioPath)
            && File.Exists(CurrentSession.VocalsAudioPath)
            && File.Exists(CurrentSession.AmbianceAudioPath))
        {
            return CurrentSession.VocalsAudioPath;
        }

        ReportStage(
            stageContext,
            "Checking vocal-separation readiness on the local GPU host…",
            progress01: 0,
            isIndeterminate: true);

        await EnsureContainerizedExecutionRuntimeStartedAsync(
            InferenceRuntime.Containerized,
            "Vocal separation",
            cancellationToken).ConfigureAwait(false);

        var readiness = _containerizedProbe is not null
            ? await ContainerizedProviderReadiness.CheckVocalSeparationForExecutionAsync(
                CurrentSettings,
                _containerizedProbe,
                cancellationToken).ConfigureAwait(false)
            : ContainerizedProviderReadiness.CheckVocalSeparation(CurrentSettings, serviceProbe: _containerizedProbe);

        if (!readiness.IsReady)
            throw new PipelineProviderException(readiness.BlockingReason ?? "Vocal separation is not ready.");

        var stemsDir = Path.Combine(GetSessionDirectory(), "stems");
        Directory.CreateDirectory(stemsDir);

        ReportStage(
            stageContext,
            "Separating vocals from backing track before transcription…",
            progress01: 0.05,
            isIndeterminate: true);

        _vocalSeparationProvider ??= CreateVocalSeparationProvider();
        var result = await _inferenceEngine.SeparateVocalsAsync(
            _vocalSeparationProvider,
            new VocalSeparationRequest(CurrentSession.IngestedMediaPath, stemsDir),
            cancellationToken).ConfigureAwait(false);

        if (!result.Success
            || string.IsNullOrWhiteSpace(result.VocalsAudioPath)
            || string.IsNullOrWhiteSpace(result.AmbianceAudioPath))
        {
            throw new InvalidOperationException(
                $"Vocal separation failed: {result.ErrorMessage ?? "Unknown vocal separation error"}");
        }

        if (!File.Exists(result.VocalsAudioPath))
            throw new InvalidOperationException($"Vocal separation completed but vocals artifact was not found: {result.VocalsAudioPath}");

        if (!File.Exists(result.AmbianceAudioPath))
            throw new InvalidOperationException($"Vocal separation completed but ambiance artifact was not found: {result.AmbianceAudioPath}");

        await WriteStemManifestsAsync(
                result.VocalsAudioPath,
                result.AmbianceAudioPath,
                cancellationToken)
            .ConfigureAwait(false);

        lock (_sessionLock)
        {
            CurrentSession = CurrentSession with
            {
                VocalsAudioPath = result.VocalsAudioPath,
                AmbianceAudioPath = result.AmbianceAudioPath,
                InstrumentalAudioPath = result.AmbianceAudioPath,
                StatusMessage = "Audio prepared for transcription.",
            };
        }
        await SaveCurrentSessionAsync().ConfigureAwait(false);

        ReportStage(
            stageContext,
            "Vocal separation complete. Transcription will use the isolated vocals stem.",
            progress01: 0.1,
            isIndeterminate: false);

        return result.VocalsAudioPath;
    }

    public Task TranscribeMediaAsync(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        TranscribeMediaAsync(progress, stageContext: null, cancellationToken);

    /// <summary>
    /// Transcribes the session's ingested media, writes the transcript to the session directory, updates session state, and optionally runs diarization.
    /// </summary>
    /// <param name="progress">Optional progress reporter receiving values from 0 to 1 for the overall transcription stage.</param>
    /// <param name="stageContext">Optional pipeline stage context used for reporting stage-specific updates.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no media is loaded, when a required model download fails, or when transcription itself fails.
    /// </exception>
    /// <exception cref="FileNotFoundException">Thrown when the ingested media file cannot be found on disk.</exception>
    /// <summary>
        /// Runs the transcription pipeline for the current session.
        /// Expects the session to have media loaded on entry and, on success, updates the session to the Transcribed stage and persists the session state.
        /// </summary>
        /// <param name="progress">Optional progress reporter for overall pipeline progress updates.</param>
        /// <param name="stageContext">Optional stage context that constrains or targets the pipeline stage; if provided, the pipeline will use it to mark or report stage-specific progress.</param>
        /// <param name="cancellationToken">Cancellation token to cancel the operation; when canceled, the method will honor the request and propagate <see cref="OperationCanceledException"/>.</param>
        /// <exception cref="PipelineProviderException">Thrown when the configured transcription provider or runtime is not ready for execution and the blocking reason prevents continuation.</exception>
    internal Task TranscribeMediaAsync(
        IProgress<double>? progress,
        PipelineStageContext? stageContext,
        CancellationToken cancellationToken) =>
        _transcriptionOrchestrator.ExecuteAsync(
            progress,
            stageContext is { } context ? context.ToShared() : null,
            cancellationToken);

    /// <summary>
        /// Advances the current session by translating the existing transcript into the specified target language.
        /// </summary>
        /// <remarks>
        /// Entry state: requires a session with a completed transcript (stage at or after <c>Transcribed</c>).
        /// On success: updates the session to the <c>Translated</c> stage and persists session metadata.
        /// Cancellation: honors <paramref name="cancellationToken"/> and will throw <see cref="OperationCanceledException"/> when canceled.
        /// </remarks>
        /// <param name="progress">Optional progress reporter for overall stage progress (0.0–1.0).</param>
        /// <param name="targetLanguage">Optional BCP-47 language code to translate into; when null, the session or settings default is used.</param>
        /// <param name="sourceLanguage">Optional source language hint; when null, the session or settings default is used.</param>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        /// <returns>A task that completes when the translation stage finishes and session state has been persisted.</returns>
        public Task TranslateTranscriptAsync(
        IProgress<double>? progress = null,
        string? targetLanguage = null,
        string? sourceLanguage = null,
        CancellationToken cancellationToken = default) =>
        TranslateTranscriptAsync(progress, targetLanguage, sourceLanguage, stageContext: null, cancellationToken);

    /// <summary>
        /// Translates the current transcript into the specified target language and advances the session to the Translated stage.
        /// </summary>
        /// <param name="progress">Optional progress reporter for stage progress updates.</param>
        /// <param name="targetLanguage">Target language code for translation (e.g., "en", "fr").</param>
        /// <param name="sourceLanguage">Optional source language hint; if null the system will use the session's detected language.</param>
        /// <param name="stageContext">Optional stage context controlling persistence/visibility of intermediate stage updates.</param>
        /// <param name="cancellationToken">Token to observe for cancellation; operation honors cancellation and will throw <see cref="OperationCanceledException"/> when requested.</param>
        /// <returns>A task that completes when translation finishes. On success the session's stage is set to Translated and translation metadata are persisted.</returns>
        internal Task TranslateTranscriptAsync(
        IProgress<double>? progress,
        string? targetLanguage,
        string? sourceLanguage,
        PipelineStageContext? stageContext,
        CancellationToken cancellationToken) =>
        _translationOrchestrator.ExecuteAsync(
            progress,
            targetLanguage,
            sourceLanguage,
            stageContext is { } context ? context.ToShared() : null,
            cancellationToken);

    /// <summary>
        /// Runs the TTS generation pipeline for the current session using the provided voice and progress reporter.
        /// </summary>
        /// <param name="progress">Optional progress reporter that receives stage progress values (0.0–1.0).</param>
        /// <param name="voice">Optional voice identifier to use for TTS; if null the session/default voice is used.</param>
        /// <param name="cancellationToken">Token to observe for cancelling the operation.</param>
        /// <remarks>
        /// Entry state: requires the session to be at or beyond the Translated stage (a translation must exist).  
        /// Exit state: on success the session will be advanced to the TtsGenerated stage and TTS-related metadata will be persisted.  
        /// Persistence: this operation updates and saves the current session state on successful completion.  
        /// Cancellation: honoring <paramref name="cancellationToken"/> will cancel the operation; if cancelled the session may be left unchanged or partially updated depending on progress.
        /// </remarks>
        /// <returns>Completion of the TTS generation operation.</returns>
        public Task GenerateTtsAsync(
        IProgress<double>? progress = null,
        string? voice = null,
        CancellationToken cancellationToken = default) =>
        GenerateTtsAsync(progress, voice, stageContext: null, cancellationToken);

    /// <summary>
    /// Generate per-segment TTS clips for the current translation, stitch them into a combined dub audio file, and update the session state.
    /// </summary>
    /// <param name="progress">Optional overall progress reporter (0.0–1.0) used for stage updates.</param>
    /// <param name="voice">Optional voice identifier to use; if null, the configured TTS voice is used.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <param name="stageContext">Optional context used to report pipeline stage messages and progress.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no translation is available, a required voice model download fails, or when zero segment clips were produced.
    /// </exception>
    /// <exception cref="FileNotFoundException">Thrown when the translation file referenced by the session cannot be found.</exception>
    /// <summary>
    /// Generates speech audio (a combined dub) from the current session's translation and persists TTS artifacts to the session state.
    /// </summary>
    /// <param name="progress">Optional progress reporter for overall TTS pipeline progress.</param>
    /// <param name="voice">Optional voice identifier to use; when null the coordinator's configured TTS voice is used.</param>
    /// <param name="stageContext">Optional context used for stage reporting; used to annotate and report stage progress and completion.</param>
    /// <param name="cancellationToken">Cancellation token that aborts the operation; cooperative cancellation is honored by awaited operations.</param>
    /// <remarks>
    /// Preconditions: requires <see cref="CurrentSession.TranslationPath"/> to be non-empty and point to an existing translation artifact.
    /// On success: creates per-segment audio under the session's tts/segments directory, produces a combined dub MP3 under tts/, and updates and persists the session state to the TtsGenerated stage.
    /// Guarding behavior: verifies TTS provider/runtime readiness and downloads any required models before generation; if readiness cannot be achieved the method throws <see cref="PipelineProviderException"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when no translation path is available on the current session.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the translation artifact file cannot be found on disk.</exception>
    /// <summary>
        /// Executes the TTS generation pipeline for the current session using the specified voice.
        /// </summary>
        /// <remarks>
        /// Entry state: requires the session to contain a completed translation (session stage at or after Translated).  
        /// Exit state on success: updates the session to indicate TTS has been generated (TtsGenerated) and persists session metadata.  
        /// Cancellation: honors <paramref name="cancellationToken"/> and will throw <see cref="OperationCanceledException"/> when cancelled.  
        /// Provider readiness: verifies the configured TTS runtime/voice is available; a readiness failure may result in a <see cref="PipelineProviderException"/> or an <see cref="InvalidOperationException"/> if required model download fails.
        /// </remarks>
        /// <param name="progress">Optional progress reporter for overall pipeline progress (0.0–1.0).</param>
        /// <param name="voice">Optional voice identifier to use for generation; if null the pipeline resolves a default voice.</param>
        /// <param name="stageContext">Optional stage context that targets a specific stage marker or controls stage persistence behavior; when provided the pipeline uses it to scope progress and completion reporting.</param>
        /// <param name="cancellationToken">Cancellation token to abort pipeline execution.</param>
        /// <exception cref="PipelineProviderException">Thrown when the configured TTS provider/runtime is not ready and cannot proceed.</exception>
    internal Task GenerateTtsAsync(
        IProgress<double>? progress,
        string? voice,
        PipelineStageContext? stageContext,
        CancellationToken cancellationToken) =>
        _ttsPipelineOrchestrator.ExecuteAsync(progress, voice, stageContext, cancellationToken);

    /// <summary>
    /// Verifies the transcription runtime/provider is ready for execution and ensures any required transcription model is available.
    /// </summary>
    /// <param name="progress">Optional progress reporter for overall readiness; may be used to report download progress.</param>
    /// <param name="stageContext">Optional pipeline stage context used for reporting stage messages.</param>
    /// <param name="cancellationToken">Token to cancel readiness checks and any model download operations.</param>
    /// <remarks>
    /// Entry state: no specific session stage required; caller should have a configured <c>CurrentSettings.TranscriptionProvider</c> and <c>CurrentSettings.TranscriptionModel</c>.
    /// Exit state: a transcription service instance is created and assigned to <c>_transcriptionService</c> if readiness checks and any necessary model download succeed.
    /// This method does not persist session state.
    /// Cancellation: the operation observes <paramref name="cancellationToken"/> and will abort ongoing checks or downloads when cancelled.
    /// Guard behavior:
    /// - If the provider/runtime is blocked from executing and a model download is not allowed, a <see cref="PipelineProviderException"/> is thrown with the blocking reason.
    /// - If a required model download fails, an <see cref="InvalidOperationException"/> is thrown.
    /// </remarks>
    /// <exception cref="PipelineProviderException">Thrown when the provider/runtime is not ready and model download is not permitted; the exception message contains the blocking reason.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a required model download fails.</exception>
    private async Task EnsureTranscriptionProviderReadyAsync(
        IProgress<double>? progress,
        PipelineStageContext? stageContext,
        CancellationToken cancellationToken)
    {
        ReportStage(
            stageContext,
            $"Checking transcription runtime, provider readiness, and model availability for {CurrentSettings.TranscriptionProvider} / {CurrentSettings.TranscriptionModel}…",
            progress01: 0,
            isIndeterminate: true);

        await EnsureContainerizedExecutionRuntimeStartedAsync(CurrentSettings.TranscriptionRuntime, cancellationToken);

        var readiness = CurrentSettings.TranscriptionRuntime == InferenceRuntime.Containerized && _containerizedProbe is not null
            ? await ContainerizedProviderReadiness.CheckTranscriptionForExecutionAsync(
                CurrentSettings,
                _containerizedProbe,
                cancellationToken)
            : TranscriptionRegistry.CheckReadiness(
                CurrentSettings.TranscriptionProvider,
                CurrentSettings.TranscriptionModel,
                CurrentSettings,
                KeyStore,
                CurrentSettings.TranscriptionProfile);

        if (!readiness.IsReady && !readiness.RequiresModelDownload)
            throw new PipelineProviderException(readiness.BlockingReason!);

        if (readiness.RequiresModelDownload)
        {
            var downloadProgress = CreateStageDownloadProgress(
                stageContext,
                progress,
                $"Preparing transcription model '{CurrentSettings.TranscriptionModel}'");
            if (!await TranscriptionRegistry.EnsureModelAsync(
                    CurrentSettings.TranscriptionProvider,
                    CurrentSettings.TranscriptionModel,
                    CurrentSettings,
                    downloadProgress,
                    cancellationToken,
                    CurrentSettings.TranscriptionProfile,
                    KeyStore))
            {
                throw new InvalidOperationException($"Failed to download model '{CurrentSettings.TranscriptionModel}'.");
            }
        }

        _transcriptionService ??= CreateTranscriptionService();
    }

    private async Task CommitTranscriptionSessionStateAsync(TranscriptionResult result, string transcriptPath)
    {
        transcriptPath = await ArtifactIntegrity.FinalizeWorkingArtifactAsync(transcriptPath).ConfigureAwait(false);
        var transcriptArtifact = await ArtifactJson.LoadTranscriptAsync(transcriptPath).ConfigureAwait(false);
        await WriteTranscriptManifestAsync(transcriptPath, transcriptArtifact, CancellationToken.None).ConfigureAwait(false);

        var nowUtc = DateTimeOffset.UtcNow;
        // When vocal separation is disabled, clear any stale stem paths from a previous run that
        // had separation enabled. When separation is enabled, SeparateVocalsAsync already wrote
        // fresh stem paths into CurrentSession before this method is called, so we preserve them.
        var vocalsPath = CurrentSettings.VocalSeparationEnabled ? CurrentSession.VocalsAudioPath : null;
        var ambiancePath = CurrentSettings.VocalSeparationEnabled ? CurrentSession.AmbianceAudioPath : null;
        var candidate = CurrentSession with
        {
            Stage = SessionWorkflowStage.Transcribed,
            TranscriptPath = transcriptPath,
            SourceLanguage = result.Language,
            VocalsAudioPath = vocalsPath,
            AmbianceAudioPath = ambiancePath,
            InstrumentalAudioPath = ambiancePath,
            TranscribedAtUtc = nowUtc,
            TranscriptionRuntime = CurrentSettings.TranscriptionRuntime,
            TranscriptionProvider = CurrentSettings.TranscriptionProvider,
            TranscriptionModel = CurrentSettings.TranscriptionModel,
            TranscriptionLanguageHint = SessionSnapshotSemantics.NormalizeTranscriptionLanguageHint(
                CurrentSettings.TranscriptionLanguageHint),
            StatusMessage = ShouldRunDiarization()
                ? $"Transcribed {result.Segments.Count} segments ({result.Language}). Speaker mapping is available before translation."
                : $"Transcribed {result.Segments.Count} segments ({result.Language}). Ready for translation.",
        };
        if (!ArtifactIntegrityValidator.ValidateTranscript(candidate, out var integrityError))
            throw new InvalidOperationException($"Transcript integrity validation failed after commit: {integrityError}");

        lock (_sessionLock)
        {
            CurrentSession = candidate;
        }

        _log.Debug($"Transcription complete: {result.Segments.Count} segments, language: {result.Language}");
        await SaveCurrentSessionAsync().ConfigureAwait(false);
    }

    private async Task CommitTranslationSessionStateAsync(
        TranslationExecutionSnapshot snapshot,
        TranslationResult result)
    {
        if (!TranslationInputsStillMatch(snapshot))
        {
            _log.Warning(
                $"Discarding translation run {snapshot.RunId} because session inputs changed. Keeping orphaned artifact at {snapshot.WorkingTranslationPath}.");
            return;
        }

        var settingsDrifted = TranslationSettingsDrifted(snapshot);
        var translationPath = await ArtifactIntegrity.FinalizeWorkingArtifactAsync(snapshot.WorkingTranslationPath).ConfigureAwait(false);
        var translationArtifact = await ArtifactJson.LoadTranslationAsync(translationPath).ConfigureAwait(false);
        var sourceLanguage = string.IsNullOrWhiteSpace(result.SourceLanguage) ? snapshot.SourceLanguage : result.SourceLanguage;
        var targetLanguage = string.IsNullOrWhiteSpace(result.TargetLanguage) ? snapshot.TargetLanguage : result.TargetLanguage;
        await WriteTranslationManifestAsync(
                translationPath,
                translationArtifact,
                sourceLanguage,
                targetLanguage,
                CancellationToken.None)
            .ConfigureAwait(false);

        var nowUtc = DateTimeOffset.UtcNow;
        var candidate = CurrentSession with
        {
            Stage = SessionWorkflowStage.Translated,
            TranslationPath = translationPath,
            SourceLanguage = sourceLanguage,
            TargetLanguage = targetLanguage,
            TranslatedAtUtc = nowUtc,
            TranslationRuntime = snapshot.Plan.Runtime,
            TranslationProvider = snapshot.Plan.ProviderId,
            TranslationModel = snapshot.Model,
            TranslationSettingsDriftedSinceArtifact = settingsDrifted,
            TranslationRunId = snapshot.RunId,
            StatusMessage = settingsDrifted
                ? "Translation complete using previous settings. Re-run translation to apply current settings."
                : "Translation complete. Ready for dubbing.",
        };
        if (!ArtifactIntegrityValidator.ValidateTranslation(candidate, out var integrityError))
            throw new InvalidOperationException($"Translation integrity validation failed after commit: {integrityError}");

        lock (_sessionLock)
        {
            CurrentSession = candidate;
        }

        _log.Debug(
            $"Translation complete run={snapshot.RunId}: {result.Segments.Count} segments, {sourceLanguage} -> {targetLanguage}, settings_drifted={(settingsDrifted ? "true" : "false")}");
        await SaveCurrentSessionAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Checks TTS runtime and provider readiness, then downloads the voice model if needed.
    /// Mirrors the guard pattern used by TranscribeMediaAsync and TranslateTranscriptAsync.
    /// </summary>
    private async Task EnsureTtsProviderReadyAsync(
        string voice,
        IProgress<double>? progress,
        PipelineStageContext? stageContext,
        CancellationToken cancellationToken)
    {
        ReportStage(
            stageContext,
            $"Checking TTS runtime, provider readiness, voice assets, and speaker/reference setup for {CurrentSettings.TtsProvider} / {voice}…",
            progress01: 0,
            isIndeterminate: true);

        await EnsureContainerizedExecutionRuntimeStartedAsync(
            CurrentSettings.TtsRuntime,
            "TTS",
            cancellationToken);

        var readiness = CurrentSettings.TtsRuntime == InferenceRuntime.Containerized && _containerizedProbe is not null
            ? await ContainerizedProviderReadiness.CheckTtsForExecutionAsync(
                CurrentSettings,
                _containerizedProbe,
                cancellationToken)
            : TtsRegistry.CheckReadiness(
                CurrentSettings.TtsProvider,
                voice,
                CurrentSettings,
                KeyStore,
                CurrentSettings.TtsProfile);

        if (!readiness.IsReady && !readiness.RequiresModelDownload)
            throw new PipelineProviderException(readiness.BlockingReason!);

        if (readiness.RequiresModelDownload)
        {
            var downloadProgress = CreateStageDownloadProgress(
                stageContext,
                progress,
                $"Preparing TTS voice '{voice}'");
            if (!await TtsRegistry.EnsureModelAsync(
                    CurrentSettings.TtsProvider,
                    voice,
                    CurrentSettings,
                    downloadProgress,
                    cancellationToken,
                    CurrentSettings.TtsProfile,
                    KeyStore))
            {
                throw new InvalidOperationException($"Failed to download voice '{voice}'.");
            }
        }
    }

    /// <summary>
    /// Loads the translation, dispatches per-segment TTS generation (Qwen batch or parallel generic),
    /// and returns the produced audio paths keyed by segment ID, the total candidate count,
    /// and the ordered segment list (for stitch ordering without a second disk read).
    /// <summary>
    /// Generates per-segment TTS audio clips for the current translation and returns produced paths and durations.
    /// </summary>
    /// <remarks>
    /// Entry requirements: <see cref="CurrentSession.TranslationPath"/> must be set and point to an existing translation artifact.
    /// On success: does not modify or persist <see cref="CurrentSession"/> stage/state; it only produces segment audio artifacts on disk.
    /// Cancellation: honors <paramref name="cancellationToken"/> and will throw <see cref="OperationCanceledException"/> when cancelled.
    /// </remarks>
    /// <param name="voice">The TTS voice to use for generated segments.</param>
    /// <param name="ttsLanguage">Optional language identifier to pass to the TTS provider; may be null to use provider defaults.</param>
    /// <param name="segmentsDir">Directory where per-segment audio files will be written.</param>
    /// <param name="stageContext">Optional pipeline stage context used for progress/stage reporting.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>
    /// A tuple containing:
    /// - `SegmentAudioPaths`: a thread-safe map from segment ID to generated audio file path;
    /// - `SegmentDurations`: a thread-safe map from segment ID to the audio duration in seconds (only present for segments that reported duration);
    /// - `TotalSegments`: the number of candidate segments considered for generation;
    /// - `OrderedSegments`: the ordered list of translation segments that were processed.
    /// </returns>
    private async Task<(ConcurrentDictionary<string, string> SegmentAudioPaths, ConcurrentDictionary<string, double> SegmentDurations, int TotalSegments, IReadOnlyList<TranslationSegmentArtifact> OrderedSegments)> GenerateSegmentClipsAsync(
        TtsExecutionSnapshot snapshot,
        PipelineStageContext? stageContext,
        CancellationToken cancellationToken)
    {
        var segmentAudioPaths = new ConcurrentDictionary<string, string>();
        var segmentDurations = new ConcurrentDictionary<string, double>();

        ReportStage(
            stageContext,
            "Generating per-segment dubbed clips for preview, seek, and segment-level refinement…",
            progress01: 0,
            isIndeterminate: true);

        var translationData = await _artifactReader.LoadTranslationAsync(snapshot.TranslationPath, cancellationToken);
        var candidateSegments = translationData.Segments?
            .Where(seg => !string.IsNullOrWhiteSpace(seg.Id) && !string.IsNullOrWhiteSpace(seg.TranslatedText))
            .ToList()
            ?? [];

        int totalSegments = candidateSegments.Count;
        ReportStage(
            stageContext,
            $"Generating {totalSegments} segment clips…",
            progress01: 0,
            isIndeterminate: true);

        try
        {
            if (snapshot.Provider is QwenContainerTtsProvider qwenProvider)
            {
                await GenerateQwenBatchSegmentAudioAsync(
                    snapshot,
                    qwenProvider,
                    candidateSegments,
                    stageContext,
                    segmentAudioPaths,
                    segmentDurations,
                    cancellationToken);
            }
            else
            {
                int completed = 0;
                var parallelism = snapshot.MaxConcurrency;
                await Parallel.ForEachAsync(
                    candidateSegments,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = parallelism,
                        CancellationToken = cancellationToken,
                    },
                    async (seg, ct) =>
                    {
                        await GenerateSingleSegmentAsync(
                            snapshot,
                            seg,
                            segmentAudioPaths,
                            segmentDurations,
                            totalSegments,
                            stageContext,
                            ct,
                            onSucceeded: () => Interlocked.Increment(ref completed)).ConfigureAwait(false);
                    }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Error($"TTS stage failed: {ex.Message}", ex);
            throw;
        }

        return (segmentAudioPaths, segmentDurations, totalSegments, candidateSegments);
    }

    /// <summary>
    /// Generates TTS audio for a single translation segment and records the output path on success.
    /// Called concurrently inside Parallel.ForEachAsync for non-Qwen providers.
    /// </summary>
    /// <summary>
    /// Generates TTS audio for a single translation segment and records the output path on success.
    /// Called concurrently inside Parallel.ForEachAsync for non-Qwen providers.
    /// <paramref name="onSucceeded"/> is invoked (thread-safely by the caller) to increment the shared
    /// progress counter; returning the new count lets this method report accurate progress without
    /// holding a ref parameter across an async boundary.
    /// <summary>
    /// Synchronously schedules and awaits generation of a single segment TTS clip, recording its output path and duration when successful.
    /// </summary>
    /// <param name="seg">The translated segment artifact containing Id, TranslatedText, and speaker metadata.</param>
    /// <param name="defaultVoice">Fallback voice to use if the segment does not specify one.</param>
    /// <param name="ttsLanguage">Optional TTS language hint to pass to the provider.</param>
    /// <param name="segmentsDir">Directory where per-segment audio files are written.</param>
    /// <param name="segmentAudioPaths">Concurrent map to record produced segment audio file paths keyed by segment Id.</param>
    /// <param name="segmentDurations">Concurrent map to record produced segment durations (seconds) keyed by segment Id.</param>
    /// <param name="totalSegments">Total number of segments being generated (used for progress reporting).</param>
    /// <param name="stageContext">Optional pipeline stage context used for progress reporting.</param>
    /// <param name="cancellationToken">Token to observe for cooperative cancellation of the generation operation.</param>
    /// <summary>
    /// Generates TTS audio for a single translation segment, records generated artifact paths/durations, and reports per-segment progress.
    /// </summary>
    /// <param name="seg">The translation segment to synthesize; its <see cref="TranslationSegmentArtifact.Id"/> and <see cref="TranslationSegmentArtifact.TranslatedText"/> must be non-empty.</param>
    /// <param name="defaultVoice">Fallback voice to use when the segment does not specify one.</param>
    /// <param name="ttsLanguage">Optional language hint for the TTS engine.</param>
    /// <param name="segmentsDir">Directory where the segment audio file will be written (file named "{id}.mp3").</param>
    /// <param name="segmentAudioPaths">Concurrent map updated with the segment id → output audio path on successful generation.</param>
    /// <param name="segmentDurations">Concurrent map updated with the segment id → duration in seconds when the TTS result provides it.</param>
    /// <param name="totalSegments">Total number of candidate segments used to compute progress reporting.</param>
    /// <param name="stageContext">Optional stage context used for progress and status reporting.</param>
    /// <param name="cancellationToken">Cancellation token that will be passed to the TTS provider call; operation observes cancellation prior to awaiting the provider.</param>
    /// <param name="onSucceeded">Callback invoked when this segment is recorded as succeeded; should return the updated completed-segment count.</param>
    /// <remarks>
    /// Entry state: any pipeline stage that has a valid translation artifact containing this segment. On success this method updates <paramref name="segmentAudioPaths"/> and optionally <paramref name="segmentDurations"/>, invokes <paramref name="onSucceeded"/>, and reports stage progress; it does not persist session state. Failures from the TTS provider are logged and suppressed (do not propagate), while cancellation is honored via <paramref name="cancellationToken"/>. If the segment has empty text or id the method returns without performing work.
    /// </remarks>
    private async Task GenerateSingleSegmentAsync(
        TtsExecutionSnapshot snapshot,
        TranslationSegmentArtifact seg,
        ConcurrentDictionary<string, string> segmentAudioPaths,
        ConcurrentDictionary<string, double> segmentDurations,
        int totalSegments,
        PipelineStageContext? stageContext,
        CancellationToken cancellationToken,
        Func<int> onSucceeded)
    {
        var id = seg.Id;
        var text = seg.TranslatedText;

        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(id))
        {
            _log.Info($"Skipping segment {id}: empty text or ID");
            return;
        }

        var segmentAudioPath = Path.Combine(snapshot.SegmentsDir, $"{id}.mp3");
        var resolvedVoice = snapshot.ResolveVoiceForSegment(seg);
        var referenceAudioPath = snapshot.ResolveReferenceAudioForSegment(seg);

        _log.Info($"Generating TTS for segment {id} (voice={resolvedVoice}, speaker={seg.SpeakerId ?? "<none>"}): {text[..Math.Min(30, text.Length)]}...");

        try
        {
            var segTask = _inferenceEngine.GenerateSegmentTtsAsync(
                snapshot.Provider,
                new SingleSegmentTtsRequest(
                    text,
                    segmentAudioPath,
                    resolvedVoice,
                    seg.SpeakerId,
                    referenceAudioPath,
                    Language: snapshot.Language,
                    SourceVideoPath: snapshot.SourceVideoPath),
                cancellationToken);
            TrackPendingTtsTask(segTask);
            var segResult = await segTask;

            if (segResult.Success && File.Exists(segmentAudioPath))
            {
                segmentAudioPaths[id] = segmentAudioPath;
                if (segResult.DurationSeconds is { } dur)
                    segmentDurations[id] = dur;
                var done = onSucceeded();
                ReportStage(
                    stageContext,
                    $"Generated segment clip {done} of {totalSegments}…",
                    progress01: (double)done / totalSegments,
                    isIndeterminate: false);
                _log.Info($"Segment TTS generated: {id} -> {segmentAudioPath}");
            }
            else
            {
                _log.Warning($"Segment TTS failed or file missing: {id}");
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Segment TTS generation failed for {id}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Collects segment audio paths in transcript order, concatenates them into a single dub file,
    /// and validates that the output file was produced.
    /// <paramref name="orderedSegments"/> comes from <see cref="GenerateSegmentClipsAsync"/> to avoid
    /// a redundant disk read of the translation artifact.
    /// </summary>
    private async Task<DubRenderResult> StitchSegmentClipsAsync(
        TtsExecutionSnapshot snapshot,
        ConcurrentDictionary<string, string> segmentAudioPaths,
        IReadOnlyList<TranslationSegmentArtifact> orderedSegments,
        string ttsPath,
        PipelineStageContext? stageContext,
        CancellationToken cancellationToken)
    {
        ReportStage(
            stageContext,
            "Stitching segment clips into combined dub file…",
            progress01: 1,
            isIndeterminate: true);
        return await RenderDubAudioAsync(
                snapshot,
                orderedSegments,
                segmentAudioPaths,
                ttsPath,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Validates segment yield, advances session to TtsGenerated, persists state, and reports stage completion.
    /// <summary>
    /// Finalizes TTS state after segment generation by updating the current session to the TtsGenerated stage, persisting the session, and reporting completion.
    /// </summary>
    /// <param name="voice">The voice identifier used to generate the TTS dub.</param>
    /// <param name="ttsPath">Path to the final stitched dub output file.</param>
    /// <param name="segmentsDir">Directory containing per-segment audio files.</param>
    /// <param name="segmentAudioPaths">Map of segment ID to generated audio file path for successfully produced segments.</param>
    /// <param name="segmentDurations">Optional map of segment ID to duration in seconds; when non-null and non-empty this will be persisted to the session, otherwise durations are not saved.</param>
    /// <param name="totalSegments">Total number of segments expected for the translation/TTS run.</param>
    /// <param name="stageContext">Optional pipeline stage context used for final reporting.</param>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="totalSegments"/> &gt; 0 but no segments were successfully generated.</exception>
    private async Task CommitTtsSessionStateAsync(
        TtsExecutionSnapshot snapshot,
        DubRenderResult renderResult,
        ConcurrentDictionary<string, string> segmentAudioPaths,
        ConcurrentDictionary<string, double>? segmentDurations,
        IReadOnlyList<TranslationSegmentArtifact> orderedSegments,
        PipelineStageContext? stageContext)
    {
        if (!TtsInputsStillMatch(snapshot))
        {
            _log.Warning(
                $"Discarding TTS run {snapshot.RunId} because session inputs changed. Keeping orphaned artifacts under {snapshot.SegmentsDir}.");
            throw new InvalidOperationException(
                "TTS inputs changed while the pipeline was running. Rerun TTS to apply the latest settings.");
        }

        int totalSegments = orderedSegments.Count;
        int succeeded = segmentAudioPaths.Count;
        var settingsDrifted = TtsSettingsDrifted(snapshot);

        if (totalSegments > 0 && succeeded == 0)
        {
            _log.Error("TTS stage completed but no segments were generated.", new InvalidOperationException("Zero TTS segments"));
            throw new InvalidOperationException(
                "TTS stage completed but no segments were generated. Check provider configuration and logs.");
        }

        if (succeeded != totalSegments)
        {
            lock (_sessionLock)
            {
                CurrentSession = CurrentSession with
                {
                    Stage = SessionWorkflowStage.Translated,
                    TtsPath = null,
                    MixedDubAudioPath = null,
                    TtsGeneratedAtUtc = null,
                    TtsSegmentsPath = snapshot.SegmentsDir,
                    TtsSegmentAudioPaths = new Dictionary<string, string>(segmentAudioPaths),
                    TtsSegmentDurations = segmentDurations is { Count: > 0 }
                        ? new Dictionary<string, double>(segmentDurations)
                        : null,
                    TtsVoice = snapshot.Voice,
                    TtsRuntime = snapshot.Plan.Runtime,
                    TtsProvider = snapshot.Plan.ProviderId,
                    TtsSettingsDriftedSinceArtifact = settingsDrifted,
                    TtsRunId = snapshot.RunId,
                    DubTimingMode = snapshot.DefaultTimingMode,
                    AmbianceMixDb = snapshot.AmbianceMixDb,
                    StatusMessage = $"Dub incomplete. {succeeded}/{totalSegments} segment clips are ready; regenerate the failed clips before finalizing the dub.",
                };
            }

            await SaveCurrentSessionAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                $"TTS generated only {succeeded}/{totalSegments} segment clips. Remaining failed segments must be regenerated before finalizing the dub.");
        }

        var candidate = CurrentSession with
        {
            Stage = SessionWorkflowStage.TtsGenerated,
            TtsPath = renderResult.DubTimelinePath,
            MixedDubAudioPath = renderResult.AmbianceMixed ? renderResult.MixedWithAmbiancePath : null,
            TtsVoice = snapshot.Voice,
            TtsGeneratedAtUtc = DateTimeOffset.UtcNow,
            TtsSegmentsPath = snapshot.SegmentsDir,
            TtsSegmentAudioPaths = new Dictionary<string, string>(segmentAudioPaths),
            TtsSegmentDurations = segmentDurations is { Count: > 0 }
                ? new Dictionary<string, double>(segmentDurations)
                : null,
            TtsRuntime = snapshot.Plan.Runtime,
            TtsProvider = snapshot.Plan.ProviderId,
            TtsSettingsDriftedSinceArtifact = settingsDrifted,
            TtsRunId = snapshot.RunId,
            DubTimingMode = snapshot.DefaultTimingMode,
            AmbianceMixDb = snapshot.AmbianceMixDb,
            StatusMessage = renderResult.AmbianceExpected && !renderResult.AmbianceMixed
                ? $"Dub complete ({snapshot.Voice}). Ambiance mix is unavailable; using the dry dub only."
                : settingsDrifted
                    ? $"Dub complete using previous TTS settings. Re-run dub to apply current settings."
                    : $"TTS generated ({snapshot.Voice}). Dubbing complete.",
        };
        await WriteTtsIntegrityArtifactsAsync(
                candidate,
                renderResult.DubTimelinePath,
                renderResult,
                snapshot.SegmentsDir,
                segmentAudioPaths,
                orderedSegments,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (!ArtifactIntegrityValidator.ValidateTts(candidate, out var integrityError))
            throw new InvalidOperationException($"TTS integrity validation failed after commit: {integrityError}");

        lock (_sessionLock)
        {
            CurrentSession = candidate;
        }
        await SaveCurrentSessionAsync().ConfigureAwait(false);

        ReportStage(
            stageContext,
            $"Dub complete. {succeeded}/{totalSegments} segment clips are ready with voice {snapshot.Voice}.",
            progress01: 1,
            isIndeterminate: false);
    }

    private Task<List<TimelineDubSegment>> BuildTimelineDubSegmentsAsync(
        IReadOnlyList<TranslationSegmentArtifact> orderedSegments,
        IReadOnlyDictionary<string, string> segmentAudioPaths,
        CancellationToken cancellationToken) =>
        BuildTimelineDubSegmentsAsync(
            orderedSegments,
            segmentAudioPaths,
            CurrentSession.SegmentTimingModeOverrides,
            CurrentSettings.DubTimingMode,
            cancellationToken);

    private Task<List<TimelineDubSegment>> BuildTimelineDubSegmentsAsync(
        TtsExecutionSnapshot snapshot,
        IReadOnlyList<TranslationSegmentArtifact> orderedSegments,
        IReadOnlyDictionary<string, string> segmentAudioPaths,
        CancellationToken cancellationToken) =>
        BuildTimelineDubSegmentsAsync(
            orderedSegments,
            segmentAudioPaths,
            snapshot.SegmentTimingOverrides,
            snapshot.DefaultTimingMode,
            cancellationToken);

    private async Task<List<TimelineDubSegment>> BuildTimelineDubSegmentsAsync(
        IReadOnlyList<TranslationSegmentArtifact> orderedSegments,
        IReadOnlyDictionary<string, string> segmentAudioPaths,
        IReadOnlyDictionary<string, SegmentTimingMode>? timingOverrides,
        SegmentTimingMode defaultTimingMode,
        CancellationToken cancellationToken)
    {
        var timelineSegments = new List<TimelineDubSegment>(orderedSegments.Count);
        var stretchDir = Path.Combine(GetSessionDirectory(), "tts", "segments", "_timeline");
        Directory.CreateDirectory(stretchDir);

        foreach (var segment in orderedSegments)
        {
            if (string.IsNullOrWhiteSpace(segment.Id))
                continue;
            if (!segmentAudioPaths.TryGetValue(segment.Id, out var sourcePath) || !File.Exists(sourcePath))
                continue;

            var segmentDuration = Math.Max(0.05, segment.End - segment.Start);
            var timingMode = ResolveRenderTimingMode(timingOverrides, defaultTimingMode, segment.Id);
            var effectivePath = sourcePath;

            if (timingMode == SegmentTimingMode.Stretch && _audioProcessingService is not null)
            {
                var stretchedPath = Path.Combine(stretchDir, $"{segment.Id}.stretch.mp3");
                var stretched = await _audioProcessingService.TimeStretchAsync(
                    sourcePath,
                    stretchedPath,
                    segmentDuration,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (stretched && File.Exists(stretchedPath))
                    effectivePath = stretchedPath;
            }

            timelineSegments.Add(new TimelineDubSegment(
                segment.Id,
                effectivePath,
                Math.Max(0, segment.Start),
                segmentDuration,
                TrimToSegmentWindow: true));
        }

        if (timelineSegments.Count == 0)
        {
            throw new InvalidOperationException(
                "No eligible segment audio files were produced for timeline composition.");
        }

        return timelineSegments;
    }

    private SegmentTimingMode ResolveRenderTimingMode(string segmentId)
    {
        return ResolveRenderTimingMode(
            CurrentSession.SegmentTimingModeOverrides,
            CurrentSettings.DubTimingMode,
            segmentId);
    }

    private static SegmentTimingMode ResolveRenderTimingMode(
        IReadOnlyDictionary<string, SegmentTimingMode>? timingOverrides,
        SegmentTimingMode defaultTimingMode,
        string segmentId)
    {
        if (timingOverrides is not null
            && timingOverrides.TryGetValue(segmentId, out var overrideMode))
        {
            if (overrideMode == SegmentTimingMode.Pause)
            {
                return DubTimingDefaults.NormalizeRenderTimingMode(defaultTimingMode);
            }

            return DubTimingDefaults.NormalizeRenderTimingMode(overrideMode);
        }

        return DubTimingDefaults.NormalizeRenderTimingMode(defaultTimingMode);
    }

    /// <summary>
    /// Generates TTS audio for multiple translated segments in a single batch using the Qwen container TTS provider and records produced file paths and durations.
    /// </summary>
    /// <remarks>
    /// Entry state: expects a prepared translation artifact (segments with Id/TranslatedText) and that TTS provider readiness and reference clips (if required) have already been validated by the caller.
    /// Exit state: populates <paramref name="segmentAudioPaths"/> with produced per-segment output paths and, when available, records per-segment durations into <paramref name="segmentDurations"/>; does not itself persist session state.
    /// Cancellation: honors <paramref name="cancellationToken"/> and will propagate OperationCanceledException when cancelled.
    /// Guard conditions: silently returns when there are no valid candidate segments (no Id or translated text). The method logs and continues when individual segment outputs are missing; it does not throw for missing outputs.
    /// </remarks>
    /// <param name="qwenProvider">The Qwen container TTS provider used to perform the batch generation.</param>
    /// <param name="candidateSegments">The list of translation segments to synthesize; only segments with non-empty Id and TranslatedText are processed.</param>
    /// <param name="segmentsDir">Directory where per-segment MP3 files will be written.</param>
    /// <param name="defaultVoice">Default voice to use when a segment does not specify one.</param>
    /// <param name="ttsLanguage">TTS language hint to pass to the provider, or null to omit.</param>
    /// <param name="stageContext">Optional pipeline stage context used for progress reporting.</param>
    /// <param name="segmentAudioPaths">Thread-safe map that will be filled with segmentId -> generated audio file path for successful outputs.</param>
    /// <param name="segmentDurations">Thread-safe map that will be filled with segmentId -> duration in seconds when the provider returns duration metadata.</param>
    /// <summary>
    /// Generates TTS audio for multiple translation segments in a single Qwen batch request and records produced audio file paths.
    /// </summary>
    /// <remarks>
    /// Entry state: caller must supply translation segments (typically loaded from the session's translation artifact); no session stage changes are performed by this method.
    /// Exit state: records any successfully produced segment audio paths into <paramref name="segmentAudioPaths"/>; does not persist session state.
    /// Guard: returns immediately when no eligible segments (non-empty Id and TranslatedText) are present.
    /// Cancellation: honors <paramref name="cancellationToken"/> and will observe cancellation propagated from the provider; provider exceptions and other errors propagate to the caller.
    /// Note: this method records produced audio paths into <paramref name="segmentAudioPaths"/>; it does not populate <paramref name="segmentDurations"/> in the current implementation.
    /// </remarks>
    /// <param name="qwenProvider">The Qwen TTS provider used to generate segments in batch.</param>
    /// <param name="candidateSegments">Translation segments to consider for batch generation; segments with empty Id or TranslatedText are skipped.</param>
    /// <param name="segmentsDir">Directory where per-segment output files will be written (each output is {Id}.mp3).</param>
    /// <param name="defaultVoice">Fallback voice to use when a segment does not specify one.</param>
    /// <param name="ttsLanguage">Optional language hint for TTS generation.</param>
    /// <param name="stageContext">Optional stage context used for progress reporting.</param>
    /// <param name="segmentAudioPaths">Concurrent map that will be populated with segment id → output audio path for successfully generated segments.</param>
    /// <param name="segmentDurations">Concurrent map accepted for durations but not populated by this method.</param>
    /// <param name="cancellationToken">Cancellation token to observe while performing the batch operation.</param>
    private async Task GenerateQwenBatchSegmentAudioAsync(
        TtsExecutionSnapshot snapshot,
        QwenContainerTtsProvider qwenProvider,
        IReadOnlyList<TranslationSegmentArtifact> candidateSegments,
        PipelineStageContext? stageContext,
        ConcurrentDictionary<string, string> segmentAudioPaths,
        ConcurrentDictionary<string, double> segmentDurations,
        CancellationToken cancellationToken)
    {
        var batchRequests = new List<QwenBatchSegmentRequest>(candidateSegments.Count);
        foreach (var segment in candidateSegments)
        {
            var id = segment.Id;
            var text = segment.TranslatedText;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(text))
                continue;

            var outputPath = Path.Combine(snapshot.SegmentsDir, $"{id}.mp3");
            var resolvedVoice = snapshot.ResolveVoiceForSegment(segment);
            var referenceAudioPath = snapshot.ResolveReferenceAudioForSegment(segment);

            batchRequests.Add(new QwenBatchSegmentRequest(
                id,
                text,
                outputPath,
                resolvedVoice,
                segment.SpeakerId,
                referenceAudioPath,
                snapshot.Language,
                snapshot.SourceVideoPath));
        }

        if (batchRequests.Count == 0)
            return;

        _log.Info($"Generating {batchRequests.Count} Qwen batch TTS segments.");
        var generatedPaths = await qwenProvider.GenerateSegmentsAsync(
            batchRequests,
            new Progress<(int Completed, int Total)>(update =>
            {
                ReportStage(
                    stageContext,
                    $"Generated segment clip {update.Completed} of {update.Total}…",
                    progress01: update.Total <= 0 ? 0 : (double)update.Completed / update.Total,
                    isIndeterminate: false);
            }),
            cancellationToken);

        foreach (var batchRequest in batchRequests)
        {
            if (generatedPaths.TryGetValue(batchRequest.SegmentId, out var outputPath) && File.Exists(outputPath))
            {
                segmentAudioPaths[batchRequest.SegmentId] = outputPath;
                _log.Info($"Qwen batch segment TTS generated: {batchRequest.SegmentId} -> {outputPath}");
            }
            else
            {
                _log.Warning($"Qwen batch segment TTS missing output: {batchRequest.SegmentId}");
            }
        }
    }

    /// <summary>
    /// Delegates to <see cref="StreamingPipelineOrchestrator.ExecuteFullPipelineAsync"/>.
    /// <summary>
            /// Runs the full streaming pipeline (transcription, translation, and TTS) via the streaming orchestrator.
            /// </summary>
            /// <remarks>
            /// Entry state: can be invoked from any pipeline stage; the orchestrator will execute the remaining streaming stages as appropriate.
            /// Exit state on success: the session will have progressed through transcription, translation, and TTS stages and end at the TtsGenerated stage when applicable.
            /// Persistence: stage progress and session updates are persisted by the orchestrator as each stage completes.
            /// Cancellation: honors <paramref name="cancellationToken"/> and will observe cancellation requests (may throw <see cref="OperationCanceledException"/>).
            /// </remarks>
            /// <param name="progress">Optional progress reporter receiving values between 0.0 and 1.0 for overall pipeline progress.</param>
            /// <param name="transcriptionStageContext">Optional context for the transcription stage; when provided, it customizes how the transcription stage is executed.</param>
            /// <param name="translationStageContext">Optional context for the translation stage; when provided, it customizes how the translation stage is executed.</param>
            /// <param name="ttsStageContext">Optional context for the TTS stage; when provided, it customizes how the TTS stage is executed.</param>
            /// <param name="cancellationToken">Token to observe for cancellation.</param>
            /// <returns>A task that completes when the orchestrator finishes executing the full streaming pipeline.</returns>
    private Task ExecuteStreamingPipelineAsync(
        IProgress<double>? progress,
        PipelineStageContext? transcriptionStageContext,
        PipelineStageContext? translationStageContext,
        PipelineStageContext? ttsStageContext,
        CancellationToken cancellationToken) =>
        _streamingPipelineOrchestrator.ExecuteFullPipelineAsync(
            progress,
            transcriptionStageContext,
            translationStageContext,
            ttsStageContext,
            cancellationToken);

    /// <summary>
    /// Delegates to <see cref="StreamingPipelineOrchestrator.ExecuteTranslationAndTtsFromTranscriptAsync"/>.
    /// <summary>
            /// Runs a streaming pipeline that translates the existing transcript and generates corresponding TTS output from that transcript.
            /// </summary>
            /// <param name="progress">Optional progress reporter for overall pipeline progress.</param>
            /// <param name="translationStageContext">Optional stage context to control translation stage behavior (e.g., target/override language); may be null.</param>
            /// <param name="ttsStageContext">Optional stage context to control TTS stage behavior (e.g., target voice); may be null.</param>
            /// <param name="cancellationToken">Cancellation token to cancel the operation; cancellation is honored and will propagate.</param>
            /// <returns>Completes when translation and TTS generation finish and the session has been advanced to the TtsGenerated stage.</returns>
            /// <remarks>
            /// Entry state: expects a transcript to be available (session at or beyond the Transcribed stage).  
            /// On success: advances session state to include translation and generated TTS artifacts (TtsGenerated).  
            /// Persistence: session state for translation and TTS is persisted by the pipeline/orchestrator on successful completion.  
            /// Guard conditions: throws <see cref="InvalidOperationException"/> if a required transcript is not available; throws <see cref="OperationCanceledException"/> when cancelled; other exceptions propagate from the orchestrator.
            /// </remarks>
    private Task ExecuteStreamingTranslationAndTtsFromTranscriptAsync(
        IProgress<double>? progress,
        PipelineStageContext? translationStageContext,
        PipelineStageContext? ttsStageContext,
        CancellationToken cancellationToken) =>
        _streamingPipelineOrchestrator.ExecuteTranslationAndTtsFromTranscriptAsync(
            progress,
            translationStageContext,
            ttsStageContext,
            cancellationToken);

    /// <summary>
        /// Advances the session pipeline from its current stage through any remaining stages (transcription, diarization, translation, and/or TTS) according to the pipeline state machine and current settings.
        /// </summary>
        /// <remarks>
        /// Entry state: any valid session stage; the method determines the next actions based on the current <see cref="CurrentSession.Stage"/> and whether diarization is enabled.
        /// Success state: the session is advanced to the next completed stage(s) determined by the state machine; the method persists session state for stages it commits.
        /// Cancellation: honors <paramref name="cancellationToken"/> and will throw <see cref="OperationCanceledException"/> when cancelled.
        /// </remarks>
        /// <param name="progress">Optional overall progress reporter for the pipeline advance.</param>
        /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
        /// <returns>A task that completes when pipeline advancement finishes.</returns>
        public Task AdvancePipelineAsync(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        AdvancePipelineAsync(progress, stageProgress: null, cancellationToken);

    /// <summary>
    /// Advances the pipeline from the current session stage through transcription, optional diarization, translation, and TTS.
    /// </summary>
    /// <param name="progress">Optional combined progress reporter for the overall advance operation.</param>
    /// <param name="stageProgress">Optional per-stage reporter that receives stage title/detail and per-stage progress updates.</param>
    /// <param name="cancellationToken">Cancellation token observed throughout stage execution.</param>
    /// <remarks>
    /// Entry starts at <see cref="CurrentSession"/>.Stage. When diarization is enabled, advance stops at
    /// <see cref="SessionWorkflowStage.Diarized"/> for speaker review; call <see cref="ContinuePipelineAsync"/> for
    /// translation and TTS. When diarization is off, runs continue through translation and TTS. Depending on cancellation or
    /// prior stage state, possible return stages include <see cref="SessionWorkflowStage.Transcribed"/>,
    /// <see cref="SessionWorkflowStage.Diarized"/>, <see cref="SessionWorkflowStage.Translated"/>, and
    /// <see cref="SessionWorkflowStage.TtsGenerated"/>. State changes are persisted by the invoked stage methods
    /// (for example via <see cref="SaveCurrentSession"/>). Cancellation is respected and propagated via
    /// <paramref name="cancellationToken"/>.
    /// <summary>
    /// Advances the session pipeline from its current stage through the remaining stages according to the pipeline state machine.
    /// </summary>
    /// <remarks>
    /// Entry state: the method expects CurrentSession.Stage to reflect the pipeline's current persisted stage.
    /// On success: the session will be advanced to the next terminal or intermediate stage(s) determined by the state machine; individual stage handlers invoked by this method are responsible for updating and persisting CurrentSession as they complete.
    /// Behavior: if the state machine indicates the full streaming pipeline should run first, the method delegates to the streaming orchestrator; otherwise it drives stage-by-stage advancement using the state machine decisions. The method may return early after initiating streaming translation+TTS or TTS-only actions when those paths are chosen.
    /// </remarks>
    /// <param name="progress">Optional overall progress reporter for long-running operations; may be null.</param>
    /// <param name="stageProgress">Optional per-stage progress reporter used to report stage messages and progress; may be null.</param>
    /// <param name="cancellationToken">Cancellation token that will be observed; the method throws <see cref="OperationCanceledException"/> when cancellation is requested.</param>
    /// <returns>A task that completes when the pipeline advancement (and any delegated orchestration it synchronously awaits) has finished.</returns>
    /// <exception cref="OperationCanceledException">Thrown if <paramref name="cancellationToken"/> is canceled during execution.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the pipeline state machine returns an unexpected advance action.</exception>
    internal async Task AdvancePipelineAsync(
        IProgress<double>? progress = null,
        IProgress<PipelineStageUpdate>? stageProgress = null,
        CancellationToken cancellationToken = default)
    {
        var shouldRunDiarization = ShouldRunDiarization();
        var remainingStages = GetAdvancePipelineStages(CurrentSession.Stage, shouldRunDiarization);

        if (PipelineStateMachine.ShouldRunFullStreamingPipelineFirst(CurrentSession.Stage, shouldRunDiarization))
        {
            await ExecuteStreamingPipelineAsync(
                progress,
                GetStageContext(remainingStages, SessionWorkflowStage.Transcribed, stageProgress),
                GetStageContext(remainingStages, SessionWorkflowStage.Translated, stageProgress),
                GetStageContext(remainingStages, SessionWorkflowStage.TtsGenerated, stageProgress),
                cancellationToken);
            return;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stageBeforeAction = CurrentSession.Stage;
            var action = PipelineStateMachine.GetNextAdvanceAction(stageBeforeAction, shouldRunDiarization);
            switch (action)
            {
                case null:
                    return;
                case PipelineAdvanceAction.Transcribe:
                    await TranscribeMediaAsync(
                        progress,
                        GetStageContext(remainingStages, SessionWorkflowStage.Transcribed, stageProgress),
                        cancellationToken);
                    if (CurrentSession.Stage <= stageBeforeAction)
                        throw new InvalidOperationException($"Pipeline stalled: stage did not advance after {action} (still at {CurrentSession.Stage}).");
                    break;
                case PipelineAdvanceAction.Diarize:
                    await _diarizationStageOrchestrator.ExecuteAsync(
                        GetStageContext(remainingStages, SessionWorkflowStage.Diarized, stageProgress) is { } stageContext
                            ? stageContext.ToShared()
                            : null,
                        cancellationToken);
                    if (CurrentSession.Stage <= stageBeforeAction)
                        throw new InvalidOperationException($"Pipeline stalled: stage did not advance after {action} (still at {CurrentSession.Stage}).");
                    // Pause for speaker review; ContinuePipelineAsync resumes translation + dub.
                    return;
                case PipelineAdvanceAction.TranslateAndDubFromTranscript:
                    await ExecuteStreamingTranslationAndTtsFromTranscriptAsync(
                        progress,
                        GetStageContext(remainingStages, SessionWorkflowStage.Translated, stageProgress),
                        GetStageContext(remainingStages, SessionWorkflowStage.TtsGenerated, stageProgress),
                        cancellationToken);
                    if (CurrentSession.Stage <= stageBeforeAction)
                        throw new InvalidOperationException($"Pipeline stalled: stage did not advance after {action} (still at {CurrentSession.Stage}).");
                    return;
                case PipelineAdvanceAction.GenerateTts:
                    await GenerateTtsAsync(
                        progress,
                        null,
                        GetStageContext(remainingStages, SessionWorkflowStage.TtsGenerated, stageProgress),
                        cancellationToken);
                    if (CurrentSession.Stage <= stageBeforeAction)
                        throw new InvalidOperationException($"Pipeline stalled: stage did not advance after {action} (still at {CurrentSession.Stage}).");
                    return;
                default:
                    throw new InvalidOperationException($"Unexpected pipeline advance action: {action}");
            }
        }
    }

    /// <summary>
    /// Continues pipeline execution after diarization by advancing through translation and TTS as needed.
    /// </summary>
    /// <param name="progress">Optional overall progress reporter for remaining continuation stages.</param>
    /// <param name="cancellationToken">Cancellation token used to stop continuation before completion.</param>
    /// <remarks>
    /// Requires <see cref="CurrentSession"/>.Stage to be at least <see cref="SessionWorkflowStage.Diarized"/>.
    /// Depending on the current stage, this operation may advance to <see cref="SessionWorkflowStage.Translated"/>
    /// and then <see cref="SessionWorkflowStage.TtsGenerated"/>. Stage transitions persist via stage methods
    /// that call <see cref="SaveCurrentSession"/> after successful completion.
    /// </remarks>
    public Task ContinuePipelineAsync(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        ContinuePipelineAsync(progress, stageProgress: null, cancellationToken);

    /// <summary>
    /// Continues pipeline execution after diarization using stage-aware progress reporting.
    /// </summary>
    /// <param name="progress">Optional overall progress reporter for remaining continuation stages.</param>
    /// <param name="stageProgress">Optional per-stage progress/status updates for translation and TTS stages.</param>
    /// <param name="cancellationToken">Cancellation token used to stop continuation before completion.</param>
    /// <remarks>
    /// Entry requires stage <see cref="SessionWorkflowStage.Diarized"/> or later. This method advances the
    /// session toward <see cref="SessionWorkflowStage.TtsGenerated"/> by running translation when below
    /// <see cref="SessionWorkflowStage.Translated"/> and then running TTS when below
    /// <see cref="SessionWorkflowStage.TtsGenerated"/>. Successful stage completions persist updates to
    /// <see cref="CurrentSession"/>. Cancellation propagates via <paramref name="cancellationToken"/>.
    /// <summary>
    /// Continues the pipeline from a diarized session toward translation and/or TTS according to the pipeline state machine.
    /// </summary>
    /// <remarks>
    /// Entry requirements: CurrentSession.Stage must be at or after <see cref="SessionWorkflowStage.Diarized"/>; otherwise an <see cref="InvalidOperationException"/> is thrown.
    /// On success:
    /// - If the state machine chooses <see cref="PipelineAdvanceAction.TranslateAndDubFromTranscript"/>, the method advances through translation and TTS, leaving the session at <see cref="SessionWorkflowStage.TtsGenerated"/> (translation and TTS stage contexts are used as provided).
    /// - If the state machine chooses <see cref="PipelineAdvanceAction.GenerateTts"/>, the method advances to <see cref="SessionWorkflowStage.TtsGenerated"/>.
    /// - If there is no continuation action, the session stage is unchanged.
    /// Persistence: this method does not directly persist session state; the invoked orchestrators/stage handlers are responsible for persisting changes.
    /// Cancellation: respects <paramref name="cancellationToken"/> and will observe cancellation propagated to the invoked orchestrators.
    /// </remarks>
    /// <param name="progress">Optional overall numeric progress reporter (0.0–1.0) used by invoked orchestrators.</param>
    /// <param name="stageProgress">Optional per-stage progress reporter used to build stage contexts for downstream orchestrators.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the continuation operation.</param>
    /// <returns>Completion of the pipeline continuation operation.</returns>
    internal async Task ContinuePipelineAsync(
        IProgress<double>? progress = null,
        IProgress<PipelineStageUpdate>? stageProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (CurrentSession.Stage < SessionWorkflowStage.Diarized)
            throw new InvalidOperationException("Speaker mapping is not ready yet. Run the pipeline through diarization first.");

        var remainingStages = GetContinuationPipelineStages(CurrentSession.Stage);

        var continuationStageBeforeAction = CurrentSession.Stage;
        switch (PipelineStateMachine.GetContinuationActionAfterDiarized(CurrentSession.Stage))
        {
            case null:
                return;
            case PipelineAdvanceAction.TranslateAndDubFromTranscript:
                await ExecuteStreamingTranslationAndTtsFromTranscriptAsync(
                    progress,
                    GetStageContext(remainingStages, SessionWorkflowStage.Translated, stageProgress),
                    GetStageContext(remainingStages, SessionWorkflowStage.TtsGenerated, stageProgress),
                    cancellationToken);
                if (CurrentSession.Stage <= continuationStageBeforeAction)
                    throw new InvalidOperationException($"Pipeline stalled: stage did not advance after {PipelineAdvanceAction.TranslateAndDubFromTranscript} (still at {CurrentSession.Stage}).");
                return;
            case PipelineAdvanceAction.GenerateTts:
                await GenerateTtsAsync(
                    progress,
                    null,
                    GetStageContext(remainingStages, SessionWorkflowStage.TtsGenerated, stageProgress),
                    cancellationToken);
                if (CurrentSession.Stage <= continuationStageBeforeAction)
                    throw new InvalidOperationException($"Pipeline stalled: stage did not advance after {PipelineAdvanceAction.GenerateTts} (still at {CurrentSession.Stage}).");
                return;
            default:
                throw new InvalidOperationException("Unexpected continuation pipeline action.");
        }
    }

    /// <summary>
    /// Runs only the TTS stage for an already translated session.
    /// </summary>
    /// <param name="progress">Optional progress reporter for TTS stage execution.</param>
    /// <param name="voice">Optional voice override; when null the configured session/provider voice is used.</param>
    /// <param name="cancellationToken">Cancellation token used to stop TTS generation before completion.</param>
    /// <remarks>
    /// Requires <see cref="CurrentSession"/>.Stage to be at least <see cref="SessionWorkflowStage.Translated"/>.
    /// On success, advances and persists the session to <see cref="SessionWorkflowStage.TtsGenerated"/>.
    /// </remarks>
    public Task RunTtsOnlyAsync(
        IProgress<double>? progress = null,
        string? voice = null,
        CancellationToken cancellationToken = default) =>
        RunTtsOnlyAsync(progress, voice, stageProgress: null, cancellationToken);

    /// <summary>
    /// Runs only the TTS stage for an already translated session with stage-aware progress updates.
    /// </summary>
    /// <param name="progress">Optional progress reporter for TTS stage execution.</param>
    /// <param name="voice">Optional voice override; when null the configured session/provider voice is used.</param>
    /// <param name="stageProgress">Optional stage progress updates describing TTS stage activity.</param>
    /// <param name="cancellationToken">Cancellation token used to stop TTS generation before completion.</param>
    /// <remarks>
    /// Entry requires stage <see cref="SessionWorkflowStage.Translated"/> or later. This method executes only
    /// TTS and advances toward terminal stage <see cref="SessionWorkflowStage.TtsGenerated"/>; persistence occurs
    /// when TTS completes and updates <see cref="CurrentSession"/>. Cancellation propagates via
    /// <paramref name="cancellationToken"/>.
    /// <summary>
    /// Runs only the TTS stage using the existing translation; requires a translated session and results in the session reaching the TtsGenerated stage on success.
    /// </summary>
    /// <param name="progress">Optional overall progress reporter for the TTS pipeline.</param>
    /// <param name="voice">Optional voice override to use for synthesis.</param>
    /// <param name="stageProgress">Optional per-stage progress updates.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <exception cref="InvalidOperationException">Thrown if the current session has not been translated (i.e., <see cref="SessionWorkflowStage"/> is less than <c>Translated</c>).</exception>
    /// <remarks>
    /// Entry state: requires <see cref="CurrentSession.Stage"/> >= <c>Translated</c>.
    /// Exit state on success: <see cref="CurrentSession.Stage"/> will be set to <c>TtsGenerated</c> and session state will be persisted.
    /// Cancellation: honors <paramref name="cancellationToken"/> and may throw <see cref="OperationCanceledException"/> if canceled.
    /// </remarks>
    internal async Task RunTtsOnlyAsync(
        IProgress<double>? progress,
        string? voice,
        IProgress<PipelineStageUpdate>? stageProgress,
        CancellationToken cancellationToken)
    {
        if (CurrentSession.Stage < SessionWorkflowStage.Translated)
            throw new InvalidOperationException("No translation is available. Continue the pipeline through translation first.");

        var remainingStages = GetContinuationPipelineStages(CurrentSession.Stage);
        await GenerateTtsAsync(
            progress,
            voice,
            GetStageContext(remainingStages, SessionWorkflowStage.TtsGenerated, stageProgress),
            cancellationToken);
    }

    /// <summary>
        /// Determines whether diarization is enabled in the current session settings.
        /// </summary>
        /// <returns>`true` if the current settings contain a non-empty diarization provider identifier; `false` otherwise.</returns>
        private bool ShouldRunDiarization() =>
        !string.IsNullOrWhiteSpace(CurrentSettings.DiarizationProvider);

    private static string NormalizePipelineLanguage(string? raw, string nonNormalizedFallback)
    {
        var n = LanguageCode.NormalizeForPersistence(raw);
        if (n is not null) return n;
        if (string.IsNullOrWhiteSpace(raw)) return nonNormalizedFallback;
        return raw.Trim();
    }

    /// <summary>
    /// Re-run transcription, optionally continuing through diarization (if enabled), translation, and TTS.
    /// </summary>
    internal async Task RerunTranscriptionAsync(
        bool remainingDownstream,
        IProgress<PipelineStageUpdate>? stageProgress,
        CancellationToken cancellationToken)
    {
        ResetPipelineToMediaLoaded();
        SaveCurrentSession();

        if (remainingDownstream)
        {
            await AdvancePipelineAsync(null, stageProgress, cancellationToken).ConfigureAwait(false);
            return;
        }

        var singleStage = new[] { SessionWorkflowStage.Transcribed };
        await TranscribeMediaAsync(
            null,
            GetStageContext(singleStage, SessionWorkflowStage.Transcribed, stageProgress),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-run diarization; optionally continue with translation and TTS afterward.
    /// </summary>
    internal async Task RerunDiarizationAsync(
        bool remainingDownstream,
        IProgress<PipelineStageUpdate>? stageProgress,
        CancellationToken cancellationToken)
    {
        var hadTranslatableOutput = CurrentSession.Stage >= SessionWorkflowStage.Translated;
        var speakerAssignmentsChanged = await RunDiarizationAsync(cancellationToken).ConfigureAwait(false);

        if (!remainingDownstream)
        {
            if (speakerAssignmentsChanged && hadTranslatableOutput)
            {
                ResetPipelineToTranslated();
                SaveCurrentSession();
            }

            return;
        }

        if (HasDiarizationMarker(CurrentSession))
        {
            ResetPipelineToDiarized();
            SaveCurrentSession();
            await ContinuePipelineAsync(null, stageProgress, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ResetPipelineToTranscribed();
            SaveCurrentSession();
            await AdvancePipelineAsync(null, stageProgress, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Re-run translation from the current transcript; optionally continue with TTS.
    /// </summary>
    internal async Task RerunTranslationAsync(
        bool remainingDownstream,
        IProgress<PipelineStageUpdate>? stageProgress,
        CancellationToken cancellationToken)
    {
        ResetPipelineForTranslationRetry();

        if (remainingDownstream)
        {
            if (CurrentSession.Stage >= SessionWorkflowStage.Diarized)
                await ContinuePipelineAsync(null, stageProgress, cancellationToken).ConfigureAwait(false);
            else
                await AdvancePipelineAsync(null, stageProgress, cancellationToken).ConfigureAwait(false);
            return;
        }

        var singleStage = new[] { SessionWorkflowStage.Translated };
        await TranslateTranscriptAsync(
            null,
            null,
            null,
            GetStageContext(singleStage, SessionWorkflowStage.Translated, stageProgress),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-generate dub (TTS) from the current translation artifact.
    /// </summary>
    internal async Task RerunDubAsync(
        IProgress<PipelineStageUpdate>? stageProgress,
        CancellationToken cancellationToken)
    {
        ResetPipelineToTranslated();
        SaveCurrentSession();
        await RunTtsOnlyAsync(null, null, stageProgress, cancellationToken).ConfigureAwait(false);
    }
}
