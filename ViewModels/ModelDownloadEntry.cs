using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Babel.Player.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Babel.Player.ViewModels;

/// <summary>
/// Represents a single locally-hosted model in the Settings → Models tab,
/// tracking its download state and providing Download/Cancel commands.
/// </summary>
public sealed partial class ModelDownloadEntry : ViewModelBase
{
    private readonly ModelDownloader _downloader;
    private readonly Func<Task<bool>> _downloadFunc;
    private readonly Func<bool> _isDownloadedFunc;
    private CancellationTokenSource? _cts;

    public ModelDownloadEntry(
        string providerLabel,
        string modelId,
        Func<bool> isDownloadedFunc,
        Func<IProgress<double>, CancellationToken, Task<bool>> downloadFunc,
        ModelDownloader downloader,
        bool deferInitialStatusRefresh = false)
    {
        ProviderLabel = providerLabel;
        ModelId = modelId;
        _downloader = downloader;
        _isDownloadedFunc = isDownloadedFunc;
        _downloadFunc = () => downloadFunc(
            new Progress<double>(p => Dispatcher.UIThread.Post(() => DownloadProgress = p)),
            _cts!.Token);

        if (deferInitialStatusRefresh)
        {
            // Avoid blocking Models-tab construction on expensive disk hashing (e.g. SortFormer).
            _ = Task.Run(() =>
            {
                bool downloaded = _isDownloadedFunc();
                Dispatcher.UIThread.Post(() =>
                {
                    IsDownloaded = downloaded;
                    ErrorMessage = null;
                });
            });
        }
        else
        {
            RefreshStatus();
        }
    }

    public string ProviderLabel { get; }
    public string ModelId { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    private bool _isDownloaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    [NotifyPropertyChangedFor(nameof(IsProgressVisible))]
    private bool _isDownloading;

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private string? _errorMessage;

    public bool IsProgressVisible => IsDownloading;
    public bool CanDownload => !IsDownloaded && !IsDownloading;
    public bool CanCancel => IsDownloading;

    public string StatusLabel => IsDownloaded ? "✓ Ready"
        : IsDownloading ? "Downloading…"
        : ErrorMessage != null ? $"Failed: {ErrorMessage}"
        : "⬇ Needs Download";

    public void RefreshStatus()
    {
        IsDownloaded = _isDownloadedFunc();
        ErrorMessage = null;
    }

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task Download()
    {
        ErrorMessage = null;
        IsDownloading = true;
        DownloadProgress = 0;
        _cts = new CancellationTokenSource();

        try
        {
            bool ok = await _downloadFunc();
            RefreshStatus();
            if (!ok && !IsDownloaded)
                ErrorMessage = "Download failed. Check logs.";
        }
        catch (OperationCanceledException)
        {
            // user cancelled — leave not-downloaded state
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsDownloading = false;
            DownloadProgress = 0;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _cts?.Cancel();
    }
}
