using Babel.Player.Models;
using Babel.Player.Services.Pipeline;
using Xunit;

namespace BabelPlayer.Tests;

public sealed class PipelineStateMachineTests
{
    [Fact]
    public void ShouldRunFullStreamingPipelineFirst_OnlyWhenPreTranscribeAndNoDiarization()
    {
        Assert.True(PipelineStateMachine.ShouldRunFullStreamingPipelineFirst(SessionWorkflowStage.MediaLoaded, shouldRunDiarization: false));
        Assert.False(PipelineStateMachine.ShouldRunFullStreamingPipelineFirst(SessionWorkflowStage.MediaLoaded, shouldRunDiarization: true));
        Assert.False(PipelineStateMachine.ShouldRunFullStreamingPipelineFirst(SessionWorkflowStage.Transcribed, shouldRunDiarization: false));
    }

    [Fact]
    public void GetNextAdvanceAction_FollowsTranscribeDiarizeThenPauseForReview()
    {
        // Foundation (pre-MediaLoaded) must not advance to Transcribe.
        Assert.Null(PipelineStateMachine.GetNextAdvanceAction(SessionWorkflowStage.Foundation, shouldRunDiarization: false));
        Assert.Null(PipelineStateMachine.GetNextAdvanceAction(SessionWorkflowStage.Foundation, shouldRunDiarization: true));

        Assert.Equal(PipelineAdvanceAction.Transcribe,
            PipelineStateMachine.GetNextAdvanceAction(SessionWorkflowStage.MediaLoaded, shouldRunDiarization: true));

        Assert.Equal(PipelineAdvanceAction.Diarize,
            PipelineStateMachine.GetNextAdvanceAction(SessionWorkflowStage.Transcribed, shouldRunDiarization: true));

        // When diarization is disabled the Transcribed stage should jump straight to translation.
        Assert.Equal(PipelineAdvanceAction.TranslateAndDubFromTranscript,
            PipelineStateMachine.GetNextAdvanceAction(SessionWorkflowStage.Transcribed, shouldRunDiarization: false));

        // Multi-speaker advance pauses at Diarized for speaker review.
        Assert.Null(PipelineStateMachine.GetNextAdvanceAction(SessionWorkflowStage.Diarized, shouldRunDiarization: true));

        // Without diarization, Diarized is not a pause point; translate/dub can still advance.
        Assert.Equal(PipelineAdvanceAction.TranslateAndDubFromTranscript,
            PipelineStateMachine.GetNextAdvanceAction(SessionWorkflowStage.Diarized, shouldRunDiarization: false));

        Assert.Equal(PipelineAdvanceAction.GenerateTts,
            PipelineStateMachine.GetNextAdvanceAction(SessionWorkflowStage.Translated, shouldRunDiarization: true));

        Assert.Null(PipelineStateMachine.GetNextAdvanceAction(SessionWorkflowStage.TtsGenerated, shouldRunDiarization: true));
    }

    [Fact]
    public void GetAdvancePipelineStages_MultiSpeakerStopsAtDiarized()
    {
        var stages = PipelineStateMachine.GetAdvancePipelineStages(
            SessionWorkflowStage.MediaLoaded,
            shouldRunDiarization: true);

        Assert.Equal(
            [SessionWorkflowStage.Transcribed, SessionWorkflowStage.Diarized],
            stages);
    }

    [Fact]
    public void GetAdvancePipelineStages_SingleSpeakerIncludesTranslateAndTts()
    {
        var stages = PipelineStateMachine.GetAdvancePipelineStages(
            SessionWorkflowStage.MediaLoaded,
            shouldRunDiarization: false);

        Assert.Equal(
            [
                SessionWorkflowStage.Transcribed,
                SessionWorkflowStage.Translated,
                SessionWorkflowStage.TtsGenerated,
            ],
            stages);
    }

    [Fact]
    public void GetContinuationActionAfterDiarized_MatchesPostDiarizationFlow()
    {
        Assert.Equal(PipelineAdvanceAction.TranslateAndDubFromTranscript,
            PipelineStateMachine.GetContinuationActionAfterDiarized(SessionWorkflowStage.Diarized));

        Assert.Equal(PipelineAdvanceAction.GenerateTts,
            PipelineStateMachine.GetContinuationActionAfterDiarized(SessionWorkflowStage.Translated));

        Assert.Null(PipelineStateMachine.GetContinuationActionAfterDiarized(SessionWorkflowStage.TtsGenerated));
    }
}
