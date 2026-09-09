using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Registries;

namespace Babel.Player.Services;

public sealed partial class SessionWorkflowCoordinator
{
    private static readonly JsonSerializerOptions DebugJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string DebugLogPath = ResolveDebugLogPath();

    private static void WriteDebugLog(string runId, string hypothesisId, string location, string message, object data)
    {
        var payload = new
        {
            sessionId = "f76224",
            runId,
            hypothesisId,
            location,
            message,
            data,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        try
        {
            var line = JsonSerializer.Serialize(payload, DebugJsonOptions);
            File.AppendAllText(DebugLogPath, line + Environment.NewLine);
        }
        catch
        {
            // Swallow debug log failures.
        }
    }

    private static string ResolveDebugLogPath()
    {
        var envPath = Environment.GetEnvironmentVariable("BABEL_DEBUG_LOG_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
            return envPath;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Babel-Player.sln")))
                return Path.Combine(dir.FullName, "debug-f76224.log");
            dir = dir.Parent;
        }

        return Path.Combine(Environment.CurrentDirectory, "debug-f76224.log");
    }

    // ── Diarization ──────────────────────────────────────────────────────

    private readonly record struct DiarizationExecutionOutcome(
        bool SpeakerAssignmentsChanged,
        int SpeakerCount,
        int SegmentCount);

    /// <summary>
    /// Runs diarization on the current session's ingested media and merges detected speaker assignments into the transcript
    /// and optional translation, updating session state on success.
    /// </summary>
    /// <remarks>
    /// Entry stage: the session must be at or beyond <see cref="SessionWorkflowStage.Transcribed"/>.
    /// Exit stage: advances to <see cref="SessionWorkflowStage.Diarized"/> unless the session is already at or beyond
    /// <see cref="SessionWorkflowStage.Translated"/>, in which case the existing stage is preserved.
    /// Session state changes are persisted via <c>SaveCurrentSession()</c>.
    /// </remarks>
    /// <param name="cancellationToken">Token to cancel the diarization operation.</param>
    /// <returns><see langword="true"/> if speaker assignments were changed in the transcript or translation; <see langword="false"/> otherwise.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the session is not at or beyond the Transcribed stage, when required session paths are missing or empty,
    /// or when no diarization provider is selected.
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// Thrown when the ingested media file or the transcript file does not exist on disk.
    /// </exception>
    public async Task<bool> RunDiarizationAsync(CancellationToken cancellationToken = default)
    {
#pragma warning disable MVVMTK0034 // snapshot read avoids generated-property design-time false negatives in some IDE states
        var currentSession = _currentSession;
#pragma warning restore MVVMTK0034

        if (currentSession.Stage < SessionWorkflowStage.Transcribed)
            throw new InvalidOperationException("No transcript available. Please transcribe media first.");

        if (string.IsNullOrWhiteSpace(currentSession.IngestedMediaPath))
            throw new InvalidOperationException("No ingested media is available for diarization.");

        if (!File.Exists(currentSession.IngestedMediaPath))
            throw new FileNotFoundException($"Ingested media file not found: {currentSession.IngestedMediaPath}");

        if (string.IsNullOrWhiteSpace(currentSession.TranscriptPath))
            throw new InvalidOperationException("No transcript available. Please transcribe media first.");

        if (!File.Exists(currentSession.TranscriptPath))
            throw new FileNotFoundException($"Transcript file not found: {currentSession.TranscriptPath}");

        if (string.IsNullOrWhiteSpace(CurrentSettings.DiarizationProvider))
            throw new InvalidOperationException("No diarization provider is selected.");

        var outcome = await ExecuteDiarizationAsync(
            currentSession.IngestedMediaPath,
            currentSession.TranscriptPath,
            cancellationToken,
            resultingStage: currentSession.Stage >= SessionWorkflowStage.Translated
                ? currentSession.Stage
                : SessionWorkflowStage.Diarized);

        return outcome.SpeakerAssignmentsChanged;
    }

    /// <summary>
    /// Executes speaker diarization for the specified audio file and merges detected speaker assignments into the transcript
    /// and optional translation artifacts, then advances and persists session state.
    /// </summary>
    /// <remarks>
    /// Entry stage: the session must already be at or beyond <see cref="SessionWorkflowStage.Transcribed"/> — callers are
    /// responsible for enforcing this precondition before invoking this method.
    /// Exit stage: defaults to <see cref="SessionWorkflowStage.Diarized"/> (or preserves the current stage when it is
    /// already at or beyond <see cref="SessionWorkflowStage.Translated"/>); supplying <paramref name="resultingStage"/>
    /// overrides this default entirely.
    /// Session state changes are persisted by <c>SaveCurrentSession()</c> before this method returns.
    /// </remarks>
    /// <param name="audioPath">Filesystem path to the source audio to diarize.</param>
    /// <param name="transcriptPath">Filesystem path to the transcript file to update with diarization speaker IDs.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    /// <param name="resultingStage">
    /// Optional stage to assign to the session after successful diarization.
    /// When <see langword="null"/>, the exit stage is computed automatically (see remarks).
    /// </param>
    /// <param name="statusMessage">Optional status message to record on the session; a context-appropriate default is used when <see langword="null"/>.</param>
    /// <returns>
    /// A <see cref="DiarizationExecutionOutcome"/> containing whether speaker assignments were applied to transcript/translation,
    /// the detected speaker count, and the diarized segment count.
    /// </returns>
    private static readonly string[] VideoExtensions = [".mp4", ".avi", ".mkv", ".mov"];

    /// <summary>
    /// True when the diarization input must be rewritten to 16 kHz mono PCM WAV before the provider runs.
    /// SortFormer always normalizes (including .wav) because DecodePcm16Mono only accepts 16-bit PCM.
    /// </summary>
    internal static bool RequiresPcmWavExtractForDiarization(string audioPath, string? diarizationProvider)
    {
        if (string.Equals(diarizationProvider, ProviderNames.SortFormerLocal, StringComparison.Ordinal))
            return true;

        var extension = Path.GetExtension(audioPath).ToLowerInvariant();
        return Array.Exists(VideoExtensions, ext => ext == extension);
    }

    /// <summary>
    /// Runs diarization for the given audio/transcript, merges speaker assignments into transcript (and translation if present), advances and persists session stage, and returns the execution outcome.
    /// </summary>
    /// <remarks>
    /// Expected entry state: the session must already have a transcribed transcript available (caller responsibility). On success the session <see cref="SessionWorkflowStage"/> is advanced to <see cref="SessionWorkflowStage.Diarized"/> unless a different <paramref name="resultingStage"/> is provided or the session is already at or beyond <c>Translated</c>. This method persists the updated session via <c>SaveCurrentSession()</c>. The operation observes <paramref name="ct"/> for cancellation and will fail fast if the configured diarization registry or provider readiness checks fail.
    /// </remarks>
    /// <param name="audioPath">Path to the source audio (or video) file to diarize. Video containers will have audio extracted to a temporary WAV file for processing.</param>
    /// <param name="transcriptPath">Path to the transcript JSON file into which speaker assignments will be merged.</param>
    /// <param name="ct">Cancellation token to observe for the diarization operation.</param>
    /// <param name="resultingStage">Optional explicit stage to set on success; if omitted, the stage is set to <c>Diarized</c> unless the session is already at or beyond <c>Translated</c>.</param>
    /// <param name="statusMessage">Optional status message to set on the session; if omitted a default message is selected based on the resulting stage.</param>
    /// <returns>
    /// A <see cref="DiarizationExecutionOutcome"/> describing whether speaker assignments changed, the detected speaker count, and the number of diarized segments.
    /// </returns>
    /// <exception cref="PipelineProviderException">Thrown when no diarization registry is configured, when audio processing is required but unavailable for video inputs, or when the selected diarization provider is not ready to execute.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the diarization provider reports an unsuccessful result.</exception>
    private async Task<DiarizationExecutionOutcome> ExecuteDiarizationAsync(
        string audioPath,
        string transcriptPath,
        CancellationToken ct,
        SessionWorkflowStage? resultingStage = null,
        string? statusMessage = null)
    {
        var totalStopwatch = Stopwatch.StartNew();
        if (DiarizationRegistry is null)
            throw new PipelineProviderException("No diarization registry is configured.");

        var providerDescriptor = DiarizationRegistry
            .GetAvailableProviders()
            .FirstOrDefault(provider => string.Equals(provider.Id, CurrentSettings.DiarizationProvider, StringComparison.Ordinal));
        var usesContainerizedRuntime = providerDescriptor?.EffectiveDefaultRuntime == InferenceRuntime.Containerized;

        // Force provider-level auto speaker-count detection and ignore legacy min/max bounds.
        int? effectiveMinSpeakers = null;
        int? effectiveMaxSpeakers = null;

        // Normalize containers SortFormer (and other PCM-WAV consumers) cannot decode.
        // Video always needs extract; non-WAV audio does too when the active provider is SortFormer.
        var effectiveAudioPath = audioPath;
        string? tempExtractedAudio = null;
        var isVideo = Array.Exists(
            VideoExtensions,
            ext => string.Equals(ext, Path.GetExtension(audioPath), StringComparison.OrdinalIgnoreCase));
        var requiresPcmWav = RequiresPcmWavExtractForDiarization(audioPath, CurrentSettings.DiarizationProvider);

        if (requiresPcmWav)
        {
            if (_audioProcessingService is null)
                throw new PipelineProviderException(
                    isVideo
                        ? "Cannot diarize video files without audio processing support (ffmpeg)."
                        : "Cannot diarize with SortFormer without audio processing support (ffmpeg).");

            tempExtractedAudio = Path.Combine(Path.GetTempPath(), $"diar_{Guid.NewGuid():N}.wav");
            _log.Debug($"Extracting PCM WAV for diarization: {audioPath} → {tempExtractedAudio}");
            await _audioProcessingService.ExtractFullAudioAsync(audioPath, tempExtractedAudio, ct)
                .ConfigureAwait(false);
            effectiveAudioPath = tempExtractedAudio;
        }

        IDiarizationProvider? provider = null;
        try
        {
            ProviderReadiness readiness;

            if (usesContainerizedRuntime)
            {
                readiness = ContainerizedProbe is not null
                    ? await ContainerizedProviderReadiness.CheckDiarizationForExecutionAsync(
                            CurrentSettings,
                            CurrentSettings.DiarizationProvider,
                            ContainerizedProbe,
                            ct)
                        .ConfigureAwait(false)
                    : DiarizationRegistry.CheckReadiness(CurrentSettings.DiarizationProvider, CurrentSettings, KeyStore);


                if (!readiness.IsReady)
                {
                    var blockingReason = readiness.BlockingReason ?? "Diarization provider is not ready.";
                    _log.Warning($"Diarization skipped: {blockingReason}");
                    throw new PipelineProviderException(blockingReason);
                }

                provider = DiarizationRegistry.CreateProvider(CurrentSettings.DiarizationProvider, CurrentSettings, KeyStore);
            }
            else
            {
                provider = DiarizationRegistry.CreateProvider(CurrentSettings.DiarizationProvider, CurrentSettings, KeyStore);
                var ensuredReady = await provider.EnsureReadyAsync(CurrentSettings, ct: ct).ConfigureAwait(false);
                readiness = provider.CheckReadiness(CurrentSettings, KeyStore);


                if (!readiness.IsReady)
                {
                    var blockingReason = readiness.BlockingReason ?? "Diarization provider is not ready.";
                    _log.Warning($"Diarization skipped: {blockingReason}");
                    throw new PipelineProviderException(blockingReason);
                }
            }

            var request = new DiarizationRequest(
                SourceAudioPath:  effectiveAudioPath,
                MinSpeakers:      effectiveMinSpeakers,
                MaxSpeakers:      effectiveMaxSpeakers);

            _log.Debug($"Running diarization: provider={CurrentSettings.DiarizationProvider}, audio={effectiveAudioPath}, " +
                      $"minSpeakers={effectiveMinSpeakers?.ToString() ?? "auto"}, " +
                      $"maxSpeakers={effectiveMaxSpeakers?.ToString() ?? "auto"}");
            var providerCallStopwatch = Stopwatch.StartNew();
            var result = await _inferenceEngine.DiarizeAsync(provider, request, ct);
            providerCallStopwatch.Stop();

            if (!result.Success)
            {
                _log.Warning($"Diarization failed: {result.ErrorMessage}");
                throw new InvalidOperationException(result.ErrorMessage ?? "Diarization provider returned an unsuccessful result.");
            }

            var transcriptMergeStopwatch = Stopwatch.StartNew();
            var transcriptChanged = await MergeDiarizationIntoTranscriptAsync(transcriptPath, result.Segments, ct);
            transcriptMergeStopwatch.Stop();
            var translationChanged = false;
#pragma warning disable MVVMTK0034 // snapshot read avoids generated-property design-time false negatives in some IDE states
            var currentSession = _currentSession;
#pragma warning restore MVVMTK0034

            if (!string.IsNullOrWhiteSpace(currentSession.TranslationPath) &&
                File.Exists(currentSession.TranslationPath))
            {
                var translationMergeStopwatch = Stopwatch.StartNew();
                translationChanged = await MergeSpeakerIdsIntoTranslationAsync(
                    transcriptPath,
                    currentSession.TranslationPath,
                    ct);
                translationMergeStopwatch.Stop();
            }

            var nextStage = resultingStage ?? (
                currentSession.Stage >= SessionWorkflowStage.Translated
                    ? currentSession.Stage
                    : SessionWorkflowStage.Diarized);
            var nextStatusMessage = statusMessage ?? "Speaker mapping ready. Review speakers, then continue.";

            lock (_sessionLock)
            {
                CurrentSession = CurrentSession with
                {
                    Stage = nextStage,
                    DiarizationProvider = CurrentSettings.DiarizationProvider,
                    SpeakersDetectedAtUtc = DateTimeOffset.UtcNow,
                    StatusMessage = nextStatusMessage,
                };
            }
            SaveCurrentSession();

            _log.Info($"Diarization complete: {result.SpeakerCount} speakers across {result.Segments.Count} segments.");

            return new DiarizationExecutionOutcome(
                SpeakerAssignmentsChanged: transcriptChanged || translationChanged,
                SpeakerCount: result.SpeakerCount,
                SegmentCount: result.Segments.Count);
        }
        finally
        {
            totalStopwatch.Stop();
            if (provider is IDisposable disposableProvider)
            {
                try
                {
                    disposableProvider.Dispose();
                }
                catch (Exception ex)
                {
                    _log.Warning($"Failed to dispose diarization provider: {ex.Message}");
                }
            }

            if (tempExtractedAudio is not null)
            {
                try
                {
                    if (File.Exists(tempExtractedAudio))
                        File.Delete(tempExtractedAudio);
                }
                catch (Exception ex)
                {
                    _log.Warning($"Failed to clean up temp diarization audio: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Merges diarization speaker assignments into an existing transcript file, updating segment speaker IDs when they change.
    /// </summary>
    /// <param name="transcriptPath">Path to the transcript JSON file to read and potentially overwrite.</param>
    /// <param name="diarizedSegments">List of diarized speaker segments used to assign speaker IDs to transcript segments.</param>
    /// <param name="ct">Cancellation token to observe during I/O operations.</param>
    /// <returns>`true` if the transcript file was modified and written back to disk, `false` if no speaker assignments changed or the transcript had no segments.</returns>
    private async Task<bool> MergeDiarizationIntoTranscriptAsync(
        string transcriptPath,
        IReadOnlyList<DiarizedSegment> diarizedSegments,
        CancellationToken ct)
    {
        var artifact = await ArtifactJson.LoadTranscriptAsync(transcriptPath, ct);
        if (artifact.Segments is null) return false;

        var before = CaptureTranscriptSpeakerAssignments(artifact.Segments);

        var result = new List<TranscriptSegmentArtifact>();
        foreach (var seg in artifact.Segments)
            result.AddRange(SplitSegmentAtSpeakerBoundaries(seg, diarizedSegments));

        var changed = !before.SequenceEqual(CaptureTranscriptSpeakerAssignments(result));
        if (!changed)
            return false;

        artifact.Segments.Clear();
        artifact.Segments.AddRange(result);

        await ArtifactPersistence.AtomicWriteTextAsync(
                transcriptPath,
                ArtifactJson.SerializeTranscript(artifact),
                ct)
            .ConfigureAwait(false);
        await WriteTranscriptManifestAsync(transcriptPath, artifact, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Splits a transcript segment into one or more segments aligned to diarized speaker turns.
    /// </summary>
    /// <param name="segment">The transcript segment to split; its SpeakerId may be updated when no split is required.</param>
    /// <param name="diarized">A list of diarized speaker turns used to determine speaker boundaries and assignments.</param>
    /// <returns>
    /// A list of transcript segments covering the same time span as the input segment. If no speaker boundary requires splitting, the returned list contains the original segment (with SpeakerId set). If splits occur, each returned segment has Start/End, Text, Words, SpeakerId set and OriginalStart populated with the input segment's start.
    /// </returns>
    private static IReadOnlyList<TranscriptSegmentArtifact> SplitSegmentAtSpeakerBoundaries(
        TranscriptSegmentArtifact segment,
        IReadOnlyList<DiarizedSegment> diarized)
    {
        var overlapping = diarized
            .Where(d => d.EndSeconds > segment.Start && d.StartSeconds < segment.End)
            .OrderBy(d => d.StartSeconds)
            .ToList();

        // Single speaker or no word timestamps → assign best speaker, return as-is
        if (overlapping.Count <= 1 || segment.Words is null || segment.Words.Count == 0)
        {
            segment.SpeakerId = FindBestSpeakerFor(segment.Start, segment.End, diarized);
            return [segment];
        }

        // Group consecutive words by which diarized speaker turn they fall in
        var groups = new List<(string SpeakerId, List<WordTimestamp> Words)>();
        foreach (var word in segment.Words)
        {
            var wordMid = (word.Start + word.End) / 2.0;
            var speaker = overlapping
                .FirstOrDefault(d => d.StartSeconds <= wordMid && d.EndSeconds > wordMid)
                ?.SpeakerId
                ?? FindBestSpeakerFor(word.Start, word.End, diarized);

            if (groups.Count == 0 || groups[^1].SpeakerId != speaker)
                groups.Add((speaker, []));
            groups[^1].Words.Add(word);
        }

        // All words landed on one speaker after word-level assignment → no split needed
        if (groups.Count == 1)
        {
            segment.SpeakerId = groups[0].SpeakerId;
            return [segment];
        }

        return groups.Select(g => new TranscriptSegmentArtifact
        {
            Start         = g.Words[0].Start,
            End           = g.Words[^1].End,
            Text          = string.Join("", g.Words.Select(w => w.Text)).Trim(),
            SpeakerId     = g.SpeakerId,
            Words         = g.Words,
            OriginalStart = segment.Start,
        }).ToList<TranscriptSegmentArtifact>();
    }

    /// <summary>
    /// Merge speaker IDs from a transcript artifact into a translation artifact when segment start times align.
    /// </summary>
    /// <param name="transcriptPath">Filesystem path to the transcript JSON artifact.</param>
    /// <param name="translationPath">Filesystem path to the translation JSON artifact to update.</param>
    /// <param name="ct">Cancellation token for the asynchronous file and I/O operations.</param>
    /// <returns>`true` if one or more translation segments had their `SpeakerId` changed and the translation file was written; `false` if no changes were made or either artifact had no segments.</returns>
    private async Task<bool> MergeSpeakerIdsIntoTranslationAsync(
        string transcriptPath,
        string translationPath,
        CancellationToken ct)
    {
        var transcript = await ArtifactJson.LoadTranscriptAsync(transcriptPath, ct);
        var translation = await ArtifactJson.LoadTranslationAsync(translationPath, ct);

        if (transcript.Segments is null || translation.Segments is null) return false;

        // Build lookup keyed by OriginalStart (set on split segments) or Start.
        // For split segments that share the same OriginalStart, the first entry wins
        // so the translation segment (which uses the pre-split start time) is matched.
        var speakerByStart = new Dictionary<double, string>();
        foreach (var s in transcript.Segments)
        {
            if (s.SpeakerId is null) continue;
            var key = s.OriginalStart ?? s.Start;
            speakerByStart.TryAdd(key, s.SpeakerId);
        }

        var anyChanged = false;
        foreach (var seg in translation.Segments)
        {
            if (!speakerByStart.TryGetValue(seg.Start, out var speakerId)) continue;
            if (seg.SpeakerId == speakerId) continue;
            seg.SpeakerId = speakerId;
            anyChanged = true;
        }

        if (!anyChanged) return false;

        await ArtifactPersistence.AtomicWriteTextAsync(
                translationPath,
                ArtifactJson.SerializeTranslation(translation),
                ct)
            .ConfigureAwait(false);
        await WriteTranslationManifestAsync(
                translationPath,
                translation,
                translation.SourceLanguage ?? CurrentSession.SourceLanguage ?? string.Empty,
                translation.TargetLanguage ?? CurrentSession.TargetLanguage ?? string.Empty,
                ct)
            .ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Produces a comparable list of speaker-assignment tuples extracted from transcript segments.
    /// </summary>
    /// <param name="segments">The transcript segments to capture assignments from.</param>
    /// <returns>
    /// A list of tuples for each segment containing its Start, End, OriginalStart (may be null), and SpeakerId (empty string if the segment's SpeakerId is null).
    /// </returns>
    private static IReadOnlyList<(double Start, double End, double? OriginalStart, string SpeakerId)> CaptureTranscriptSpeakerAssignments(
        IReadOnlyList<TranscriptSegmentArtifact> segments)
    {
        var result = new List<(double Start, double End, double? OriginalStart, string SpeakerId)>(segments.Count);
        foreach (var segment in segments)
        {
            result.Add((
                segment.Start,
                segment.End,
                segment.OriginalStart,
                segment.SpeakerId ?? string.Empty));
        }

        return result;
    }

    /// <summary>
    /// Selects the speaker ID whose diarized interval has the largest overlap with the specified time range.
    /// </summary>
    /// <param name="start">Start time of the range in seconds.</param>
    /// <param name="end">End time of the range in seconds.</param>
    /// <param name="diarizedSegments">Diarized segments to consider (each with start/end times and a SpeakerId).</param>
    /// <returns>The speaker ID with the greatest positive overlap, or "spk_00" if no diarized segment overlaps the range.</returns>
    private static string FindBestSpeakerFor(double start, double end, IReadOnlyList<DiarizedSegment> diarizedSegments)
    {
        string? best = null;
        double bestOverlap = 0;
        foreach (var d in diarizedSegments)
        {
            var overlapStart = Math.Max(start, d.StartSeconds);
            var overlapEnd = Math.Min(end, d.EndSeconds);
            var overlap = Math.Max(0, overlapEnd - overlapStart);
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                best = d.SpeakerId;
            }
        }
        return best ?? "spk_00";
    }

    public IReadOnlyDictionary<string, string> GetSpeakerVoiceAssignments() =>
        CurrentSession.SpeakerVoiceAssignments is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(CurrentSession.SpeakerVoiceAssignments, StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> GetSpeakerReferenceAudioPaths() =>
        CurrentSession.SpeakerReferenceAudioPaths is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(CurrentSession.SpeakerReferenceAudioPaths, StringComparer.Ordinal);

    /// <summary>
            /// Gets the per-segment dub timing mode overrides from the current session snapshot.
            /// </summary>
            /// <remarks>
            /// Returns a new, shallow copy of the overrides so callers may inspect or iterate without affecting session state.
            /// </remarks>
            /// <returns>
            /// A read-only dictionary mapping segment IDs to their <see cref="SegmentTimingMode"/> override; empty if no overrides are set. The returned dictionary uses ordinal key comparison.
            /// </returns>
            public IReadOnlyDictionary<string, SegmentTimingMode> GetSegmentTimingModeOverrides() =>
        CurrentSession.SegmentTimingModeOverrides is null
            ? new Dictionary<string, SegmentTimingMode>()
            : new Dictionary<string, SegmentTimingMode>(CurrentSession.SegmentTimingModeOverrides, StringComparer.Ordinal);

    /// <summary>
    /// Set or remove a per-segment timing mode override for the given segment ID.
    /// </summary>
    /// <param name="segmentId">The identifier of the segment to modify; leading and trailing whitespace are ignored.</param>
    /// <param name="mode">The timing mode to set, or null to remove any existing override for the segment.</param>
    /// <remarks>
    /// Operation is thread-safe and will persist the updated session when a change is made via SaveCurrentSession(); if the new value is identical to the existing one no persistence occurs. This method validates <paramref name="segmentId"/> and throws <see cref="ArgumentException"/> for null, empty, or whitespace values.
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown when <paramref name="segmentId"/> is null, empty, or whitespace.</exception>
    public void SetSegmentTimingOverride(string segmentId, SegmentTimingMode? mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentId);
        var normalizedSegmentId = segmentId.Trim();
        lock (_sessionLock)
        {
            var current = CurrentSession.SegmentTimingModeOverrides is null
                ? new Dictionary<string, SegmentTimingMode>(StringComparer.Ordinal)
                : new Dictionary<string, SegmentTimingMode>(CurrentSession.SegmentTimingModeOverrides, StringComparer.Ordinal);

            bool changed;
            if (mode.HasValue)
            {
                changed = !current.TryGetValue(normalizedSegmentId, out var existing) || existing != mode.Value;
                current[normalizedSegmentId] = mode.Value;
            }
            else
            {
                changed = current.Remove(normalizedSegmentId);
            }

            if (!changed)
                return;

            CurrentSession = CurrentSession with
            {
                SegmentTimingModeOverrides = current.Count == 0 ? null : current,
            };
        }
        MarkSessionInputsChanged("segment timing override updated");
        SaveCurrentSession();
    }

    public void SetSpeakerVoiceAssignment(string speakerId, string voiceOrModel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(speakerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceOrModel);

        lock (_sessionLock)
        {
            var current = CurrentSession.SpeakerVoiceAssignments ?? new Dictionary<string, string>(StringComparer.Ordinal);
            var updated = new Dictionary<string, string>(current, StringComparer.Ordinal)
            {
                [speakerId] = voiceOrModel,
            };

            CurrentSession = CurrentSession with { SpeakerVoiceAssignments = updated };
        }
        MarkSessionInputsChanged("speaker voice assignment updated");
        SaveCurrentSession();
    }

    /// <summary>
    /// Removes the voice assignment for the given speaker from the current session and persists the session when a change occurs.
    /// </summary>
    /// <param name="speakerId">The speaker identifier whose voice assignment should be removed; must not be null, empty, or whitespace.</param>
    /// <remarks>
    /// - Validates <paramref name="speakerId"/> and throws <see cref="ArgumentException"/> if it is null, empty, or whitespace.
    /// - Acquires the coordinator's session lock while updating <c>CurrentSession.SpeakerVoiceAssignments</c>.
    /// - If no assignment exists for the specified speaker, the method returns without persisting.
    /// - When removal results in an empty assignments dictionary, the session value is set to <c>null</c>.
    /// - On a successful change the method calls <c>SaveCurrentSession()</c> to persist the session.
    /// </remarks>
    public void RemoveSpeakerVoiceAssignment(string speakerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(speakerId);
        lock (_sessionLock)
        {
            if (CurrentSession.SpeakerVoiceAssignments is null)
                return;

            var updated = new Dictionary<string, string>(CurrentSession.SpeakerVoiceAssignments, StringComparer.Ordinal);
            if (!updated.Remove(speakerId))
                return;
            CurrentSession = CurrentSession with { SpeakerVoiceAssignments = updated.Count == 0 ? null : updated };
        }
        MarkSessionInputsChanged("speaker voice assignment removed");
        SaveCurrentSession();
    }

    public void ApplySpeakerVoiceAssignmentChanges(IReadOnlyDictionary<string, string?> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        if (updates.Count == 0)
            return;

        lock (_sessionLock)
        {
            var current = CurrentSession.SpeakerVoiceAssignments is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(CurrentSession.SpeakerVoiceAssignments, StringComparer.Ordinal);

            var changed = false;
            foreach (var (speakerId, candidateVoice) in updates)
            {
                if (string.IsNullOrWhiteSpace(speakerId))
                    continue;

                var normalizedSpeakerId = speakerId.Trim();
                var normalizedVoice = string.IsNullOrWhiteSpace(candidateVoice) ? null : candidateVoice.Trim();

                if (string.IsNullOrWhiteSpace(normalizedVoice))
                {
                    changed |= current.Remove(normalizedSpeakerId);
                    continue;
                }

                if (!current.TryGetValue(normalizedSpeakerId, out var existing) ||
                    !string.Equals(existing, normalizedVoice, StringComparison.Ordinal))
                {
                    current[normalizedSpeakerId] = normalizedVoice;
                    changed = true;
                }
            }

            if (!changed)
                return;

            CurrentSession = CurrentSession with
            {
                SpeakerVoiceAssignments = current.Count == 0 ? null : current,
            };
        }
        MarkSessionInputsChanged("speaker voice assignments updated");
        SaveCurrentSession();
    }

    public void SetSpeakerReferenceAudioPath(string speakerId, string clipPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(speakerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clipPath);

        lock (_sessionLock)
        {
            var current = CurrentSession.SpeakerReferenceAudioPaths ?? new Dictionary<string, string>(StringComparer.Ordinal);
            var updated = new Dictionary<string, string>(current, StringComparer.Ordinal)
            {
                [speakerId] = clipPath,
            };

            CurrentSession = CurrentSession with { SpeakerReferenceAudioPaths = updated };
        }
        MarkSessionInputsChanged("speaker reference path updated");
        SaveCurrentSession();
    }

    /// <summary>
    /// Removes a stored reference audio path for the specified speaker from the current session and persists the session when a change is made.
    /// </summary>
    /// <param name="speakerId">The identifier of the speaker whose reference audio path should be removed. Must be non-empty and non-whitespace.</param>
    /// <remarks>
    /// Expected state on entry: a current session may be loaded; no specific pipeline stage is required. 
    /// State on success: the session's <c>SpeakerReferenceAudioPaths</c> mapping will have the entry for <paramref name="speakerId"/> removed; if the mapping becomes empty the session field is set to <c>null</c>. 
    /// Persistence: the method calls <c>SaveCurrentSession()</c> only when a removal actually occurs. 
    /// Guard behavior: if the session has no speaker reference mappings or the given <paramref name="speakerId"/> is not present, the method performs no change and does not persist the session.
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown when <paramref name="speakerId"/> is null, empty, or consists only of whitespace.</exception>
    public void RemoveSpeakerReferenceAudioPath(string speakerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(speakerId);
        lock (_sessionLock)
        {
            if (CurrentSession.SpeakerReferenceAudioPaths is null)
                return;

            var updated = new Dictionary<string, string>(CurrentSession.SpeakerReferenceAudioPaths, StringComparer.Ordinal);
            if (!updated.Remove(speakerId))
                return;
            CurrentSession = CurrentSession with { SpeakerReferenceAudioPaths = updated.Count == 0 ? null : updated };
        }
        MarkSessionInputsChanged("speaker reference path removed");
        SaveCurrentSession();
    }

    public void ApplySpeakerReferenceAudioPathChanges(IReadOnlyDictionary<string, string?> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        if (updates.Count == 0)
            return;

        lock (_sessionLock)
        {
            var current = CurrentSession.SpeakerReferenceAudioPaths is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(CurrentSession.SpeakerReferenceAudioPaths, StringComparer.Ordinal);

            var changed = false;
            foreach (var (speakerId, candidatePath) in updates)
            {
                if (string.IsNullOrWhiteSpace(speakerId))
                    continue;

                var normalizedSpeakerId = speakerId.Trim();
                var normalizedPath = string.IsNullOrWhiteSpace(candidatePath) ? null : candidatePath.Trim();

                if (string.IsNullOrWhiteSpace(normalizedPath))
                {
                    changed |= current.Remove(normalizedSpeakerId);
                    continue;
                }

                if (!current.TryGetValue(normalizedSpeakerId, out var existing) ||
                    !string.Equals(existing, normalizedPath, StringComparison.Ordinal))
                {
                    current[normalizedSpeakerId] = normalizedPath;
                    changed = true;
                }
            }

            if (!changed)
                return;

            CurrentSession = CurrentSession with
            {
                SpeakerReferenceAudioPaths = current.Count == 0 ? null : current,
            };
        }
        MarkSessionInputsChanged("speaker reference paths updated");
        SaveCurrentSession();
    }

    public Task<string> ExtractSpeakerReferenceFromSegmentAsync(
        string speakerId,
        WorkflowSegmentState segment,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(speakerId);
        ArgumentNullException.ThrowIfNull(segment);

        var startSeconds = Math.Max(0, segment.StartSeconds);
        var naturalDuration = Math.Max(0.1, segment.EndSeconds - segment.StartSeconds);
        return ExtractSpeakerReferenceFromSourceAsync(speakerId, startSeconds, naturalDuration, cancellationToken);
    }

    /// <summary>
    /// Extracts a WAV reference clip from the session's ingested (or source) media at an arbitrary timeline window.
    /// Duration is clamped between 3 and 15 seconds (same rules as segment-based extraction).
    /// </summary>
    public async Task<string> ExtractSpeakerReferenceFromSourceAsync(
        string speakerId,
        double startSeconds,
        double naturalDurationSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(speakerId);

        if (_audioProcessingService is null)
            throw new InvalidOperationException("Audio processing is unavailable, so reference extraction cannot run.");

        var mediaPath = !string.IsNullOrWhiteSpace(CurrentSession.IngestedMediaPath)
            ? CurrentSession.IngestedMediaPath
            : CurrentSession.SourceMediaPath;
        if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath))
            throw new FileNotFoundException("Cannot extract reference clip because source media is unavailable.", mediaPath);

        var safeSpeakerId = string.Join("_", speakerId.Trim().Split(Path.GetInvalidFileNameChars()));
        var refsDir = Path.Combine(GetSessionDirectory(), "tts", "references");
        Directory.CreateDirectory(refsDir);

        var start = Math.Max(0, startSeconds);
        var naturalDuration = Math.Max(0.1, naturalDurationSeconds);
        var durationSeconds = Math.Clamp(naturalDuration, 3.0, 15.0);
        var outputPath = Path.Combine(
            refsDir,
            $"manual-ref-{safeSpeakerId}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.wav");

        await _audioProcessingService.ExtractAudioClipAsync(
            mediaPath,
            outputPath,
            start,
            durationSeconds,
            cancellationToken).ConfigureAwait(false);

        return outputPath;
    }

    /// <summary>
    /// Plays a short audio file on the headless segment transport (stops active TTS preview first).
    /// </summary>
    public Task PlayWizardAudioPreviewAsync(string audioFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioFilePath);
        if (!File.Exists(audioFilePath))
            throw new FileNotFoundException("Preview audio file not found.", audioFilePath);

        StopTtsPlayback();

        var player = GetOrCreateSegmentPlayer();
        player.Load(audioFilePath);
        player.Volume = TtsVolume;
        player.Seek(0);
        _ = Task.Run(() => player.Play()).FireAndForgetAsync(_log, "Play wizard reference preview");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops wizard / TTS preview playback on the segment transport.
    /// </summary>
    public void StopWizardAudioPreview() => StopTtsPlayback();

    public async Task<string?> AutoPickAlternateSpeakerReferenceAsync(
        string speakerId,
        IReadOnlyCollection<string>? excludePaths = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(speakerId);

        if (_audioProcessingService is null)
            return null;

        if (string.IsNullOrWhiteSpace(CurrentSession.TranscriptPath) || !File.Exists(CurrentSession.TranscriptPath))
            return null;

        var mediaPath = !string.IsNullOrWhiteSpace(CurrentSession.IngestedMediaPath)
            ? CurrentSession.IngestedMediaPath
            : CurrentSession.SourceMediaPath;
        if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath))
            return null;

        var transcript = await ArtifactJson
            .LoadTranscriptAsync(CurrentSession.TranscriptPath, cancellationToken)
            .ConfigureAwait(false);
        if (transcript.Segments is null || transcript.Segments.Count == 0)
            return null;

        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (excludePaths is not null)
        {
            foreach (var path in excludePaths.Where(path => !string.IsNullOrWhiteSpace(path)))
                excluded.Add(path.Trim());
        }

        var refsDir = Path.Combine(GetSessionDirectory(), "tts", "references");
        Directory.CreateDirectory(refsDir);
        var safeSpeakerId = string.Join("_", speakerId.Trim().Split(Path.GetInvalidFileNameChars()));

        var candidates = transcript.Segments
            .Where(segment => string.Equals(segment.SpeakerId, speakerId, StringComparison.Ordinal))
            .Select(segment => new
            {
                Start = segment.Start,
                Duration = Math.Max(0.1, segment.End - segment.Start),
            })
            .Where(candidate => candidate.Duration > 0)
            .OrderByDescending(candidate => candidate.Duration)
            .Take(8)
            .ToList();

        foreach (var candidate in candidates)
        {
            var boundedDuration = Math.Clamp(candidate.Duration, 3.0, 15.0);
            var startMs = (long)Math.Round(candidate.Start * 1000, MidpointRounding.AwayFromZero);
            var outputPath = Path.Combine(refsDir, $"alt-ref-{safeSpeakerId}-{startMs}.wav");
            if (excluded.Contains(outputPath))
                continue;

            try
            {
                await _audioProcessingService.ExtractAudioClipAsync(
                        mediaPath,
                        outputPath,
                        Math.Max(0, candidate.Start),
                        boundedDuration,
                        cancellationToken)
                    .ConfigureAwait(false);
                return outputPath;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Try next candidate.
            }
        }

        return null;
    }

    private string ResolveVoiceForSegment(TranslationSegmentArtifact segment, string defaultVoice)
    {
        var speakerId = segment.SpeakerId;
        if (string.IsNullOrWhiteSpace(speakerId))
            return defaultVoice;

        if (CurrentSession.SpeakerVoiceAssignments is not null &&
            CurrentSession.SpeakerVoiceAssignments.TryGetValue(speakerId, out var mappedVoice) &&
            !string.IsNullOrWhiteSpace(mappedVoice))
        {
            return mappedVoice;
        }

        return defaultVoice;
    }

    private string? ResolveReferenceAudioForSegment(TranslationSegmentArtifact segment)
    {
        if (CurrentSession.SpeakerReferenceAudioPaths is null)
            return null;

        var speakerId = segment.SpeakerId;
        if (!string.IsNullOrWhiteSpace(speakerId) &&
            CurrentSession.SpeakerReferenceAudioPaths.TryGetValue(speakerId, out var speakerPath) &&
            !string.IsNullOrWhiteSpace(speakerPath))
            return speakerPath;

        var defaultKey = QwenReferenceKeys.SingleSpeakerDefault;
        return CurrentSession.SpeakerReferenceAudioPaths.TryGetValue(defaultKey, out var defaultPath) &&
               !string.IsNullOrWhiteSpace(defaultPath)
            ? defaultPath
            : null;
    }

    private void QueueMediaReloadRequest(bool autoPlay, string reason)
    {
        if (string.IsNullOrWhiteSpace(CurrentSession.IngestedMediaPath))
            return;

        PendingMediaReloadRequest = new MediaReloadRequest(
            CurrentSession.IngestedMediaPath,
            autoPlay,
            reason);
    }

    /// <summary>
    /// Retrieves the segment player used for TTS playback, creating one if necessary.
    /// </summary>
    /// <returns>The segment player instance; its <c>PlaybackRate</c> is set to the coordinator's TTS playback rate and segment lifecycle event handlers are subscribed (only once).</returns>
    private IMediaTransport GetOrCreateSegmentPlayer()
    {
        var player = _transportManager.GetOrCreateSegmentPlayer();
        player.PlaybackRate = TtsPlaybackRate;
        player.Volume = TtsVolume;

        // Subscribe to segment lifecycle events exactly once.
        if (!_subscribedToSegmentEvents)
        {
            player.Ended += _segmentEndedHandler;
            player.ErrorOccurred += _segmentErrorHandler;
            _subscribedToSegmentEvents = true;
        }

        return player;
    }

    /// <summary>
    /// Update the segment player's playback rate to match the new TTS playback rate.
    /// </summary>
    /// <param name="value">New playback rate multiplier (e.g., 1.0 = normal speed).</param>
    partial void OnTtsPlaybackRateChanged(double value)
    {
        if (_transportManager.SegmentPlayer is { } player)
            player.PlaybackRate = value;
    }

    partial void OnTtsVolumeChanged(double value)
    {
        if (_transportManager.SegmentPlayer is { } player)
            player.Volume = value;
    }

    /// <summary>
    /// Queues playback of the pre-generated TTS audio for the specified translation segment.
    /// </summary>
    /// <param name="segmentId">The identifier of the translation segment whose TTS audio should be played.</param>
    /// <remarks>
    /// Entry/state requirements: a current session must be loaded and must contain a TTS audio path for <paramref name="segmentId"/> in <c>CurrentSession.TtsSegmentAudioPaths</c>.
    /// On success: marks the coordinator's in-memory playback state by setting <c>PlaybackState</c> to <c>PlayingSingleSegment</c> and <c>ActiveTtsSegmentId</c> to <paramref name="segmentId"/>, then schedules the actual playback.
    /// Persistence: does not persist the session to disk.
    /// Cancellation/guards: there is no cancellation token for this call; playback is started/scheduled and runs independently. If the required audio file is not present on disk, a <see cref="FileNotFoundException"/> is thrown.
    /// </remarks>
    /// <exception cref="FileNotFoundException">Thrown if the resolved TTS audio file does not exist on disk.</exception>
    public Task PlayTtsForSegmentAsync(string segmentId)
        => PlayTtsForSegmentAsync(segmentId, null, SegmentTimingMode.Off);

    /// <summary>
    /// Queues playback of the TTS audio associated with the specified segment and marks that segment as the active TTS segment.
    /// </summary>
    /// <remarks>
    /// Requires an active session whose <c>TtsSegmentAudioPaths</c> contains an entry for <paramref name="segmentId"/> and whose audio file exists on disk; otherwise the method throws. On entry no specific session stage is required. On success the coordinator's <see cref="PlaybackState"/> is set to <c>PlayingSingleSegment</c> and <see cref="ActiveTtsSegmentId"/> is set to <paramref name="segmentId"/>. The audio playback is started in the background and this method returns immediately; it does not persist session state. This method does not accept cancellation and does not await the playback task; background failures are logged via the coordinator logger.
    /// </remarks>
    /// <param name="segmentId">Identifier of the translation/segment whose TTS audio will be played.</param>
    /// <param name="segment">Optional workflow segment state used by timing modes; may be null.</param>
    /// <param name="timingMode">Effective timing mode — resolves per-segment override then session default.</param>
    /// <returns>A Task that completes once the playback request has been queued.</returns>
    /// <exception cref="InvalidOperationException">Thrown when there is no active session or when no TTS audio path exists for <paramref name="segmentId"/>.</exception>
    /// <exception cref="System.IO.FileNotFoundException">Thrown when the resolved TTS audio file does not exist on disk.</exception>
    public Task PlayTtsForSegmentAsync(string segmentId, WorkflowSegmentState? segment, SegmentTimingMode timingMode)
    {
        if (CurrentSession is null)
            throw new InvalidOperationException("No active session.");

        var paths = CurrentSession.TtsSegmentAudioPaths;
        if (paths is null || !paths.TryGetValue(segmentId, out var audioPath))
            throw new InvalidOperationException($"No TTS audio path for segment '{segmentId}'.");

        if (!File.Exists(audioPath))
            throw new FileNotFoundException($"TTS audio file not found: {audioPath}", audioPath);

        StopTtsPlayback();
        PlaybackState = PlaybackState.PlayingSingleSegment;
        ActiveTtsSegmentId = segmentId;

        _ = PlayTtsWithTimingAsync(segmentId, audioPath, segment, timingMode).FireAndForgetAsync(
            _log, $"Play TTS for segment {segmentId}");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Plays a TTS audio file for a queued segment, applying optional timing behavior (stretch or pause) and guarding against inactive segments.
    /// </summary>
    /// <param name="segmentId">The identifier of the active TTS segment; used as the active-segment guard.</param>
    /// <param name="audioPath">Path to the TTS audio file to play.</param>
    /// <param name="segment">Optional segment timing metadata; required for timing behaviors that depend on segment start/end.</param>
    /// <param name="timingMode">Controls timing behavior: plain playback, stretch to match segment duration, or pause-source semantics.</param>
    /// <returns>A task that completes when playback (and any timing behavior) finishes or is aborted.</returns>
    /// <remarks>
    /// Entry state: expects the coordinator to be running with an active session; the method requires that the caller has set ActiveTtsSegmentId to the segment being played when appropriate.
    /// On success: leaves playback state equivalent to idle for single-segment playback; for pause-mode it will seek the source player to the segment end and resume source playback only if it was playing before the pause; it clears ActiveTtsSegmentId and sets PlaybackState to Idle when appropriate.
    /// Persistence: does not modify or persist session state to disk.
    /// Guards: the method repeatedly checks the active-segment guard via IsStillActiveTtsSegment(segmentId) and returns early without side effects when the guard fails. For pause-mode, if the pause-mode completion does not succeed the method attempts to resume the source player only if the segment is still active and the source had been playing, then clears active TTS state.
    /// Cancellation: this method does not accept a CancellationToken and does not honor external cancellation; internal time-stretching is invoked with CancellationToken.None.
    /// Side effects: may seek and resume the source media player, change ActiveTtsSegmentId, and set PlaybackState to Idle. Exceptions during timing operations are caught and logged; OperationCanceledException from invoked services is propagated where thrown by those services.
    /// </remarks>
    private async Task PlayTtsWithTimingAsync(
        string segmentId,
        string audioPath,
        WorkflowSegmentState? segment,
        SegmentTimingMode timingMode)
    {
        var effectivePath = audioPath;

        if (timingMode == SegmentTimingMode.Stretch && segment is not null && _audioProcessingService is not null)
        {
            var targetDuration = segment.EndSeconds - segment.StartSeconds;
            if (targetDuration > 0)
            {
                var stretchedPath = audioPath + ".stretched.mp3";
                try
                {
                    var stretched = await _audioProcessingService.TimeStretchAsync(
                        audioPath, stretchedPath, targetDuration,
                        cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    if (!IsStillActiveTtsSegment(segmentId))
                        return;
                    if (stretched && File.Exists(stretchedPath))
                        effectivePath = stretchedPath;
                }
                catch (Exception ex)
                {
                    _log.Warning($"Time-stretch failed for segment audio, playing original: {ex.Message}");
                }
            }
        }

        if (!IsStillActiveTtsSegment(segmentId))
            return;

        var player = GetOrCreateSegmentPlayer();
        player.Load(effectivePath);
        player.Volume = TtsVolume;

        if (timingMode == SegmentTimingMode.Pause && segment is not null)
        {
            var source = SourceMediaPlayer;
            var sourceWasPlaying = source?.IsPlaying == true;

            // Pause the source video, play TTS fully, then seek to segment end and optionally resume.
            source?.Pause();

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ttsPauseModeCompletion = tcs;
            var pauseModeCompletedSuccessfully = false;

            try
            {
                await Task.Run(() => player.Play()).ConfigureAwait(false);
                pauseModeCompletedSuccessfully = await tcs.Task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warning($"Pause-mode TTS playback failed for segment '{segmentId}': {ex.Message}");
            }
            finally
            {
                if (ReferenceEquals(_ttsPauseModeCompletion, tcs))
                    _ttsPauseModeCompletion = null;
            }

            if (!pauseModeCompletedSuccessfully)
            {
                if (IsStillActiveTtsSegment(segmentId))
                {
                    if (source is not null && sourceWasPlaying)
                    {
                        try
                        {
                            source.Play();
                        }
                        catch (Exception ex)
                        {
                            _log.Warning($"Failed to resume source playback after pause-mode failure for segment '{segmentId}': {ex.Message}");
                        }
                    }

                    ActiveTtsSegmentId = null;
                    PlaybackState = PlaybackState.Idle;
                }

                return;
            }

            if (!IsStillActiveTtsSegment(segmentId))
                return;

            // Seek source to segment end; only resume if it was playing before we paused for preview.
            if (source is not null)
            {
                source.Seek((long)(segment.EndSeconds * 1000));
                if (sourceWasPlaying)
                    source.Play();
            }

            if (string.Equals(ActiveTtsSegmentId, segmentId, StringComparison.Ordinal))
            {
                ActiveTtsSegmentId = null;
                PlaybackState = PlaybackState.Idle;
            }
        }
        else
        {
            await Task.Run(() => player.Play()).ConfigureAwait(false);
        }
    }

    /// <summary>
        /// Determines whether the provided segment identifier matches the currently active TTS segment.
        /// </summary>
        /// <param name="segmentId">The segment identifier to compare with the active TTS segment.</param>
        /// <returns>`true` if <paramref name="segmentId"/> is equal to the current <c>ActiveTtsSegmentId</c> using ordinal comparison; `false` otherwise.</returns>
        private bool IsStillActiveTtsSegment(string segmentId) =>
        string.Equals(ActiveTtsSegmentId, segmentId, StringComparison.Ordinal);

    /// <summary>
    /// Stops any in-progress TTS segment playback and resets TTS playback state.
    /// </summary>
    /// <remarks>
    /// If a segment player is present, an attempt is made to pause it; an <see cref="ObjectDisposedException"/> from the pause call is swallowed (shutdown/race condition).
    /// Completes and clears any outstanding pause-mode completion, clears the active TTS segment identifier, and sets the coordinator's playback state to <see cref="PlaybackState.Idle"/>.
    /// This method does not persist session state and has no cancellation semantics. If no segment player exists, the method is a no-op.
    /// </remarks>
    public void StopTtsPlayback()
    {
        try
        {
            _transportManager.SegmentPlayer?.Pause();
        }
        catch (ObjectDisposedException)
        {
            // Shutdown/race path: segment transport was disposed while timer tick tried to stop playback.
        }
        _ttsPauseModeCompletion?.TrySetResult(false);
        _ttsPauseModeCompletion = null;
        ActiveTtsSegmentId = null;
        PlaybackState = PlaybackState.Idle;
    }

    /// <summary>
    /// Stops any active TTS playback and pauses the source media player.
    /// </summary>
    public void StopPlayback()
    {
        StopTtsPlayback();
        StopAmbiancePlayback();
        StopSourceMedia();
    }

    /// <summary>
    /// Starts playback of the session's ingested media positioned at the start time of the specified segment.
    /// </summary>
    /// <param name="segmentId">The identifier of the segment to play from.</param>
    /// <returns>A task that completes after playback has been scheduled.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when there is no active session, no media is loaded, or the specified segment cannot be found.
    /// </exception>
    /// <exception cref="FileNotFoundException">Thrown when the ingested media file does not exist at the recorded path.</exception>
    public async Task PlaySourceMediaAtSegmentAsync(string segmentId)
    {
        if (CurrentSession is null)
            throw new InvalidOperationException("No active session.");

        if (string.IsNullOrEmpty(CurrentSession.IngestedMediaPath))
            throw new InvalidOperationException("No media loaded.");

        if (!File.Exists(CurrentSession.IngestedMediaPath))
            throw new FileNotFoundException($"Ingested media not found: {CurrentSession.IngestedMediaPath}");

        var segments = await GetSegmentWorkflowListAsync();
        var target = segments.Find(s => s.SegmentId == segmentId);
        if (target is null)
            throw new InvalidOperationException($"Segment not found: {segmentId}");

        var player = GetOrCreateSourcePlayer();
        player.Load(CurrentSession.IngestedMediaPath);
        player.Seek((long)(target.StartSeconds * 1000));
        _log.Debug($"Playing source media at segment {segmentId} ({target.StartSeconds:F1}s)");
        _ = Task.Run(() => player.Play()).FireAndForgetAsync(_log, $"Play Source Media at segment {segmentId}");
    }

    /// <summary>
    /// Pauses playback of the current source media player if one exists.
    /// </summary>
    public void StopSourceMedia()
    {
        StopAmbiancePlayback();
        try
        {
            _transportManager.SourceMediaPlayer?.Pause();
        }
        catch (ObjectDisposedException)
        {
            // Shutdown/race path: source transport was disposed during pause.
        }
    }

    /// <summary>
    /// Pauses separated ambiance preview playback if an ambiance player exists.
    /// </summary>
    public void StopAmbiancePlayback()
    {
        try
        {
            _transportManager.AmbiancePlayer?.Pause();
        }
        catch (ObjectDisposedException)
        {
            // Shutdown/race path: ambiance transport was disposed during pause.
        }
    }

    public IMediaTransport GetOrCreateSourcePlayer() =>
        GetOrCreateSourcePlayerWithDiagnostics();

    private IMediaTransport GetOrCreateSourcePlayerWithDiagnostics()
    {
        var player = _transportManager.GetOrCreateSourcePlayer();
        EnsureSourcePlayerDiagnosticsSubscribed(player);
        return player;
    }

    public IMediaTransport? SourceMediaPlayer => _transportManager.SourceMediaPlayer;

    /// <summary>The TTS segment player, if it has been created. Null until first TTS playback.</summary>
    public IMediaTransport? SegmentPlayer => _transportManager.SegmentPlayer;

    /// <summary>The ambiance preview player, if it has been created. Null until first separated-ambiance preview.</summary>
    public IMediaTransport? AmbiancePlayer => _transportManager.AmbiancePlayer;

    public IMediaTransport GetOrCreateAmbiancePlayer() =>
        _transportManager.GetOrCreateAmbiancePlayer();

    /// <summary>
    /// Performs an orderly shutdown by flushing pending state, unsubscribing event handlers, waiting for in-flight TTS tasks, and disposing managed resources.
    /// </summary>
    /// <remarks>
    /// Attempts to complete any pending save and in-flight TTS operations before disposing internal services and transport resources. Exceptions thrown during disposal or while waiting for pending tasks are caught and ignored to allow shutdown to continue.
    /// </remarks>
    public void Dispose()
    {
        RequestShutdown();
        if (_containerizedProbe is not null)
            _containerizedProbe.ProbeResultUpdated -= OnProbeResultUpdated;
        _readinessSignals.OnCompleted();
        _readinessSignals.Dispose();
        FlushPendingSave();
        WaitForOwnedBackgroundOperations(TimeSpan.FromSeconds(5));

        // Unsubscribe segment events before disposing the transport manager.
        if (_subscribedToSegmentEvents)
        {
            var segmentPlayer = _transportManager.GetOrCreateSegmentPlayer();
            segmentPlayer.Ended -= _segmentEndedHandler;
            segmentPlayer.ErrorOccurred -= _segmentErrorHandler;
            _subscribedToSegmentEvents = false;
        }

        if (_subscribedToSourceDiagnostics
            && _transportManager.SourceMediaPlayer is LibMpvEmbeddedTransport embedded)
        {
            embedded.VsrDiagnosticChanged -= _vsrDiagnosticChangedHandler;
            _subscribedToSourceDiagnostics = false;
        }

        // Prefer waiting for in-flight TTS before tearing down the inference host (local TTS can
        // still be talking to the managed GPU process). If that wait times out, we still dispose
        // the inference hosts so child Python/Docker processes do not keep the OS process alive
        // after the main window closes.
        var pendingTtsSnapshot = SnapshotPendingTtsTasks();
        if (pendingTtsSnapshot.Length > 0)
        {
            try
            {
                bool completed = Task.WhenAll(pendingTtsSnapshot).Wait(TimeSpan.FromSeconds(2));
                if (!completed)
                {
                    _log.Warning("TTS shutdown timed out — scheduling background disposal of TTS service.");
                    ScheduleSafeTtsDisposal();

                    _transportManager.Dispose();

                    if (_containerizedInferenceManager is IDisposable inferenceAfterTtsTimeout)
                    {
                        try
                        {
                            inferenceAfterTtsTimeout.Dispose();
                        }
                        catch (Exception ex)
                        {
                            _log.Warning($"Failed to dispose containerized inference manager after TTS timeout: {ex.Message}");
                        }
                    }

                    _perSessionStore?.Dispose();
                    _shutdownCts.Dispose();
                    return;
                }
            }
            catch
            {
                // Ignore exceptions during shutdown - tasks may have been canceled or failed.
            }
        }

        if (_containerizedInferenceManager is IDisposable disposableInferenceManager)
        {
            try
            {
                disposableInferenceManager.Dispose();
            }
            catch (Exception ex)
            {
                _log.Warning($"Failed to dispose containerized inference manager on shutdown: {ex.Message}");
            }
        }

        RetireTranslationProviderCache("coordinator dispose");
        RetireTtsProviderCache("coordinator dispose");
        _transportManager.Dispose();
        _perSessionStore?.Dispose();
        _shutdownCts.Dispose();
    }

    /// <summary>
    /// Schedules disposal of the coordinator's TTS service on a background thread when the service implements <see cref="IDisposable"/>.
    /// </summary>
    /// <remarks>
    /// If no disposable TTS service is present, the method returns immediately. Disposal is performed asynchronously and any exception
    /// thrown during disposal is caught and logged as a warning; exceptions are not propagated to the caller.
    /// </remarks>
    private void ScheduleSafeTtsDisposal()
    {
        Task.Run(() => RetireTtsProviderCache("background tts disposal"))
            .FireAndForgetAsync(_log, "TTS background disposal");
    }
}
