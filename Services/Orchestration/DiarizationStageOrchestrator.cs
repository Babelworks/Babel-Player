using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Planning;

namespace Babel.Player.Services.Orchestration;

internal sealed class DiarizationStageOrchestrator
{
    private readonly ISessionStateAccessor _session;
    private readonly IStageExecutionPlanner _planner;
    private readonly IDiarizationExecutor _diarizationExecutor;
    private readonly AppLog _log;

    internal DiarizationStageOrchestrator(
        ISessionStateAccessor session,
        IStageExecutionPlanner planner,
        IDiarizationExecutor diarizationExecutor,
        AppLog log)
    {
        _session = session;
        _planner = planner;
        _diarizationExecutor = diarizationExecutor;
        _log = log;
    }

    internal async Task ExecuteAsync(
        PipelineStageContext? stageContext,
        CancellationToken cancellationToken)
    {
        var stagePlan = _planner.ResolveAndApplyExecutionPlan(InferenceStage.Diarization);
        var stageTimer = Stopwatch.StartNew();
        var stageSucceeded = false;

        try
        {
            if (string.IsNullOrWhiteSpace(_session.CurrentSession.IngestedMediaPath))
                throw new InvalidOperationException("No ingested media is available for speaker mapping.");
            if (string.IsNullOrWhiteSpace(_session.CurrentSession.TranscriptPath))
                throw new InvalidOperationException("No transcript is available for speaker mapping.");

            PipelineStageReporter.ReportStage(
                stageContext,
                $"Running {_session.CurrentSettings.DiarizationProvider} diarization to identify speakers before translation and dubbing…",
                progress01: 0,
                isIndeterminate: true);

            var outcome = await _diarizationExecutor.ExecuteDiarizationAsync(
                    _session.CurrentSession.IngestedMediaPath,
                    _session.CurrentSession.TranscriptPath,
                    cancellationToken,
                    resultingStage: SessionWorkflowStage.Diarized,
                    statusMessage: LocalizationService.Instance["Status_ReviewSpeakersThenContinue"])
                .ConfigureAwait(false);

            PipelineStageReporter.ReportStage(
                stageContext,
                $"Speaker mapping complete. Identified {outcome.SpeakerCount} speakers across {outcome.SegmentCount} segments.",
                progress01: 1,
                isIndeterminate: false);
            stageSucceeded = true;
        }
        finally
        {
            _log.Debug(
                $"Stage telemetry stage=diarization success={(stageSucceeded ? "true" : "false")} " +
                $"provider={stagePlan.ProviderId} runtime={stagePlan.Runtime} role={stagePlan.Role} " +
                $"elapsed_ms={stageTimer.ElapsedMilliseconds}");
        }
    }
}
