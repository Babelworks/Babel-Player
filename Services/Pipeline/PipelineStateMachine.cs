using System.Collections.Generic;
using Babel.Player.Models;

namespace Babel.Player.Services.Pipeline;

/// <summary>
/// Encodes allowed pipeline advance steps from <see cref="SessionWorkflowStage"/> and diarization settings.
/// </summary>
internal enum PipelineAdvanceAction
{
    Transcribe,
    Diarize,
    /// <summary>Streaming translation + TTS from an existing transcript (commits translation and dub).</summary>
    TranslateAndDubFromTranscript,
    /// <summary>Generate combined dub from translation artifact.</summary>
    GenerateTts,
}

internal static class PipelineStateMachine
{
    /// <summary>
    /// Determines whether the pipeline should run the full streaming path (ASR → translation → TTS) before performing diarization.
    /// Full streaming path skips separate transcribe/diarize steps in this invocation.
    /// </summary>
    /// <param name="currentStage">The session's current workflow stage.</param>
    /// <param name="shouldRunDiarization">Whether diarization is scheduled for the session.</param>
    /// <returns><c>true</c> if the session is at or past <see cref="SessionWorkflowStage.MediaLoaded"/> but earlier than <see cref="SessionWorkflowStage.Transcribed"/> and diarization is not requested; <c>false</c> otherwise.</returns>
    internal static bool ShouldRunFullStreamingPipelineFirst(
        SessionWorkflowStage currentStage,
        bool shouldRunDiarization) =>
        currentStage >= SessionWorkflowStage.MediaLoaded
        && currentStage < SessionWorkflowStage.Transcribed
        && !shouldRunDiarization;

    /// <summary>
    /// Selects the next pipeline advance action based on the current workflow stage and whether diarization should run.
    /// When diarization is enabled, advance stops at <see cref="SessionWorkflowStage.Diarized"/> so the UI can review
    /// speakers before <see cref="GetContinuationActionAfterDiarized"/> resumes translation and dub.
    /// Returns null when nothing is left to advance.
    /// </summary>
    /// <param name="currentStage">The current session workflow stage used to determine the next action.</param>
    /// <param name="shouldRunDiarization">If true, diarization will be scheduled when applicable; translation/dub are deferred until continue.</param>
    /// <returns>
    /// A <see cref="PipelineAdvanceAction"/> representing the next action to perform, or <c>null</c> if no further advancement is required.
    /// </returns>
    internal static PipelineAdvanceAction? GetNextAdvanceAction(
        SessionWorkflowStage currentStage,
        bool shouldRunDiarization)
    {
        if (currentStage < SessionWorkflowStage.MediaLoaded)
            return null;

        if (currentStage < SessionWorkflowStage.Transcribed)
            return PipelineAdvanceAction.Transcribe;

        if (shouldRunDiarization && currentStage < SessionWorkflowStage.Diarized)
            return PipelineAdvanceAction.Diarize;

        // Multi-speaker UI advance pauses at Diarized for speaker review.
        if (shouldRunDiarization && currentStage == SessionWorkflowStage.Diarized)
            return null;

        if (currentStage < SessionWorkflowStage.Translated)
            return PipelineAdvanceAction.TranslateAndDubFromTranscript;

        if (currentStage < SessionWorkflowStage.TtsGenerated)
            return PipelineAdvanceAction.GenerateTts;

        return null;
    }

    /// <summary>
    /// Stages that a single <c>AdvancePipelineAsync</c> invocation will attempt when starting from <paramref name="currentStage"/>.
    /// When diarization is on, advance ends at <see cref="SessionWorkflowStage.Diarized"/> (translation/dub are continuation stages).
    /// </summary>
    internal static IReadOnlyList<SessionWorkflowStage> GetAdvancePipelineStages(
        SessionWorkflowStage currentStage,
        bool shouldRunDiarization)
    {
        var stages = new List<SessionWorkflowStage>(capacity: shouldRunDiarization ? 2 : 3);
        if (currentStage < SessionWorkflowStage.Transcribed)
            stages.Add(SessionWorkflowStage.Transcribed);

        if (shouldRunDiarization && currentStage < SessionWorkflowStage.Diarized)
            stages.Add(SessionWorkflowStage.Diarized);

        if (!shouldRunDiarization)
        {
            if (currentStage < SessionWorkflowStage.Translated)
                stages.Add(SessionWorkflowStage.Translated);
            if (currentStage < SessionWorkflowStage.TtsGenerated)
                stages.Add(SessionWorkflowStage.TtsGenerated);
        }

        return stages;
    }

    /// <summary>
    /// Determines the next pipeline action to perform after diarization based on the current workflow stage.
    /// Continuation after diarization: translation+dub or TTS-only.
    /// </summary>
    /// <param name="currentStage">The current session workflow stage to evaluate.</param>
    /// <returns>
    /// `PipelineAdvanceAction.TranslateAndDubFromTranscript` if `currentStage` is before `Translated`;
    /// `PipelineAdvanceAction.GenerateTts` if `currentStage` is before `TtsGenerated`;
    /// `null` if no further action is required.
    /// </returns>
    internal static PipelineAdvanceAction? GetContinuationActionAfterDiarized(SessionWorkflowStage currentStage)
    {
        if (currentStage < SessionWorkflowStage.Translated)
            return PipelineAdvanceAction.TranslateAndDubFromTranscript;

        if (currentStage < SessionWorkflowStage.TtsGenerated)
            return PipelineAdvanceAction.GenerateTts;

        return null;
    }
}
