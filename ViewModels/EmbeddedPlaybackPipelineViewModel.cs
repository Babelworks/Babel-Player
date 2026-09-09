using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Resources;
using Babel.Player.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Babel.Player.ViewModels;

public sealed partial class EmbeddedPlaybackPipelineViewModel : ViewModelBase, IDisposable
{
    private readonly EmbeddedPlaybackViewModel _parent;
    private readonly SessionWorkflowCoordinator _coordinator;
    private readonly IPipelineRefreshDialogService? _refreshDialogs;
    private CancellationTokenSource? _pipelineCts;

    internal EmbeddedPlaybackPipelineViewModel(
        EmbeddedPlaybackViewModel parent,
        SessionWorkflowCoordinator coordinator,
        IPipelineRefreshDialogService? refreshDialogs = null)
    {
        _parent = parent;
        _coordinator = coordinator;
        _refreshDialogs = refreshDialogs;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PipelineProgressStatusLine))]
    [NotifyPropertyChangedFor(nameof(ShowPipelineStatusChrome))]
    private double _pipelineProgressPercent;

    private int _pipelineStageIndex;
    private int _pipelineStageCount = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PipelineProgressStatusLine))]
    [NotifyPropertyChangedFor(nameof(ShowPipelineStatusChrome))]
    private bool _isPipelineProgressVisible;

    [ObservableProperty]
    private string _pipelineStageTitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPipelineStatusChrome))]
    private string _pipelineStageDetail = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PipelineProgressStatusLine))]
    private bool _isPipelineProgressIndeterminate;

    public bool ShowPipelineStatusChrome =>
        _parent.IsBusy || IsPipelineProgressVisible || !string.IsNullOrWhiteSpace(PipelineStageDetail);

    public bool CanRunPipeline => !_parent.IsBusy;

    /// <summary>
    /// True when multi-speaker mapping finished and the user should review speakers before continuing.
    /// </summary>
    public bool IsAwaitingSpeakerReview =>
        _coordinator.CurrentSession.Stage == SessionWorkflowStage.Diarized
        && !string.IsNullOrWhiteSpace(_coordinator.CurrentSettings.DiarizationProvider);

    public string RunPipelineButtonLabel =>
        IsAwaitingSpeakerReview
            ? (Strings.ResourceManager.GetString("Button_ContinuePipeline") ?? "Continue")
            : (Strings.ResourceManager.GetString("Button_RunPipeline") ?? "Run Pipeline");

    public bool CanRefreshTranscription =>
        !_parent.IsBusy &&
        !string.IsNullOrWhiteSpace(_coordinator.CurrentSession.IngestedMediaPath) &&
        File.Exists(_coordinator.CurrentSession.IngestedMediaPath!);

    public bool CanRefreshDiarization =>
        !_parent.IsBusy &&
        _coordinator.CurrentSession.Stage >= SessionWorkflowStage.Transcribed &&
        !string.IsNullOrWhiteSpace(_coordinator.CurrentSettings.DiarizationProvider) &&
        !string.IsNullOrWhiteSpace(_coordinator.CurrentSession.TranscriptPath) &&
        File.Exists(_coordinator.CurrentSession.TranscriptPath!);

    public bool CanRefreshTranslation =>
        !_parent.IsBusy &&
        _coordinator.CurrentSession.Stage >= SessionWorkflowStage.Transcribed &&
        !string.IsNullOrWhiteSpace(_coordinator.CurrentSession.TranscriptPath) &&
        File.Exists(_coordinator.CurrentSession.TranscriptPath!);

    public bool CanRefreshDub =>
        !_parent.IsBusy &&
        _coordinator.CurrentSession.Stage >= SessionWorkflowStage.Translated &&
        !string.IsNullOrWhiteSpace(_coordinator.CurrentSession.TranslationPath) &&
        File.Exists(_coordinator.CurrentSession.TranslationPath!);

    public string PipelineProgressStatusLine =>
        !IsPipelineProgressVisible
            ? string.Empty
            : IsPipelineProgressIndeterminate
                ? "Pipeline is active; this stage has not reported a numeric percentage yet."
                : $"Overall pipeline progress: {PipelineProgressPercent:P0} (stage {_pipelineStageIndex} of {_pipelineStageCount}).";

    [RelayCommand(CanExecute = nameof(CanRunPipeline))]
    public async Task RunPipelineAsync()
    {
        var diagnostics = _coordinator.BootstrapDiagnostics;
        if (!diagnostics.AllDependenciesAvailable)
        {
            _parent.StatusText = $"⚠ {diagnostics.DiagnosticSummary}";
            _parent.ClearStatusErrorDetail();
            return;
        }

        _pipelineCts?.Cancel();
        _pipelineCts?.Dispose();
        _pipelineCts = new CancellationTokenSource();
        var cancellationToken = _pipelineCts.Token;
        ResetProgressState();
        var stageProgress = new Progress<SessionWorkflowCoordinator.PipelineStageUpdate>(ApplyStageUpdate);
        var continuing = IsAwaitingSpeakerReview;

        try
        {
            _parent.IsBusy = true;
            _parent.StatusText = continuing
                ? (Strings.ResourceManager.GetString("Status_ContinuingPipeline") ?? "Continuing pipeline…")
                : (Strings.ResourceManager.GetString("Status_RunningPipeline") ?? "Running pipeline…");
            _parent.ClearStatusErrorDetail();

            if (continuing)
            {
                await _coordinator.ContinuePipelineAsync(
                    progress: null,
                    stageProgress: stageProgress,
                    cancellationToken: cancellationToken);
            }
            else
            {
                await _coordinator.AdvancePipelineAsync(
                    progress: null,
                    stageProgress: stageProgress,
                    cancellationToken: cancellationToken);
            }

            ShowRefreshDetail("Loading segments and refreshing playback data…");
            _parent.StatusText = "Loading segments…";
            await _parent.Preview.RefreshSegmentsAsync();
            _parent.StatusText = _coordinator.CurrentSession.StatusMessage;
            _parent.ClearStatusErrorDetail();
            NotifySessionStateChanged();
        }
        catch (OperationCanceledException)
        {
            _parent.StatusText = "Pipeline cancelled.";
            _parent.ClearStatusErrorDetail();
        }
        catch (Exception ex)
        {
            _parent.StatusText = $"Pipeline failed: {ex.Message}";
            _parent.SetStatusErrorDetail("Pipeline failed", ex);
        }
        finally
        {
            _parent.IsBusy = false;
            ResetProgressState();
            _pipelineCts?.Dispose();
            _pipelineCts = null;
            NotifySessionStateChanged();
        }
    }

    [RelayCommand]
    public void ClearPipeline()
    {
        _coordinator.ClearPipeline();
        _parent.Preview.ClearSegments();
        _parent.ResetInteractiveModes();
        _parent.StatusText = "Pipeline cleared. Ready to run fresh.";
        _parent.ClearStatusErrorDetail();
    }

    [RelayCommand]
    public void CancelPipeline()
    {
        if (_pipelineCts is null)
            return;

        _pipelineCts.Cancel();
        _parent.StatusText = "Canceling pipeline...";
        ResetProgressState();
        _parent.ClearStatusErrorDetail();
    }

    [RelayCommand(CanExecute = nameof(CanRefreshTranscription))]
    private async Task RefreshTranscriptionAsync()
    {
        var scope = await GetScopeAsync(PipelineRefreshSection.Transcription).ConfigureAwait(true);
        if (!scope.HasValue)
            return;

        await RunPipelineOperationAsync(
            "Re-running transcription…",
            ct => _coordinator.RerunTranscriptionAsync(
                scope.Value == PipelineRefreshScope.RemainingPipeline,
                new Progress<SessionWorkflowCoordinator.PipelineStageUpdate>(ApplyStageUpdate),
                ct)).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRefreshDiarization))]
    private async Task RefreshDiarizationAsync()
    {
        var scope = await GetScopeAsync(PipelineRefreshSection.Diarization).ConfigureAwait(true);
        if (!scope.HasValue)
            return;

        await RunPipelineOperationAsync(
            $"Running {_parent.ResolveDiarizationProviderLabel()} diarization…",
            ct => _coordinator.RerunDiarizationAsync(
                scope.Value == PipelineRefreshScope.RemainingPipeline,
                new Progress<SessionWorkflowCoordinator.PipelineStageUpdate>(ApplyStageUpdate),
                ct)).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRefreshTranslation))]
    private async Task RefreshTranslationAsync()
    {
        var scope = await GetScopeAsync(PipelineRefreshSection.Translation).ConfigureAwait(true);
        if (!scope.HasValue)
            return;

        await RunPipelineOperationAsync(
            "Re-running translation…",
            ct => _coordinator.RerunTranslationAsync(
                scope.Value == PipelineRefreshScope.RemainingPipeline,
                new Progress<SessionWorkflowCoordinator.PipelineStageUpdate>(ApplyStageUpdate),
                ct)).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRefreshDub))]
    private async Task RefreshDubAsync()
    {
        if (_refreshDialogs is null)
            return;

        if (!await _refreshDialogs.ConfirmRegenerateDubAsync().ConfigureAwait(true))
            return;

        await RunPipelineOperationAsync(
            "Re-generating dub…",
            ct => _coordinator.RerunDubAsync(
                new Progress<SessionWorkflowCoordinator.PipelineStageUpdate>(ApplyStageUpdate),
                ct)).ConfigureAwait(true);
    }

    private async Task<PipelineRefreshScope?> GetScopeAsync(PipelineRefreshSection section)
    {
        if (_refreshDialogs is null)
            return PipelineRefreshScope.ThisStageOnly;

        return await _refreshDialogs.PromptRefreshScopeAsync(section).ConfigureAwait(true);
    }

    private async Task RunPipelineOperationAsync(string status, Func<CancellationToken, Task> run)
    {
        var diagnostics = _coordinator.BootstrapDiagnostics;
        if (!diagnostics.AllDependenciesAvailable)
        {
            _parent.StatusText = $"⚠ {diagnostics.DiagnosticSummary}";
            _parent.ClearStatusErrorDetail();
            return;
        }

        _pipelineCts?.Cancel();
        _pipelineCts?.Dispose();
        _pipelineCts = new CancellationTokenSource();
        var cancellationToken = _pipelineCts.Token;
        ResetProgressState();

        try
        {
            _parent.IsBusy = true;
            _parent.StatusText = status;
            _parent.ClearStatusErrorDetail();
            await run(cancellationToken).ConfigureAwait(true);
            ShowRefreshDetail("Loading segments and refreshing playback data…");
            _parent.StatusText = "Loading segments…";
            await _parent.Preview.RefreshSegmentsAsync().ConfigureAwait(true);
            _parent.StatusText = _coordinator.CurrentSession.StatusMessage;
            _parent.ClearStatusErrorDetail();
        }
        catch (OperationCanceledException)
        {
            _parent.StatusText = "Cancelled.";
            _parent.ClearStatusErrorDetail();
        }
        catch (Exception ex)
        {
            _parent.StatusText = $"Operation failed: {ex.Message}";
            _parent.SetStatusErrorDetail("Pipeline operation failed", ex);
        }
        finally
        {
            _parent.IsBusy = false;
            ResetProgressState();
            _pipelineCts?.Dispose();
            _pipelineCts = null;
        }
    }

    public void NotifyBusyStateChanged()
    {
        RunPipelineCommand.NotifyCanExecuteChanged();
        RefreshTranscriptionCommand.NotifyCanExecuteChanged();
        RefreshDiarizationCommand.NotifyCanExecuteChanged();
        RefreshTranslationCommand.NotifyCanExecuteChanged();
        RefreshDubCommand.NotifyCanExecuteChanged();
    }

    public void NotifySessionStateChanged()
    {
        NotifyBusyStateChanged();
        NotifyPipelineFooterChrome();
        OnPropertyChanged(nameof(IsAwaitingSpeakerReview));
        OnPropertyChanged(nameof(RunPipelineButtonLabel));
    }

    public void NotifyPipelineFooterChrome() => OnPropertyChanged(nameof(ShowPipelineStatusChrome));

    internal void ApplyStageUpdate(SessionWorkflowCoordinator.PipelineStageUpdate update)
    {
        _pipelineStageIndex = update.StageIndex;
        _pipelineStageCount = Math.Max(1, update.StageCount);
        PipelineStageTitle = $"Stage {update.StageIndex} of {update.StageCount}: {update.Title}";
        PipelineStageDetail = string.IsNullOrWhiteSpace(update.StreamingStatus)
            ? update.Detail
            : $"{update.Detail} {update.StreamingStatus}";
        if (update.IsIndeterminate)
        {
            PipelineProgressPercent = _pipelineStageCount > 0
                ? (_pipelineStageIndex - 1d) / _pipelineStageCount
                : 0d;
        }
        else
        {
            var stageFrac = Math.Clamp(update.Progress01, 0d, 1d);
            PipelineProgressPercent = _pipelineStageCount > 0
                ? ((_pipelineStageIndex - 1d) + stageFrac) / _pipelineStageCount
                : stageFrac;
        }

        IsPipelineProgressIndeterminate = update.IsIndeterminate;
        IsPipelineProgressVisible = true;
    }

    internal void ShowRefreshDetail(string detail)
    {
        if (!IsPipelineProgressVisible)
            return;

        PipelineStageDetail = detail;
        PipelineProgressPercent = 1.0;
        IsPipelineProgressIndeterminate = true;
    }

    internal void ResetProgressState()
    {
        PipelineStageTitle = string.Empty;
        PipelineStageDetail = string.Empty;
        PipelineProgressPercent = 0;
        IsPipelineProgressIndeterminate = false;
        IsPipelineProgressVisible = false;
        NotifyPipelineFooterChrome();
    }

    public void Dispose()
    {
        _pipelineCts?.Cancel();
        _pipelineCts?.Dispose();
        _pipelineCts = null;
    }
}
