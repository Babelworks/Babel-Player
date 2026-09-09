using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Transcription;

namespace Babel.Player.Services;

public sealed partial class SessionWorkflowCoordinator
{
    /// <summary>
    /// Channel-based streaming transcription → translation → TTS pipeline stages.
    /// </summary>
    internal sealed class StreamingPipelineOrchestrator
    {
        private readonly SessionWorkflowCoordinator _c;

        /// <summary>
/// Initializes a new orchestrator instance that is bound to the given session workflow coordinator.
/// </summary>
/// <remarks>
/// Expected entry state: a valid (non-null) <paramref name="coordinator"/> representing the session host and its services.
/// Exit state: the orchestrator is ready to run streaming pipeline methods that operate against the provided coordinator.
/// Persistence: this constructor does not persist session state.
/// Cancellation: not applicable to construction.
/// </remarks>
/// <param name="coordinator">The <see cref="SessionWorkflowCoordinator"/> that the orchestrator will use to access session state, providers, and helper methods. Must not be null.</param>
internal StreamingPipelineOrchestrator(SessionWorkflowCoordinator coordinator) => _c = coordinator;

        /// <summary>
        /// Orchestrates a full streaming pipeline: transcription (streaming when available) → translation → per-segment TTS, producing and committing transcript, translation, and final TTS artifacts.
        /// </summary>
        /// <remarks>
        /// Entry state: expects a current session configured on the coordinator; the method will ensure transcription, translation, and TTS providers/models are ready before starting stages. If the configured transcription provider does not support streaming, the method falls back to a non-streaming transcription path and then runs translation and TTS from the produced transcript artifact. On success the method persists session state for transcription, translation, and TTS (commits artifacts and related session metadata) and produces final stitched TTS output. The method observes the provided <paramref name="cancellationToken"/> and will cancel ongoing operations when it is signaled; cancellation may result in partial artifacts depending on where the pipeline was interrupted.
        /// </remarks>
        /// <param name="progress">Optional overall progress reporter used to report stage-level progress and status messages.</param>
        /// <param name="transcriptionStageContext">Optional context used to report transcription stage status and progress.</param>
        /// <param name="translationStageContext">Optional context used to report translation stage status and progress.</param>
        /// <param name="ttsStageContext">Optional context used to report TTS stage status and progress.</param>
        /// <param name="cancellationToken">Cancellation token to abort the pipeline; ongoing provider/model initialization and in-flight segment processing honor this token.</param>
        internal async Task ExecuteFullPipelineAsync(
            IProgress<double>? progress,
            PipelineStageContext? transcriptionStageContext,
            PipelineStageContext? translationStageContext,
            PipelineStageContext? ttsStageContext,
            CancellationToken cancellationToken)
        {
            _c.ResolveAndApplyExecutionPlan(Planning.InferenceStage.Transcription);
            var transcriptionSourcePath = _c.CurrentSession.IngestedMediaPath
                ?? throw new InvalidOperationException("Ingested media path is required to start the streaming pipeline.");
            if (_c.CurrentSettings.VocalSeparationEnabled)
            {
                transcriptionSourcePath = await _c.SeparateVocalsAsync(progress, transcriptionStageContext, cancellationToken)
                    .ConfigureAwait(false);
            }

            await _c.EnsureTranscriptionProviderReadyAsync(progress, transcriptionStageContext, cancellationToken);

            if (_c._transcriptionService is not IStreamingTranscriptionProvider streamingProvider)
            {
                await _c.TranscribeMediaAsync(progress, transcriptionStageContext, cancellationToken);
                await ExecuteTranslationAndTtsFromTranscriptAsync(
                    progress,
                    translationStageContext,
                    ttsStageContext,
                    cancellationToken);
                return;
            }

            ReportStage(
                transcriptionStageContext,
                $"Starting transcription with {_c.CurrentSettings.TranscriptionProvider} / {_c.CurrentSettings.TranscriptionModel}. Translation will begin as segments arrive.",
                progress01: 0,
                isIndeterminate: true,
                streamingStatus: "Downstream translation and dubbing will overlap with ASR output.");

            var transcriptPath = BuildTranscriptArtifactPath(transcriptionSourcePath);
            var transcriptPartialPath = TranscriptArtifactStreamingWriter.GetPartialPath(transcriptPath);
            var targetLanguage = NormalizePipelineLanguage(_c.CurrentSettings.TargetLanguage, _c.CurrentSettings.TargetLanguage);
            var translationPath = BuildTranslationArtifactPath(transcriptPath, targetLanguage);
            var translationPartialPath = TranscriptArtifactStreamingWriter.GetPartialPath(translationPath);
            var voice = _c.CurrentSettings.TtsVoice;
            var ttsLanguage = NormalizePipelineLanguage(
                _c.CurrentSession.TargetLanguage ?? _c.CurrentSettings.TargetLanguage,
                _c.CurrentSettings.TargetLanguage);

            var transcriptArtifactWriter = new TranscriptArtifactStreamingWriter(
                transcriptPartialPath,
                _c.CurrentSession.SourceLanguage ?? "unknown",
                0d);
            transcriptArtifactWriter.ResetJournal();
            await transcriptArtifactWriter.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var transcriptChannel = Channel.CreateBounded<TranscriptChannelItem>(new BoundedChannelOptions(8)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });
            var translationChannel = Channel.CreateUnbounded<TranslationChannelItem>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
            });
            var translationWriter = new TranslationArtifactStreamingWriter(
                translationPartialPath,
                _c.CurrentSession.SourceLanguage ?? "unknown",
                targetLanguage);
            translationWriter.ResetJournal();
            await translationWriter.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var translationStagePlan = _c.ResolveAndApplyExecutionPlan(Planning.InferenceStage.Translation);
            var translationDownloadProgress = CreateStageDownloadProgress(
                translationStageContext,
                progress,
                $"Preparing translation model '{_c.CurrentSettings.TranslationModel}'");
            var translationSnapshot = await _c.PrepareTranslationExecutionSnapshotAsync(
                    translationStagePlan,
                    transcriptPath,
                    _c.CurrentSession.SourceLanguage ?? "unknown",
                    targetLanguage,
                    translationDownloadProgress,
                    allowPendingTranscriptArtifact: true,
                    cancellationToken)
                .ConfigureAwait(false);
            await using var translationProviderLease = translationSnapshot.ProviderLease;
            var ttsStagePlan = _c.ResolveAndApplyExecutionPlan(Planning.InferenceStage.Tts);
            var ttsSnapshot = await _c.PrepareTtsExecutionSnapshotAsync(
                    ttsStagePlan,
                    translationSnapshot.TranslationPath,
                    voice,
                    ttsLanguage,
                    progress,
                    ttsStageContext,
                    cancellationToken,
                    allowPendingTranslationArtifact: true)
                .ConfigureAwait(false);
            await using var ttsProviderLease = ttsSnapshot.ProviderLease;
            var ttsResultChannel = Channel.CreateBounded<TtsChannelItem>(new BoundedChannelOptions(Math.Max(4, ttsSnapshot.MaxConcurrency))
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });

            ReportStage(
                translationStageContext,
                $"Streaming translation to {targetLanguage} with {translationSnapshot.Plan.ProviderId} / {translationSnapshot.Model}.",
                progress01: 0,
                isIndeterminate: true,
                streamingStatus: "Dub generation will start as translated segments arrive.");
            ReportStage(
                ttsStageContext,
                $"Streaming TTS synthesis with {ttsSnapshot.Plan.ProviderId} / {ttsSnapshot.Voice}.",
                progress01: 0,
                isIndeterminate: true,
                streamingStatus: "Segment clips are generated as translation continues.");

            using var pipelineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var pipelineToken = pipelineCts.Token;

            var ttsCollectorTask = CollectStreamingTtsResultsAsync(ttsResultChannel.Reader, ttsStageContext, pipelineToken);
            var ttsStageTask = RunStreamingTtsStageAsync(
                ttsSnapshot,
                translationChannel.Reader,
                ttsResultChannel.Writer,
                ttsStageContext,
                pipelineToken);
            var translationTask = RunStreamingTranslationStageAsync(
                translationSnapshot,
                transcriptChannel.Reader,
                translationChannel.Writer,
                translationWriter,
                translationStageContext,
                pipelineToken);

            var forwardingWriter = new TranscriptChannelForwardingWriter(transcriptArtifactWriter, transcriptChannel.Writer);
            ExceptionDispatchInfo? capturedFailure = null;
            TranscriptionResult? transcriptionResult = null;
            try
            {
                var streamRequest = CpuTranscriptionRuntimePolicy.BuildTranscriptionRequest(
                    _c.CurrentSettings,
                    _c.HardwareSnapshot,
                    transcriptionSourcePath,
                    transcriptPath,
                    _c.CurrentSettings.TranscriptionModel,
                    SessionSnapshotSemantics.NormalizeTranscriptionLanguageHint(_c.CurrentSettings.TranscriptionLanguageHint),
                    _c.Log);
                transcriptionResult = await _c._inferenceEngine.TranscribeStreamingAsync(
                    streamingProvider,
                    streamRequest,
                    forwardingWriter,
                    pipelineToken).ConfigureAwait(false);
                forwardingWriter.TryComplete();

                await transcriptArtifactWriter.CompleteAsync(transcriptionResult, transcriptPath, pipelineToken).ConfigureAwait(false);
                await _c.CommitTranscriptionSessionStateAsync(transcriptionResult, transcriptPath).ConfigureAwait(false);
                ReportStage(
                    transcriptionStageContext,
                    $"Transcription complete. {transcriptionResult.Segments.Count} segments were detected in {transcriptionResult.Language}.",
                    progress01: 1,
                    isIndeterminate: false);

                var translationResult = await translationTask.ConfigureAwait(false);
                await translationWriter.CompleteAsync(translationPath, pipelineToken).ConfigureAwait(false);
                await PersistStreamingTranslationWorkingArtifactAsync(
                    translationSnapshot.WorkingTranslationPath,
                    translationResult.SourceLanguage,
                    translationResult.TargetLanguage,
                    translationWriter.OrderedSegments,
                    pipelineToken).ConfigureAwait(false);
                await _c.CommitTranslationSessionStateAsync(
                    translationSnapshot,
                    translationResult).ConfigureAwait(false);
                ReportStage(
                    translationStageContext,
                    $"Translation complete. {translationResult.Segments.Count} segments were translated from {translationResult.SourceLanguage} to {translationResult.TargetLanguage}.",
                    progress01: 1,
                    isIndeterminate: false);

                await ttsStageTask.ConfigureAwait(false);
                var segmentAudioPaths = await ttsCollectorTask.ConfigureAwait(false);
                var renderResult = await _c.StitchSegmentClipsAsync(
                    ttsSnapshot,
                    segmentAudioPaths,
                    translationWriter.OrderedSegments,
                    ttsSnapshot.TtsPath,
                    ttsStageContext,
                    pipelineToken).ConfigureAwait(false);
                await _c.CommitTtsSessionStateAsync(
                    ttsSnapshot,
                    renderResult,
                    segmentAudioPaths,
                    null,
                    translationWriter.OrderedSegments,
                    ttsStageContext).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                pipelineCts.Cancel();
                forwardingWriter.TryComplete(ex);
                capturedFailure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                forwardingWriter.TryComplete();
                capturedFailure = await ObserveStreamingTaskCompletionAsync(translationTask, "translation", capturedFailure, pipelineCts).ConfigureAwait(false);
                capturedFailure = await ObserveStreamingTaskCompletionAsync(ttsStageTask, "tts-stage", capturedFailure, pipelineCts).ConfigureAwait(false);
                capturedFailure = await ObserveStreamingTaskCompletionAsync(ttsCollectorTask, "tts-collector", capturedFailure, pipelineCts).ConfigureAwait(false);
            }

            capturedFailure?.Throw();
        }

        /// <summary>
        /// Runs streamed translation and TTS stages using an existing transcript artifact: it enqueues transcript segments, performs per-segment translation, synthesizes per-segment audio, stitches final audio, and persists translation and TTS session state.
        /// </summary>
        /// <param name="progress">Optional progress reporter for overall pipeline progress.</param>
        /// <param name="translationStageContext">Context describing translation stage status and progress reporting; used to report stage start and completion.</param>
        /// <param name="ttsStageContext">Context describing TTS stage status and progress reporting; used to report stage start and completion.</param>
        /// <param name="cancellationToken">Token to cancel the operation; the method observes this token and will throw <see cref="OperationCanceledException"/> if cancellation is requested.</param>
        /// <exception cref="InvalidOperationException">Thrown if there is no transcript available in the current session (i.e., <see cref="CurrentSession.TranscriptPath"/> is null or empty).</exception>
        /// <remarks>
        /// Entry state: requires a completed transcript artifact available at <c>CurrentSession.TranscriptPath</c>. The method ensures translation and TTS providers/models are ready before starting work and will create service instances lazily if needed.
        /// Exit state on success: translation and TTS artifacts are finalized and committed to session state; translation and TTS stage contexts are reported as completed; final stitched TTS audio is written to disk and recorded in the session state.
        /// Persistence: commits translation and TTS session state (artifact paths, languages, voice, segment counts and produced segment audio paths) before returning.
        /// Cancellation: honors <paramref name="cancellationToken"/> throughout asynchronous operations and will propagate cancellation as <see cref="OperationCanceledException"/>.
        /// </remarks>
        internal async Task ExecuteTranslationAndTtsFromTranscriptAsync(
            IProgress<double>? progress,
            PipelineStageContext? translationStageContext,
            PipelineStageContext? ttsStageContext,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_c.CurrentSession.TranscriptPath))
                throw new InvalidOperationException("No transcript available. Please transcribe media first.");

            var transcript = await _c._artifactReader.LoadTranscriptAsync(_c.CurrentSession.TranscriptPath, cancellationToken).ConfigureAwait(false);
            var transcriptChannel = Channel.CreateBounded<TranscriptChannelItem>(new BoundedChannelOptions(8)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

            var producerTask = Task.Run(async () =>
            {
                try
                {
                    foreach (var segment in transcript.Segments ?? [])
                    {
                        if (string.IsNullOrWhiteSpace(segment.Text))
                            continue;

                        var segmentId = SegmentId(segment.Start);
                        await transcriptChannel.Writer.WriteAsync(
                            new TranscriptChannelItem(
                                segmentId,
                                CloneTranscriptSegment(segment),
                                _c.CurrentSession.SourceLanguage ?? transcript.Language ?? "unknown",
                                transcript.LanguageProbability),
                            cancellationToken).ConfigureAwait(false);
                    }

                    transcriptChannel.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    transcriptChannel.Writer.TryComplete(ex);
                    throw;
                }
            }, cancellationToken);

            var targetLanguage = NormalizePipelineLanguage(_c.CurrentSettings.TargetLanguage, _c.CurrentSettings.TargetLanguage);
            var translationPath = BuildTranslationArtifactPath(_c.CurrentSession.TranscriptPath!, targetLanguage);
            var translationPartialPath = TranscriptArtifactStreamingWriter.GetPartialPath(translationPath);
            var voice = _c.CurrentSettings.TtsVoice;
            var ttsLanguage = NormalizePipelineLanguage(
                _c.CurrentSession.TargetLanguage ?? _c.CurrentSettings.TargetLanguage,
                _c.CurrentSettings.TargetLanguage);

            var translationStagePlan = _c.ResolveAndApplyExecutionPlan(Planning.InferenceStage.Translation);
            var translationDownloadProgress = CreateStageDownloadProgress(
                translationStageContext,
                progress,
                $"Preparing translation model '{_c.CurrentSettings.TranslationModel}'");
            var translationSnapshot = await _c.PrepareTranslationExecutionSnapshotAsync(
                    translationStagePlan,
                    _c.CurrentSession.TranscriptPath!,
                    _c.CurrentSession.SourceLanguage ?? transcript.Language ?? "unknown",
                    targetLanguage,
                    translationDownloadProgress,
                    allowPendingTranscriptArtifact: false,
                    cancellationToken)
                .ConfigureAwait(false);
            await using var translationProviderLease = translationSnapshot.ProviderLease;
            var ttsStagePlan = _c.ResolveAndApplyExecutionPlan(Planning.InferenceStage.Tts);
            var ttsSnapshot = await _c.PrepareTtsExecutionSnapshotAsync(
                    ttsStagePlan,
                    translationSnapshot.TranslationPath,
                    voice,
                    ttsLanguage,
                    progress,
                    ttsStageContext,
                    cancellationToken,
                    allowPendingTranslationArtifact: true)
                .ConfigureAwait(false);
            await using var ttsProviderLease = ttsSnapshot.ProviderLease;

            var translationWriter = new TranslationArtifactStreamingWriter(
                translationPartialPath,
                _c.CurrentSession.SourceLanguage ?? transcript.Language ?? "unknown",
                targetLanguage);
            translationWriter.ResetJournal();
            await translationWriter.InitializeAsync(cancellationToken).ConfigureAwait(false);

            ReportStage(
                translationStageContext,
                $"Streaming translation to {targetLanguage} with {translationSnapshot.Plan.ProviderId} / {translationSnapshot.Model}.",
                progress01: 0,
                isIndeterminate: true,
                streamingStatus: "Dub generation will start as translated segments arrive.");
            ReportStage(
                ttsStageContext,
                $"Streaming TTS synthesis with {ttsSnapshot.Plan.ProviderId} / {ttsSnapshot.Voice}.",
                progress01: 0,
                isIndeterminate: true,
                streamingStatus: "Segment clips are generated as translation continues.");

            var translationChannel = Channel.CreateUnbounded<TranslationChannelItem>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
            });
            var ttsResultChannel = Channel.CreateBounded<TtsChannelItem>(new BoundedChannelOptions(Math.Max(4, ttsSnapshot.MaxConcurrency))
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });

            using var pipelineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var pipelineToken = pipelineCts.Token;

            var ttsCollectorTask = CollectStreamingTtsResultsAsync(ttsResultChannel.Reader, ttsStageContext, pipelineToken);
            var ttsStageTask = RunStreamingTtsStageAsync(
                ttsSnapshot,
                translationChannel.Reader,
                ttsResultChannel.Writer,
                ttsStageContext,
                pipelineToken);
            var translationTask = RunStreamingTranslationStageAsync(
                translationSnapshot,
                transcriptChannel.Reader,
                translationChannel.Writer,
                translationWriter,
                translationStageContext,
                pipelineToken);

            ExceptionDispatchInfo? capturedFailure = null;
            try
            {
                await producerTask.ConfigureAwait(false);
                var translationResult = await translationTask.ConfigureAwait(false);
                await translationWriter.CompleteAsync(translationPath, pipelineToken).ConfigureAwait(false);
                await PersistStreamingTranslationWorkingArtifactAsync(
                    translationSnapshot.WorkingTranslationPath,
                    translationResult.SourceLanguage,
                    translationResult.TargetLanguage,
                    translationWriter.OrderedSegments,
                    pipelineToken).ConfigureAwait(false);
                await _c.CommitTranslationSessionStateAsync(
                    translationSnapshot,
                    translationResult).ConfigureAwait(false);
                ReportStage(
                    translationStageContext,
                    $"Translation complete. {translationResult.Segments.Count} segments were translated from {translationResult.SourceLanguage} to {translationResult.TargetLanguage}.",
                    progress01: 1,
                    isIndeterminate: false);

                await ttsStageTask.ConfigureAwait(false);
                var segmentAudioPaths = await ttsCollectorTask.ConfigureAwait(false);
                var renderResult = await _c.StitchSegmentClipsAsync(
                    ttsSnapshot,
                    segmentAudioPaths,
                    translationWriter.OrderedSegments,
                    ttsSnapshot.TtsPath,
                    ttsStageContext,
                    pipelineToken).ConfigureAwait(false);
                await _c.CommitTtsSessionStateAsync(
                    ttsSnapshot,
                    renderResult,
                    segmentAudioPaths,
                    null,
                    translationWriter.OrderedSegments,
                    ttsStageContext).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                pipelineCts.Cancel();
                transcriptChannel.Writer.TryComplete(ex);
                capturedFailure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                transcriptChannel.Writer.TryComplete();
                capturedFailure = await ObserveStreamingTaskCompletionAsync(producerTask, "transcript-producer", capturedFailure, pipelineCts).ConfigureAwait(false);
                capturedFailure = await ObserveStreamingTaskCompletionAsync(translationTask, "translation", capturedFailure, pipelineCts).ConfigureAwait(false);
                capturedFailure = await ObserveStreamingTaskCompletionAsync(ttsStageTask, "tts-stage", capturedFailure, pipelineCts).ConfigureAwait(false);
                capturedFailure = await ObserveStreamingTaskCompletionAsync(ttsCollectorTask, "tts-collector", capturedFailure, pipelineCts).ConfigureAwait(false);
            }

            capturedFailure?.Throw();
        }

        /// <summary>
        /// Consumes transcript segments from the provided reader, translates each segment and appends translated segments to the translation writer while updating the streaming translation artifact.
        /// </summary>
        /// <remarks>
        /// Entry state: expects <paramref name="artifactWriter"/> to be initialized for the current session and the translation provider to be ready (translation model/service prepared by the caller).  
        /// Exit state on success: returns a <see cref="TranslationResult"/> built from the in-memory streaming artifact; <paramref name="translationWriter"/> is completed; the method does not commit or persist session state (persistence/commit is performed by the caller).  
        /// Failure behavior: on a translation failure or if a translated segment cannot be resolved from the provider response, the method completes <paramref name="translationWriter"/> with the exception and rethrows; callers should handle transaction/commit accordingly.  
        /// Cancellation: honors <paramref name="cancellationToken"/> for reading, writing and artifact operations and will stop processing when canceled.
        /// </remarks>
        /// <param name="transcriptReader">Reader that yields transcript channel items to translate.</param>
        /// <param name="translationWriter">Writer that receives translated channel items; will be completed by this method.</param>
        /// <param name="artifactWriter">Streaming translation artifact writer used to append pending segments and persist translated segment updates.</param>
        /// <param name="stageContext">Optional context used for progress/stage reporting.</param>
        /// <param name="cancellationToken">Token to cancel ongoing reads, writes and artifact operations.</param>
        /// <returns>A <see cref="TranslationResult"/> representing the completed translation artifact and its segments.</returns>
        private async Task<TranslationResult> RunStreamingTranslationStageAsync(
            TranslationExecutionSnapshot snapshot,
            ChannelReader<TranscriptChannelItem> transcriptReader,
            ChannelWriter<TranslationChannelItem> translationWriter,
            TranslationArtifactStreamingWriter artifactWriter,
            PipelineStageContext? stageContext,
            CancellationToken cancellationToken)
        {
            var completed = 0;
            string? sourceLanguage = null;

            try
            {
                await foreach (var item in transcriptReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    sourceLanguage ??= item.SourceLanguage;
                    await artifactWriter.AppendPendingSegmentAsync(item, cancellationToken).ConfigureAwait(false);
                    var segmentIndex = artifactWriter.IndexOfSegment(item.SegmentId);
                    if (segmentIndex < 0)
                        throw new InvalidOperationException($"Pending segment '{item.SegmentId}' was not found in the streaming translation artifact.");

                    var result = await _c._inferenceEngine.TranslateSingleSegmentTextAsync(
                        snapshot.Provider,
                        new SingleSegmentTranslationTextRequest(
                            item.Segment.Text ?? string.Empty,
                            item.SegmentId,
                            item.SourceLanguage,
                            snapshot.TargetLanguage,
                            snapshot.Model),
                        cancellationToken).ConfigureAwait(false);

                    if (!result.Success)
                    {
                        var errorMsg = result.ErrorMessage ?? "Unknown translation error";
                        _c.Log.Error($"Streaming translation failed: {errorMsg}", new Exception(errorMsg));
                        throw new InvalidOperationException($"Translation failed: {errorMsg}");
                    }

                    var translatedSegment = await artifactWriter.ApplyTranslatedTextAsync(
                        item.SegmentId,
                        result.TranslatedText,
                        result.SourceLanguage,
                        result.TargetLanguage,
                        cancellationToken).ConfigureAwait(false);

                    completed++;
                    ReportStage(
                        stageContext,
                        $"Translated segment {completed}…",
                        progress01: 0,
                        isIndeterminate: true,
                        streamingStatus: "Dub is consuming translated segments in parallel.");
                    await translationWriter.WriteAsync(
                        new TranslationChannelItem(
                            item.SegmentId,
                            translatedSegment,
                            item.SourceLanguage,
                            snapshot.TargetLanguage),
                        cancellationToken).ConfigureAwait(false);
                }

                var source = sourceLanguage ?? snapshot.SourceLanguage;
                return BuildTranslationResult(artifactWriter.OrderedSegments, source, snapshot.TargetLanguage);
            }
            catch (Exception ex)
            {
                translationWriter.TryComplete(ex);
                throw;
            }
            finally
            {
                translationWriter.TryComplete();
            }
        }

        /// <summary>
        /// Consumes translated segments and generates per-segment TTS audio concurrently, emitting TTS results to <paramref name="resultWriter"/>.
        /// </summary>
        /// <remarks>
        /// Entry state: expects an active translation stage producing <paramref name="translationReader"/> items and that TTS provider readiness has been established by the caller.
        /// Exit state on success: all queued translation items have been processed, all per-segment TTS tasks have completed, and <paramref name="resultWriter"/> has been completed successfully.
        /// This method does not persist session state; callers are responsible for committing any session artifacts after completion.
        /// Cancellation: honors <paramref name="cancellationToken"/> for reading, scheduling, and awaiting tasks; cancellation will abort waiting for additional items and propagate OperationCanceledException to the caller.
        /// Concurrency: parallelism is limited to max(1, _c._ttsService?.MaxConcurrency ?? 1). Each scheduled segment generation releases the semaphore when finished so other items can proceed.
        /// Channel semantics: on any unhandled exception the method completes <paramref name="resultWriter"/> with that exception and rethrows; on normal completion it completes <paramref name="resultWriter"/> without error.
        /// </remarks>
        /// <param name="translationReader">Reader providing translated segments to synthesize.</param>
        /// <param name="resultWriter">Writer to receive per-segment TTS results; will be completed by this method.</param>
        /// <param name="cancellationToken">Token to observe for cancellation of reading and task scheduling.</param>
        private async Task RunStreamingTtsStageAsync(
            TtsExecutionSnapshot snapshot,
            ChannelReader<TranslationChannelItem> translationReader,
            ChannelWriter<TtsChannelItem> resultWriter,
            PipelineStageContext? stageContext,
            CancellationToken cancellationToken)
        {
            var parallelism = snapshot.MaxConcurrency;
            using var semaphore = new SemaphoreSlim(parallelism, parallelism);
            var tasks = new List<Task>();

            try
            {
                await foreach (var item in translationReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            await GenerateStreamingTtsSegmentAsync(
                                snapshot,
                                item,
                                resultWriter,
                                stageContext,
                                cancellationToken).ConfigureAwait(false);
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }, cancellationToken));
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);
                resultWriter.TryComplete();
            }
            catch (Exception ex)
            {
                resultWriter.TryComplete(ex);
                throw;
            }
        }

        /// <summary>
        /// Collects TTS segment-generation results and maps successfully produced segment IDs to their audio file paths.
        /// </summary>
        /// <param name="resultReader">Channel reader that yields TTS results; must be completed by producers when no more results will be written.</param>
        /// <param name="stageContext">Optional stage context used to report per-segment progress; may be null.</param>
        /// <param name="cancellationToken">Token to observe for cancellation; if canceled, enumeration stops and an <see cref="OperationCanceledException"/> may be thrown.</param>
        /// <remarks>
        /// Entry state: called while the TTS streaming stage is active and producing results. On success, the method returns a map of segment IDs to verified existing audio file paths and does not persist session state. The method reports progress via <paramref name="stageContext"/> as items are processed. The method observes <paramref name="cancellationToken"/> and will stop iterating if cancellation is requested.
        /// </remarks>
        /// <returns>A thread-safe <see cref="ConcurrentDictionary{TKey, TValue}"/> mapping each segment's ID to its generated audio file path for results whose generation succeeded and whose audio file exists.</returns>
        private async Task<ConcurrentDictionary<string, string>> CollectStreamingTtsResultsAsync(
            ChannelReader<TtsChannelItem> resultReader,
            PipelineStageContext? stageContext,
            CancellationToken cancellationToken)
        {
            var segmentAudioPaths = new ConcurrentDictionary<string, string>();
            var completed = 0;
            var failed = 0;

            await foreach (var item in resultReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                completed++;
                if (item.Result.Success && !string.IsNullOrWhiteSpace(item.Result.AudioPath) && File.Exists(item.Result.AudioPath))
                    segmentAudioPaths[item.SegmentId] = item.Result.AudioPath;
                else
                    failed++;

                var statusMsg = failed > 0
                    ? $"Generated {completed} segment clips ({failed} failed)…"
                    : $"Generated segment clip {completed}…";

                ReportStage(
                    stageContext,
                    statusMsg,
                    progress01: 0,
                    isIndeterminate: true,
                    streamingStatus: "Translation is still feeding new segments downstream.");
            }

            return segmentAudioPaths;
        }

        /// <summary>
        /// Generates a single segment TTS MP3 file and, on success, emits a corresponding TTS result into the provided result channel.
        /// </summary>
        /// <remarks>
        /// Entry/exit state:
        /// - Entry: expects the coordinator's TTS provider and inference engine to be initialized and ready to accept requests.
        /// - Exit on success: a TtsChannelItem referencing a successfully produced audio file is written to <paramref name="resultWriter"/>; otherwise no item is produced and failure is logged.
        /// Persistence:
        /// - The generated MP3 is written to disk under <paramref name="segmentsDir"/>; no session state is persisted by this method.
        /// Cancellation:
        /// - The supplied <paramref name="cancellationToken"/> is propagated to the inference call and the channel write; the method may observe cooperative cancellation and return early if cancelled.
        /// Guard conditions:
        /// - Returns immediately if the segment id or translated text is null/whitespace.
        /// Error handling:
        /// - Failures in generation are caught and logged; exceptions are not rethrown.
        /// </remarks>
        /// <param name="item">The translation channel item containing the segment to synthesize.</param>
        /// <param name="resultWriter">Channel writer to publish successful TTS results (TtsChannelItem).</param>
        /// <param name="cancellationToken">Token used to observe cancellation for the inference call and channel write.</param>
        private async Task GenerateStreamingTtsSegmentAsync(
            TtsExecutionSnapshot snapshot,
            TranslationChannelItem item,
            ChannelWriter<TtsChannelItem> resultWriter,
            PipelineStageContext? stageContext,
            CancellationToken cancellationToken)
        {
            var id = item.SegmentId;
            var text = item.Segment.TranslatedText;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(text))
                return;

            var segmentAudioPath = Path.Combine(snapshot.SegmentsDir, $"{id}.mp3");
            var resolvedVoice = snapshot.ResolveVoiceForSegment(item.Segment);
            var referenceAudioPath = snapshot.ResolveReferenceAudioForSegment(item.Segment);
            var targetDurationSeconds = Math.Max(0, item.Segment.End - item.Segment.Start);
            _c.Log.Info(
                $"Generating TTS for segment {id} (voice={resolvedVoice}, speaker={item.Segment.SpeakerId ?? "<none>"}): {text[..Math.Min(30, text.Length)]}...");

            TtsResult result;
            try
            {
                var task = _c._inferenceEngine.GenerateSegmentTtsAsync(
                    snapshot.Provider,
                    new SingleSegmentTtsRequest(
                        text,
                        segmentAudioPath,
                        resolvedVoice,
                        item.Segment.SpeakerId,
                        referenceAudioPath,
                        Language: snapshot.Language,
                        SourceVideoPath: snapshot.SourceVideoPath,
                        TargetDurationSeconds: targetDurationSeconds,
                        SourceStartSeconds: item.Segment.Start,
                        SourceEndSeconds: item.Segment.End),
                    cancellationToken);
                _c.TrackPendingTtsTask(task);
                result = await _c.AwaitWithTtsHeartbeatAsync(
                    task,
                    stageContext,
                    elapsed => $"Generating dub audio for {id} ({FormatTtsHeartbeatElapsed(elapsed)})…",
                    progressFactory: null,
                    isIndeterminate: true,
                    cancellationToken,
                    elapsed => elapsed.TotalSeconds >= 20 ? "Still generating." : null).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _c.Log.Error($"Streaming TTS generation failed for {id}: {ex.Message}", ex);
                result = new TtsResult(false, string.Empty, resolvedVoice ?? string.Empty, 0, ex.Message);
            }

            var audioPathForResult = result.Success && File.Exists(segmentAudioPath) ? segmentAudioPath : string.Empty;
            if (!result.Success || string.IsNullOrEmpty(audioPathForResult))
                _c.Log.Warning($"Streaming TTS failed or file missing for segment {id}: {result.ErrorMessage}");

            await resultWriter.WriteAsync(
                new TtsChannelItem(
                    id,
                    CloneTranslationSegment(item.Segment),
                    result with { AudioPath = audioPathForResult }),
                cancellationToken).ConfigureAwait(false);
        }

        private async Task<ExceptionDispatchInfo?> ObserveStreamingTaskCompletionAsync(
            Task task,
            string taskName,
            ExceptionDispatchInfo? currentFailure,
            CancellationTokenSource linkedCts)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
            {
                // Expected when upstream failure cancels linked downstream work.
            }
            catch (Exception ex)
            {
                if (currentFailure is null)
                {
                    linkedCts.Cancel();
                    return ExceptionDispatchInfo.Capture(ex);
                }

                _c.Log.Warning($"Streaming downstream task '{taskName}' failed after an earlier error: {ex.Message}");
            }

            return currentFailure;
        }

        /// <summary>
        /// Builds the filesystem path for the current session's transcript artifact and ensures its directory exists.
        /// </summary>
        private string BuildTranscriptArtifactPath(string sourceAudioPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceAudioPath);
            var sessionDir = _c.GetSessionDirectory();
            var transcriptDir = Path.Combine(sessionDir, "transcripts");
            Directory.CreateDirectory(transcriptDir);
            var transcriptStem = SessionWorkflowCoordinator.ResolveTranscriptArtifactStem(
                _c.CurrentSession.IngestedMediaPath,
                sourceAudioPath);
            return Path.Combine(transcriptDir, $"{transcriptStem}.json");
        }

        /// <summary>
        /// Builds the full path for a translation artifact corresponding to a transcript file and ensures the translations directory exists.
        /// </summary>
        /// <param name="transcriptPath">The full path to the transcript JSON artifact.</param>
        /// <param name="targetLanguage">Target language identifier to append to the translation filename (e.g., "en", "fr").</param>
        /// <returns>The full path to the translation JSON file named "{transcriptBase}_{targetLanguage}.json" located in the session's translations directory.</returns>
        private static string BuildTranslationArtifactPath(string transcriptPath, string targetLanguage)
        {
            var translationDir = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(transcriptPath)!)!, "translations");
            Directory.CreateDirectory(translationDir);
            var fileName = Path.GetFileNameWithoutExtension(transcriptPath);
            return Path.Combine(translationDir, $"{fileName}_{targetLanguage}.json");
        }

        private static (string TtsPath, string SegmentsDir) BuildTtsArtifacts(string translationPath, string voice) =>
            SessionWorkflowCoordinator.BuildTtsOutputPaths(translationPath, voice);

        /// <summary>
                /// Builds a successful TranslationResult from a collection of translation artifact segments.
                /// </summary>
                /// <param name="segments">Ordered translation artifact segments to convert into translated segments; each segment's text and translated text will be used (empty string if null).</param>
                /// <param name="sourceLanguage">The detected or specified source language code for the translation result.</param>
                /// <param name="targetLanguage">The target language code for the translation result.</param>
                /// <returns>A <see cref="TranslationResult"/> with Success set to true, the list of translated segments, the provided source and target languages, and a null error.</returns>
                private static TranslationResult BuildTranslationResult(
            IReadOnlyList<TranslationSegmentArtifact> segments,
            string sourceLanguage,
            string targetLanguage) =>
            new(
                true,
                segments.Select(segment => new TranslatedSegment(
                    segment.Start,
                    segment.End,
                    segment.Text ?? string.Empty,
                    segment.TranslatedText ?? string.Empty,
                    segment.SpeakerId)).ToList(),
                sourceLanguage,
                targetLanguage,
                null);

        internal static async Task PersistStreamingTranslationWorkingArtifactAsync(
            string workingTranslationPath,
            string sourceLanguage,
            string targetLanguage,
            IReadOnlyList<TranslationSegmentArtifact> segments,
            CancellationToken cancellationToken)
        {
            var workingArtifact = new TranslationArtifact
            {
                SourceLanguage = sourceLanguage,
                TargetLanguage = targetLanguage,
                Segments = [.. segments],
            };
            await ArtifactPersistence.AtomicWriteTextAsync(
                workingTranslationPath,
                ArtifactJson.SerializeTranslation(workingArtifact),
                cancellationToken).ConfigureAwait(false);
        }

        private string ResolveTranslatedText(
            TranslationResult result,
            int segmentIndex,
            string sourceText,
            string segmentId)
        {
            if (result.Segments.Count == 0)
                throw new InvalidOperationException($"Translation result for segment '{segmentId}' did not contain any segments.");

            if (segmentIndex >= 0 && segmentIndex < result.Segments.Count)
            {
                var indexed = result.Segments[segmentIndex];
                if (!string.IsNullOrWhiteSpace(indexed.TranslatedText))
                    return indexed.TranslatedText;
            }

            if (result.Segments.Count == 1 && !string.IsNullOrWhiteSpace(result.Segments[0].TranslatedText))
            {
                _c.Log.Warning(
                    $"ResolveTranslatedText used fallback='single-segment' for segmentId='{segmentId}', segmentIndex={segmentIndex}, sourceText='{sourceText}'.");
                return result.Segments[0].TranslatedText;
            }

            var byText = result.Segments.FirstOrDefault(segment =>
                string.Equals(segment.Text, sourceText, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(segment.TranslatedText));
            if (byText is not null)
            {
                _c.Log.Warning(
                    $"ResolveTranslatedText used fallback='text-match' for segmentId='{segmentId}', segmentIndex={segmentIndex}, sourceText='{sourceText}'.");
                return byText.TranslatedText;
            }

            throw new InvalidOperationException(
                $"Translation result did not contain a translated value for segment '{segmentId}'.");
        }

        /// <summary>
            /// Create a copy of the provided transcript segment artifact.
            /// </summary>
            /// <param name="segment">The segment to clone.</param>
            /// <returns>A new <see cref="TranscriptSegmentArtifact"/> with the same Start, End, Text, SpeakerId, and OriginalStart values, and a cloned `Words` list if the original had one.</returns>
            private static TranscriptSegmentArtifact CloneTranscriptSegment(TranscriptSegmentArtifact segment) =>
            new()
            {
                Start = segment.Start,
                End = segment.End,
                Text = segment.Text,
                SpeakerId = segment.SpeakerId,
                OriginalStart = segment.OriginalStart,
                Words = segment.Words is null ? null : [.. segment.Words],
            };

        /// <summary>
            /// Creates a new TranslationSegmentArtifact that copies the identifying, timing, text, translation, and speaker fields from the provided segment.
            /// </summary>
            /// <param name="segment">The source segment to clone.</param>
            /// <returns>A new <see cref="TranslationSegmentArtifact"/> with the same Id, Start, End, Text, TranslatedText, and SpeakerId as <paramref name="segment"/>.</returns>
            private static TranslationSegmentArtifact CloneTranslationSegment(TranslationSegmentArtifact segment) =>
                StreamingArtifactCloneHelpers.CloneTranslationSegment(segment);
    }
}
