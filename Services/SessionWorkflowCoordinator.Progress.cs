using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Pipeline;
using SharedOrchestration = Babel.Player.Services.Orchestration;

namespace Babel.Player.Services;

public sealed partial class SessionWorkflowCoordinator
{
    private static readonly TimeSpan TtsHeartbeatInterval = TimeSpan.FromSeconds(10);

    internal sealed record PipelineStageUpdate(
        int StageIndex,
        int StageCount,
        SessionWorkflowStage TargetStage,
        string Title,
        string Detail,
        double Progress01,
        bool IsIndeterminate,
        string? StreamingStatus = null)
    {
        internal SharedOrchestration.PipelineStageUpdate ToShared() =>
            new(
                StageIndex,
                StageCount,
                TargetStage,
                Title,
                Detail,
                Progress01,
                IsIndeterminate,
                StreamingStatus);

        internal static PipelineStageUpdate FromShared(SharedOrchestration.PipelineStageUpdate update) =>
            new(
                update.StageIndex,
                update.StageCount,
                update.TargetStage,
                update.Title,
                update.Detail,
                update.Progress01,
                update.IsIndeterminate,
                update.StreamingStatus);
    }

    internal readonly record struct PipelineStageContext(
        int StageIndex,
        int StageCount,
        SessionWorkflowStage TargetStage,
        string Title,
        IProgress<PipelineStageUpdate>? Reporter)
    {
        internal SharedOrchestration.PipelineStageContext ToShared()
        {
            var reporterTarget = Reporter;
            IProgress<SharedOrchestration.PipelineStageUpdate>? reporter = reporterTarget is null
                ? null
                : new Progress<SharedOrchestration.PipelineStageUpdate>(
                    update => reporterTarget.Report(PipelineStageUpdate.FromShared(update)));
            return new SharedOrchestration.PipelineStageContext(
                StageIndex,
                StageCount,
                TargetStage,
                Title,
                reporter);
        }

        internal static PipelineStageContext? FromShared(SharedOrchestration.PipelineStageContext? context)
        {
            if (context is not { } shared)
                return null;

            IProgress<PipelineStageUpdate>? reporter = shared.Reporter is null
                ? null
                : new Progress<PipelineStageUpdate>(
                    update => shared.Reporter.Report(update.ToShared()));
            return new PipelineStageContext(
                shared.StageIndex,
                shared.StageCount,
                shared.TargetStage,
                shared.Title,
                reporter);
        }
    }

    private static IReadOnlyList<SessionWorkflowStage> GetAdvancePipelineStages(
        SessionWorkflowStage currentStage,
        bool shouldRunDiarization) =>
        PipelineStateMachine.GetAdvancePipelineStages(currentStage, shouldRunDiarization);

    private static IReadOnlyList<SessionWorkflowStage> GetContinuationPipelineStages(SessionWorkflowStage currentStage)
    {
        var stages = new List<SessionWorkflowStage>(capacity: 2);
        if (currentStage < SessionWorkflowStage.Translated)
            stages.Add(SessionWorkflowStage.Translated);
        if (currentStage < SessionWorkflowStage.TtsGenerated)
            stages.Add(SessionWorkflowStage.TtsGenerated);
        return stages;
    }

    private static PipelineStageContext? GetStageContext(
        IReadOnlyList<SessionWorkflowStage> remainingStages,
        SessionWorkflowStage targetStage,
        IProgress<PipelineStageUpdate>? stageProgress)
    {
        if (stageProgress is null)
            return null;

        var stageIndex = -1;
        for (var i = 0; i < remainingStages.Count; i++)
        {
            if (remainingStages[i] == targetStage)
            {
                stageIndex = i;
                break;
            }
        }
        if (stageIndex < 0)
            return null;

        return new PipelineStageContext(
            stageIndex + 1,
            remainingStages.Count,
            targetStage,
            GetPipelineStageTitle(targetStage),
            stageProgress);
    }

    private static string GetPipelineStageTitle(SessionWorkflowStage stage) =>
        SharedOrchestration.PipelineStageReporter.GetPipelineStageTitle(stage);

    private static void ReportStage(
        PipelineStageContext? context,
        string detail,
        double progress01,
        bool isIndeterminate,
        string? streamingStatus = null) =>
        SharedOrchestration.PipelineStageReporter.ReportStage(
            context is { } stageContext ? stageContext.ToShared() : null,
            detail,
            progress01,
            isIndeterminate,
            streamingStatus);

    private static IProgress<double>? CreateStageDownloadProgress(
        PipelineStageContext? context,
        IProgress<double>? rawProgress,
        string detailPrefix) =>
        SharedOrchestration.PipelineStageReporter.CreateStageDownloadProgress(
            context is { } stageContext ? stageContext.ToShared() : null,
            rawProgress,
            detailPrefix);

    private async Task<T> AwaitWithTtsHeartbeatAsync<T>(
        Task<T> work,
        PipelineStageContext? context,
        Func<TimeSpan, string> detailFactory,
        Func<double>? progressFactory,
        bool isIndeterminate,
        CancellationToken cancellationToken,
        Func<TimeSpan, string?>? streamingStatusFactory = null)
    {
        if (context is null)
            return await work.ConfigureAwait(false);

        var startedAt = DateTimeOffset.UtcNow;
        while (true)
        {
            var delayTask = Task.Delay(TtsHeartbeatInterval, cancellationToken);
            var completedTask = await Task.WhenAny(work, delayTask).ConfigureAwait(false);
            if (completedTask == work)
                return await work.ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            var elapsed = DateTimeOffset.UtcNow - startedAt;
            ReportStage(
                context,
                detailFactory(elapsed),
                progressFactory?.Invoke() ?? 0,
                isIndeterminate,
                streamingStatusFactory?.Invoke(elapsed));
        }
    }

    private static string FormatTtsHeartbeatElapsed(TimeSpan elapsed)
    {
        var seconds = Math.Max(1, (int)Math.Floor(elapsed.TotalSeconds));
        return $"{seconds}s";
    }
}
