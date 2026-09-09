using System;
using System.IO;
using System.Linq;
using System.Text;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Credentials;
using Babel.Player.ViewModels;

namespace Babel.Player.Views;

public partial class MainWindow : Window
{
    private const double ControlsActivityDebounceMs = 75;
    private LibMpvEmbeddedTransport? _embeddedTransport;

    private PropertyChangedEventHandler? _playbackPropertyChangedHandler;
    private PropertyChangedEventHandler? _coordinatorPropertyChangedHandler;
    private EventHandler<PointerEventArgs>? _videoOverlayPointerMovedHandler;
    private EventHandler<SizeChangedEventArgs>? _videoViewSizeChangedHandler;
    private EventHandler? _videoNativePointerActivityHandler;
    private EventHandler<PixelPointEventArgs>? _windowPositionChangedHandler;
    private EventHandler? _windowScalingChangedHandler;
    private EventHandler? _screensChangedHandler;
    private EventHandler<SizeChangedEventArgs>? _playerChromeWidthHostSizeChangedHandler;
    private EventHandler<SizeChangedEventArgs>? _wideVideoChromeLayoutSizeChangedHandler;
    private bool _playerChromeWidthHostSizeHooked;
    private EventHandler<SizeChangedEventArgs>? _paneLayoutHostSizeChangedHandler;
    private double _wideVideoChromeRequiredWidth;
    private Screens? _subscribedScreens;
    private long _lastControlsActivityTickMs;
    private bool _isApplyingWindowStateFromViewModel;
    private bool _isApplyingFullscreenFromWindowState;
    private SpeakerReferenceWizardWindow? _speakerWizardWindow;
    private Control? _activePaneSplitter;
    private Control? _paneLayoutHostForDrag;
    private bool _activePaneSplitterIsLeft;
    private double _paneSplitterStartWidth;
    private double _paneSplitterDragStartX;

    public MainWindow()
    {
        InitializeComponent();

        // Keep the title in sync with the build configuration.
        Title = AppIdentity.AppName;

        // Enable drag & drop on the window
        DragDrop.AddDragOverHandler(this, OnDragEnter);
        DragDrop.AddDropHandler(this, OnFileDrop);

#if BABEL_DEV
        WireDevToolbarClick("DevLogButton", OnDevLogClick);
        WireDevToolbarClick("DevLogButtonCompact", OnDevLogClick);
        WireDevToolbarClick("FreshStartButton", OnFreshStartClick);
        WireDevToolbarClick("FreshStartButtonCompact", OnFreshStartClick);
#endif

        var videoView = this.FindControl<MpvVideoView>("VideoView");
        if (videoView is not null)
        {
            videoView.HandleReady += OnVideoHandleReady;
            _videoViewSizeChangedHandler = OnVideoViewSizeChanged;
            videoView.SizeChanged += _videoViewSizeChangedHandler;
            _videoNativePointerActivityHandler = OnVideoNativePointerActivity;
            videoView.NativePointerActivity += _videoNativePointerActivityHandler;
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainWindowViewModel vm)
        {
            _playbackPropertyChangedHandler = OnPlaybackPropertyChanged;
            vm.Playback.Preview.PropertyChanged += _playbackPropertyChangedHandler;
            _coordinatorPropertyChangedHandler = OnCoordinatorPropertyChanged;
            vm.Coordinator.PropertyChanged += _coordinatorPropertyChangedHandler;

            var overlay = this.FindControl<Panel>("VideoOverlayPanel");
            if (overlay is not null)
            {
                _videoOverlayPointerMovedHandler = OnVideoAreaPointerMoved;
                overlay.PointerMoved += _videoOverlayPointerMovedHandler;
            }
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (DataContext is MainWindowViewModel vm)
            _ = vm.TryShowManagedBackendWarmupNoticeAsync();

        _windowPositionChangedHandler ??= OnWindowPositionChanged;
        _windowScalingChangedHandler ??= OnWindowMetricsChanged;
        _screensChangedHandler ??= OnScreensChanged;

        PositionChanged += _windowPositionChangedHandler;
        ScalingChanged += _windowScalingChangedHandler;

        _subscribedScreens = Screens;
        _subscribedScreens.Changed += _screensChangedHandler;

        var videoView = this.FindControl<MpvVideoView>("VideoView");
        if (videoView is not null)
            UpdateEmbeddedTransportViewportMetrics(videoView);

        SyncChromeWindowState();
        WireVideoChromeCompactState();
        WirePaneLayoutHost();
    }

    private void SyncChromeWindowState()
    {
        var maxIcon = this.FindControl<Control>("ChromeMaximizeIcon");
        var restoreIcon = this.FindControl<Control>("ChromeRestoreIcon");
        if (maxIcon is null || restoreIcon is null)
            return;

        var maximized = WindowState == WindowState.Maximized;
        maxIcon.IsVisible = !maximized;
        restoreIcon.IsVisible = maximized;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == WindowStateProperty)
            SyncChromeWindowState();

        if (change.Property != WindowStateProperty || _isApplyingWindowStateFromViewModel)
            return;

        if (DataContext is not MainWindowViewModel vm)
            return;

        var isFullscreen = WindowState == WindowState.FullScreen;
        if (vm.Playback.Preview.IsFullscreen == isFullscreen)
            return;

        _isApplyingFullscreenFromWindowState = true;
        vm.Playback.Preview.IsFullscreen = isFullscreen;
        _isApplyingFullscreenFromWindowState = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        if (_windowPositionChangedHandler is not null)
        {
            PositionChanged -= _windowPositionChangedHandler;
        }

        if (_windowScalingChangedHandler is not null)
        {
            ScalingChanged -= _windowScalingChangedHandler;
        }

        if (_subscribedScreens is not null && _screensChangedHandler is not null)
            _subscribedScreens.Changed -= _screensChangedHandler;

        if (DataContext is MainWindowViewModel vm)
        {
            if (_playbackPropertyChangedHandler is not null)
                vm.Playback.Preview.PropertyChanged -= _playbackPropertyChangedHandler;
            if (_coordinatorPropertyChangedHandler is not null)
                vm.Coordinator.PropertyChanged -= _coordinatorPropertyChangedHandler;

            var overlay = this.FindControl<Panel>("VideoOverlayPanel");
            if (overlay is not null && _videoOverlayPointerMovedHandler is not null)
                overlay.PointerMoved -= _videoOverlayPointerMovedHandler;
        }

        var videoView = this.FindControl<MpvVideoView>("VideoView");
        if (videoView is not null)
        {
            videoView.HandleReady -= OnVideoHandleReady;
            if (_videoViewSizeChangedHandler is not null)
                videoView.SizeChanged -= _videoViewSizeChangedHandler;
            if (_videoNativePointerActivityHandler is not null)
                videoView.NativePointerActivity -= _videoNativePointerActivityHandler;
        }

        var playerChromeWidthHost = this.FindControl<Control>("PlayerChromeWidthHost");
        if (playerChromeWidthHost is not null && _playerChromeWidthHostSizeChangedHandler is not null)
            playerChromeWidthHost.SizeChanged -= _playerChromeWidthHostSizeChangedHandler;

        var wideVideoChromeLayout = this.FindControl<Control>("WideVideoChromeLayoutRoot");
        if (wideVideoChromeLayout is not null && _wideVideoChromeLayoutSizeChangedHandler is not null)
            wideVideoChromeLayout.SizeChanged -= _wideVideoChromeLayoutSizeChangedHandler;

        var paneLayoutHost = this.FindControl<Control>("PaneLayoutHost");
        if (paneLayoutHost is not null && _paneLayoutHostSizeChangedHandler is not null)
            paneLayoutHost.SizeChanged -= _paneLayoutHostSizeChangedHandler;

        _playbackPropertyChangedHandler = null;
        _coordinatorPropertyChangedHandler = null;
        _videoOverlayPointerMovedHandler = null;
        _videoViewSizeChangedHandler = null;
        _videoNativePointerActivityHandler = null;
        _windowPositionChangedHandler = null;
        _windowScalingChangedHandler = null;
        _screensChangedHandler = null;
        _subscribedScreens = null;
        _playerChromeWidthHostSizeChangedHandler = null;
        _wideVideoChromeLayoutSizeChangedHandler = null;
        _paneLayoutHostSizeChangedHandler = null;
    }

    private void WireVideoChromeCompactState()
    {
        var playerChromeWidthHost = this.FindControl<Control>("PlayerChromeWidthHost");
        var wideVideoChromeLayout = this.FindControl<Control>("WideVideoChromeLayoutRoot");
        if (playerChromeWidthHost is null || wideVideoChromeLayout is null)
            return;

        _playerChromeWidthHostSizeChangedHandler ??= OnPlayerChromeWidthHostSizeChanged;
        if (!_playerChromeWidthHostSizeHooked)
        {
            playerChromeWidthHost.SizeChanged += _playerChromeWidthHostSizeChangedHandler;
            _playerChromeWidthHostSizeHooked = true;
        }

        _wideVideoChromeLayoutSizeChangedHandler ??= OnWideVideoChromeLayoutSizeChanged;
        wideVideoChromeLayout.SizeChanged -= _wideVideoChromeLayoutSizeChangedHandler;
        wideVideoChromeLayout.SizeChanged += _wideVideoChromeLayoutSizeChangedHandler;

        CacheWideVideoChromeRequiredWidth(forceMeasure: true);
        UpdateVideoChromeCompactState();
    }

    private void OnPlayerChromeWidthHostSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateVideoChromeCompactState();
    }

    private void OnWideVideoChromeLayoutSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        CacheWideVideoChromeRequiredWidth();
        UpdateVideoChromeCompactState();
    }

    private void UpdateVideoChromeCompactState()
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        var playerChromeWidthHost = this.FindControl<Control>("PlayerChromeWidthHost");
        var wideVideoChromeLayout = this.FindControl<Control>("WideVideoChromeLayoutRoot");
        if (playerChromeWidthHost is null || wideVideoChromeLayout is null)
            return;

        var availableWidth = playerChromeWidthHost.Bounds.Width;
        if (availableWidth <= 0)
            return;

        var requiredWidth = CacheWideVideoChromeRequiredWidth();
        if (requiredWidth <= 0)
            return;

        const double hysteresisPx = 24;
        var shouldCompact = vm.Playback.Preview.IsCompactVideoChrome
            ? availableWidth < requiredWidth + hysteresisPx
            : availableWidth < requiredWidth;

        vm.Playback.Preview.IsCompactVideoChrome = shouldCompact;
    }

    /// <summary>
    /// Clears the cached wide-chrome width so the next measure can shrink the threshold when
    /// intrinsic layout tightens (dub off, panes hidden, exiting fullscreen).
    /// </summary>
    private void InvalidateWideVideoChromeRequiredWidth() => _wideVideoChromeRequiredWidth = 0;

    private double CacheWideVideoChromeRequiredWidth(bool forceMeasure = false)
    {
        var wideVideoChromeLayout = this.FindControl<Control>("WideVideoChromeLayoutRoot");
        if (wideVideoChromeLayout is null)
            return 0;

        if (forceMeasure || _wideVideoChromeRequiredWidth <= 0)
        {
            wideVideoChromeLayout.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _wideVideoChromeRequiredWidth = Math.Max(
                wideVideoChromeLayout.DesiredSize.Width,
                wideVideoChromeLayout.Bounds.Width);
            return _wideVideoChromeRequiredWidth;
        }

        // Keep the cached width current when the layout naturally grows, but avoid
        // shrinking it on constrained drag frames where Bounds width reflects the
        // current squeeze instead of the wide layout's true fit requirement.
        var observedWidth = Math.Max(wideVideoChromeLayout.DesiredSize.Width, wideVideoChromeLayout.Bounds.Width);
        if (observedWidth > _wideVideoChromeRequiredWidth)
            _wideVideoChromeRequiredWidth = observedWidth;

        return _wideVideoChromeRequiredWidth;
    }

    private void WirePaneLayoutHost()
    {
        var paneLayoutHost = this.FindControl<Control>("PaneLayoutHost");
        if (paneLayoutHost is null)
            return;

        _paneLayoutHostSizeChangedHandler ??= OnPaneLayoutHostSizeChanged;
        paneLayoutHost.SizeChanged -= _paneLayoutHostSizeChangedHandler;
        paneLayoutHost.SizeChanged += _paneLayoutHostSizeChangedHandler;
    }

    private void OnPaneLayoutHostSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        // Drag clamping uses the live host width. No-op here beyond keeping the
        // reference available for future updates without relying on a magic width.
    }

    private void OnVideoAreaPointerMoved(object? sender, PointerEventArgs e)
    {
        NotifyControlsActivityDebounced();
    }

    private void OnWindowSurfacePointerMoved(object? sender, PointerEventArgs e)
    {
        NotifyControlsActivityDebounced();
    }

    private void OnVideoNativePointerActivity(object? sender, EventArgs e)
    {
        NotifyControlsActivityDebounced();
    }

    private void NotifyControlsActivityDebounced()
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        var nowMs = Environment.TickCount64;
        if (nowMs - _lastControlsActivityTickMs < ControlsActivityDebounceMs)
            return;

        _lastControlsActivityTickMs = nowMs;
        vm.Playback.Preview.NotifyControlsActivity();
    }

    private void OnPlaybackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(EmbeddedPlaybackPreviewViewModel.SelectedSegment):
                var item = (sender as EmbeddedPlaybackPreviewViewModel)?.SelectedSegment;
                if (item != null)
                    this.FindControl<ListBox>("SegmentList")?.ScrollIntoView(item);
                break;
            case nameof(EmbeddedPlaybackPreviewViewModel.IsDubModeOn):
            case nameof(EmbeddedPlaybackPreviewViewModel.IsPipelinePaneVisible):
            case nameof(EmbeddedPlaybackPreviewViewModel.IsSegmentsPaneVisible):
            case nameof(EmbeddedPlaybackPreviewViewModel.SwapPaneSides):
                InvalidateWideVideoChromeRequiredWidth();
                UpdateVideoChromeCompactState();
                break;
            case nameof(EmbeddedPlaybackPreviewViewModel.IsFullscreen):
                if (DataContext is MainWindowViewModel vm && !_isApplyingFullscreenFromWindowState)
                {
                    var desiredState = vm.Playback.Preview.IsFullscreen ? WindowState.FullScreen : WindowState.Normal;
                    if (WindowState != desiredState)
                    {
                        _isApplyingWindowStateFromViewModel = true;
                        WindowState = desiredState;
                        _isApplyingWindowStateFromViewModel = false;
                    }
                }

                InvalidateWideVideoChromeRequiredWidth();
                UpdateVideoChromeCompactState();
                break;
        }
    }

    private void OnCoordinatorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionWorkflowCoordinator.PendingMediaReloadRequest))
            TryApplyPendingMediaReloadRequest();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Handled || DataContext is not MainWindowViewModel vm)
            return;

        var preview = vm.Playback.Preview;
        var focusedElement = FocusManager?.GetFocusedElement();
        if (!MainWindowShortcutRouter.TryMap(e.Key, e.KeyModifiers, focusedElement, preview.IsFullscreen, out var action))
            return;

        var handled = action switch
        {
            MainWindowShortcutAction.PlayPause => TryExecuteShortcut(preview.IsSourceMediaLoaded, preview.PlayPauseSourceCommand),
            MainWindowShortcutAction.ToggleSubtitles => TryExecuteShortcut(preview.HasSegments, preview.ToggleSubtitlesCommand),
            MainWindowShortcutAction.ToggleLeftPane => TryExecuteShortcut(!preview.IsFullscreen, preview.ToggleLeftPaneCommand),
            MainWindowShortcutAction.ToggleRightPane => TryExecuteShortcut(!preview.IsFullscreen, preview.ToggleRightPaneCommand),
            MainWindowShortcutAction.ToggleDubMode => TryExecuteShortcut(preview.HasSegments, preview.ToggleDubModeCommand),
            MainWindowShortcutAction.ToggleFullscreen => TryExecuteShortcut(preview.IsSourceMediaLoaded, preview.ToggleFullscreenCommand),
            MainWindowShortcutAction.ExitFullscreen => TryExitFullscreen(preview),
            _ => false,
        };

        if (handled)
            e.Handled = true;
    }

    private static bool TryExecuteShortcut(bool isEnabled, ICommand? command)
    {
        if (!isEnabled || command is null || !command.CanExecute(null))
            return false;

        command.Execute(null);
        return true;
    }

    private static bool TryExitFullscreen(EmbeddedPlaybackPreviewViewModel preview)
    {
        if (!preview.IsFullscreen)
            return false;

        preview.IsFullscreen = false;
        return true;
    }

    public void OnPaneSplitterPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control splitter
            || DataContext is not MainWindowViewModel vm
            || !e.GetCurrentPoint(splitter).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            if (splitter.Name == "LeftPaneSplitter")
                vm.Playback.Preview.ResetLeftPaneWidthCommand.Execute(null);
            else
                vm.Playback.Preview.ResetRightPaneWidthCommand.Execute(null);

            e.Handled = true;
            return;
        }

        _activePaneSplitter = splitter;
        _activePaneSplitterIsLeft = splitter.Name == "LeftPaneSplitter";
        _paneSplitterDragStartX = e.GetPosition(this).X;
        _paneSplitterStartWidth = _activePaneSplitterIsLeft
            ? vm.Playback.Preview.LeftPaneWidth
            : vm.Playback.Preview.RightPaneWidth;
        _paneLayoutHostForDrag = this.FindControl<Control>("PaneLayoutHost");
        e.Pointer.Capture(splitter);
        e.Handled = true;
    }

    public void OnPaneSplitterPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_activePaneSplitter is null
            || DataContext is not MainWindowViewModel vm
            || GetPaneLayoutHostWidth() <= 0)
        {
            return;
        }

        var delta = e.GetPosition(this).X - _paneSplitterDragStartX;
        var desiredWidth = _activePaneSplitterIsLeft
            ? _paneSplitterStartWidth + delta
            : _paneSplitterStartWidth - delta;
        var hostWidth = GetPaneLayoutHostWidth();
        if (_activePaneSplitterIsLeft)
            vm.Playback.Preview.ResizeLeftPane(desiredWidth, hostWidth);
        else
            vm.Playback.Preview.ResizeRightPane(desiredWidth, hostWidth);

        e.Handled = true;
    }

    public void OnPaneSplitterPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_activePaneSplitter is null || DataContext is not MainWindowViewModel vm)
            return;

        vm.Playback.Preview.CommitPaneLayout();
        ClearActivePaneSplitter();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    public void OnPaneSplitterPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_activePaneSplitter is null)
            return;

        if (DataContext is MainWindowViewModel vm)
            vm.Playback.Preview.CommitPaneLayout();

        ClearActivePaneSplitter();
    }

    private double GetPaneLayoutHostWidth()
    {
        return _paneLayoutHostForDrag?.Bounds.Width ?? 0;
    }

    private void ClearActivePaneSplitter()
    {
        _activePaneSplitter = null;
        _paneLayoutHostForDrag = null;
        _paneSplitterStartWidth = 0;
        _paneSplitterDragStartX = 0;
    }

    // ── Drag & Drop file support ──────────────────────────────────────────────────
    private static readonly string[] SupportedVideoExtensions = ["mp4", "mkv", "avi", "webm", "mov"];
    private static readonly string[] SupportedAudioExtensions = ["wav", "mp3", "flac", "ogg", "m4a"];

    private async void OnFileDrop(object? sender, DragEventArgs e)
    {
        var dataTransfer = e.DataTransfer;
        if (dataTransfer == null) return;
        
        var files = dataTransfer.TryGetFiles();
        if (files == null) return;
        
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (path == null) continue;
            
            var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            if (SupportedVideoExtensions.Contains(ext) || SupportedAudioExtensions.Contains(ext))
            {
                if (DataContext is MainWindowViewModel vm)
                {
                    try
                    {
                        vm.Coordinator.LoadMedia(path);
                        return;
                    }
                    catch (Exception ex)
                    {
                        vm.Playback.StatusText = $"Failed to open: {ex.Message}";
                    }
                }
            }
        }
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        var dataTransfer = e.DataTransfer;
        if (dataTransfer == null) 
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        
        var files = dataTransfer.TryGetFiles();
        if (files != null)
        {
            foreach (var file in files)
            {
                var path = file.TryGetLocalPath();
                if (path == null) continue;
                
                var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
                if (SupportedVideoExtensions.Contains(ext) || SupportedAudioExtensions.Contains(ext))
                {
                    e.DragEffects = DragDropEffects.Copy;
                    return;
                }
            }
        }
        e.DragEffects = DragDropEffects.None;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
    }

    // ── Video handle + media loading ───────────────────────────────────────────

    private void OnVideoHandleReady(object? sender, IntPtr hwnd)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            try
            {
                var player = vm.Coordinator.GetOrCreateSourcePlayer();
                if (player is LibMpvEmbeddedTransport embedded)
                {
                    _embeddedTransport = embedded;
                    embedded.AttachToWindow(hwnd);
                    if (sender is MpvVideoView videoView)
                        UpdateEmbeddedTransportViewportMetrics(videoView);
                    TryApplyPendingMediaReloadRequest();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Video init error: {ex.Message}");
            }
        }
    }

    private void OnVideoViewSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is MpvVideoView videoView)
            UpdateEmbeddedTransportViewportMetrics(videoView);
    }

    private void OnWindowPositionChanged(object? sender, PixelPointEventArgs e)
    {
        OnWindowMetricsChanged(sender, EventArgs.Empty);
    }

    private void OnWindowMetricsChanged(object? sender, EventArgs e)
    {
        var videoView = this.FindControl<MpvVideoView>("VideoView");
        if (videoView is not null)
            UpdateEmbeddedTransportViewportMetrics(videoView);
    }

    private void OnScreensChanged(object? sender, EventArgs e)
    {
        var videoView = this.FindControl<MpvVideoView>("VideoView");
        if (videoView is not null)
            UpdateEmbeddedTransportViewportMetrics(videoView);
    }

    private void UpdateEmbeddedTransportViewportMetrics(MpvVideoView videoView)
    {
        if (_embeddedTransport is null)
            return;

        var scale = TopLevel.GetTopLevel(videoView)?.RenderScaling ?? 1.0;
        var width = Math.Max(0, (int)Math.Round(videoView.Bounds.Width * scale));
        var height = Math.Max(0, (int)Math.Round(videoView.Bounds.Height * scale));
        _embeddedTransport.SetDisplaySize(width, height);

        var screen = Screens.ScreenFromWindow(this);
        _embeddedTransport.SetMonitorResolution(screen?.Bounds.Width ?? 0, screen?.Bounds.Height ?? 0);
    }

    private static readonly string[] VideoExtensions = ["*.mp4", "*.mkv", "*.avi", "*.webm", "*.mov"];
    private static readonly string[] AudioExtensions = ["*.wav", "*.mp3", "*.flac", "*.ogg", "*.m4a"];
    private static readonly string[] AllFilesPattern = ["*.*"];
    private static readonly string[] SrtPattern = ["*.srt"];

    public async void OnOpenMediaClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Media File",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Video Files") { Patterns = VideoExtensions },
                new FilePickerFileType("All Files") { Patterns = AllFilesPattern },
            ]
        });

        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        if (DataContext is not MainWindowViewModel vm) return;

        try
        {
            vm.Coordinator.LoadMedia(path);
        }
        catch (Exception ex)
        {
            vm.Playback.StatusText = $"Failed to open: {ex.Message}";
        }
    }

    /// <summary>
    /// Opens a file picker to select an audio reference clip for the currently selected speaker and assigns the chosen file to that speaker.
    /// </summary>
    /// <remarks>
    /// If the window's DataContext is not a MainWindowViewModel or there is no selected speaker id, the method returns without action.
    /// If the user cancels the file picker or the chosen file has no local path, the method returns without assigning a reference clip.
    /// When a valid file is selected, the selected speaker routing entry is updated with that reference clip.
    /// </remarks>
    public async void OnBrowseReferenceClipClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (string.IsNullOrWhiteSpace(vm.Playback.SpeakerRouting.SelectedSpeakerId)) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Select reference clip for {vm.Playback.SpeakerRouting.SelectedSpeakerId}",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Audio Files") { Patterns = AudioExtensions },
                new FilePickerFileType("All Files") { Patterns = AllFilesPattern },
            ]
        });

        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        await vm.Playback.SpeakerRouting.SetReferenceAudioForSelectedSpeakerAsync(path);
    }

    public void OnReviewSpeakerReferencesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        if (_speakerWizardWindow is { IsVisible: true })
        {
            _speakerWizardWindow.Activate();
            return;
        }

        var wizard = new SpeakerReferenceWizardWindow
        {
            DataContext = new SpeakerReferenceWizardViewModel(vm.Playback, vm.Coordinator, vm.ModelDownloader),
        };
        wizard.Closed += (_, _) => _speakerWizardWindow = null;
        _speakerWizardWindow = wizard;
        wizard.Show(this);
    }

    private void OnExportMenuButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu })
            menu.Open(sender as Control);
    }

    /// <summary>
    /// Prompts the user to choose an output .srt file and exports the current playback segments as SubRip subtitles.
    /// </summary>
    /// <remarks>
    /// If no segments are available, sets the playback status to "No segments available to export." 
    /// On success sets the playback status to "Exported captions to {file.Name}." 
    /// On failure sets the playback status to "Failed to export captions: {error message}".
    /// </remarks>
    public async void OnExportToSrtClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        if (!vm.Playback.Preview.HasSegments)
        {
            vm.Playback.StatusText = "No segments available to export.";
            return;
        }

        var sourceMediaPath = vm.Coordinator.CurrentSession.SourceMediaPath;
        var suggestedName = string.IsNullOrWhiteSpace(sourceMediaPath)
            ? "babel-player-captions.srt"
            : $"{Path.GetFileNameWithoutExtension(sourceMediaPath)}.srt";
        var startFolder = await TryGetProjectExportFolderAsync(vm).ConfigureAwait(true);

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Captions",
            DefaultExtension = "srt",
            SuggestedFileName = suggestedName,
            SuggestedStartLocation = startFolder,
            FileTypeChoices =
            [
                new FilePickerFileType("SubRip Subtitle") { Patterns = SrtPattern },
            ]
        });

        if (file is null)
            return;

        var srt = SrtGenerator.Generate(vm.Playback.Preview.Segments);

        try
        {
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream, Encoding.UTF8);
            await writer.WriteAsync(srt);
            await writer.FlushAsync();
            vm.Playback.StatusText = $"Exported captions to {file.Name}.";
        }
        catch (Exception ex)
        {
            vm.Playback.StatusText = $"Failed to export captions: {ex.Message}";
        }
    }

    private static readonly string[] Mp3Patterns = ["*.mp3"];
    private static readonly string[] Mp4Patterns = ["*.mp4"];

    /// <summary>
    /// Exports dubbed audio as MP3 using a fresh timeline render (segment timings, stretch/pause, ambiance mix)
    /// so the file matches current session configuration — not an older on-disk dub artifact.
    /// </summary>
    public async void OnExportToMp3Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        vm.Playback.StatusText = "Rendering dubbed audio for export…";

        DubRenderResult? render;
        try
        {
            render = await vm.Coordinator.TryRenderDubAudioForExportAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            vm.Playback.StatusText = $"Dub render failed: {ex.Message}";
            return;
        }

        if (render is null)
        {
            vm.Playback.StatusText =
                "Cannot export dub audio: need a saved translation, per-segment TTS clips, and ffmpeg.";
            return;
        }

        var srcPath = render.MixedWithAmbiancePath ?? render.DubTimelinePath;

        var sourceMediaPath = vm.Coordinator.CurrentSession.SourceMediaPath;
        var suggestedName = string.IsNullOrWhiteSpace(sourceMediaPath)
            ? "babel-dub.mp3"
            : $"{Path.GetFileNameWithoutExtension(sourceMediaPath)}-dub.mp3";
        var startFolder = await TryGetProjectExportFolderAsync(vm).ConfigureAwait(true);

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export dubbed audio",
            DefaultExtension = "mp3",
            SuggestedFileName = suggestedName,
            SuggestedStartLocation = startFolder,
            FileTypeChoices =
            [
                new FilePickerFileType("MP3 audio") { Patterns = Mp3Patterns },
            ],
        });

        if (file is null)
        {
            vm.Playback.StatusText = string.Empty;
            return;
        }

        var destPath = file.TryGetLocalPath();
        if (string.IsNullOrEmpty(destPath))
        {
            vm.Playback.StatusText = "Could not resolve a local path for the export file.";
            return;
        }

        try
        {
            File.Copy(srcPath, destPath, overwrite: true);
            vm.Playback.StatusText = $"Exported dubbed audio to {Path.GetFileName(destPath)}.";
        }
        catch (Exception ex)
        {
            vm.Playback.StatusText = $"Failed to export MP3: {ex.Message}";
        }
        finally
        {
            TryDeleteQuiet(render.DubTimelinePath);
            if (!string.Equals(render.MixedWithAmbiancePath, render.DubTimelinePath, StringComparison.OrdinalIgnoreCase))
                TryDeleteQuiet(render.MixedWithAmbiancePath);
        }
    }

    /// <summary>
    /// Muxes the source video with a freshly rendered dub track (plus optional soft subs from the current segment list).
    /// </summary>
    public async void OnExportToMp4Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        vm.Playback.StatusText = "Rendering dubbed audio for video export…";

        DubRenderResult? render;
        try
        {
            render = await vm.Coordinator.TryRenderDubAudioForExportAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            vm.Playback.StatusText = $"Dub render failed: {ex.Message}";
            return;
        }

        if (render is null)
        {
            vm.Playback.StatusText =
                "Cannot export video: need a saved translation, per-segment TTS clips, and ffmpeg.";
            return;
        }

        var dubPath = render.MixedWithAmbiancePath ?? render.DubTimelinePath;

        var sourceMediaPath = vm.Coordinator.CurrentSession.SourceMediaPath;
        var suggestedName = string.IsNullOrWhiteSpace(sourceMediaPath)
            ? "babel-export.mp4"
            : $"{Path.GetFileNameWithoutExtension(sourceMediaPath)}-dub.mp4";
        var startFolder = await TryGetProjectExportFolderAsync(vm).ConfigureAwait(true);

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export video with dubbed track",
            DefaultExtension = "mp4",
            SuggestedFileName = suggestedName,
            SuggestedStartLocation = startFolder,
            FileTypeChoices =
            [
                new FilePickerFileType("MP4 video") { Patterns = Mp4Patterns },
            ],
        });

        if (file is null)
        {
            vm.Playback.StatusText = string.Empty;
            return;
        }

        var destPath = file.TryGetLocalPath();
        if (string.IsNullOrEmpty(destPath))
        {
            vm.Playback.StatusText = "Could not resolve a local path for the export file.";
            return;
        }

        var session = vm.Coordinator.CurrentSession;
        var segments = vm.Playback.Preview.Segments.ToArray();
        var settings = vm.Coordinator.CurrentSettings;
        var encoder = HardwareEncoderHelper.ResolveEncoder(settings, vm.Coordinator.HardwareSnapshot);

        var planner = new VideoExportPlanner();
        var options = new ExportVideoOptions(
            destPath,
            IncludeTtsAudio: true,
            IncludeSoftCaptions: vm.Playback.Preview.HasSegments,
            BurnInCaptions: false,
            OverwriteExisting: true,
            Encoder: encoder,
            DubAudioPathOverride: dubPath);

        var validation = planner.Validate(session, segments, options);
        if (!validation.CanExport)
        {
            vm.Playback.StatusText = "Cannot export video: " + string.Join(" ", validation.Issues);
            TryDeleteQuiet(render.DubTimelinePath);
            TryDeleteQuiet(render.MixedWithAmbiancePath);
            return;
        }

        try
        {
            var plan = planner.BuildPlan(session, segments, options);
            await FfmpegVideoExportRunner.RunPlanAsync(plan, vm.Coordinator.Log).ConfigureAwait(true);
            vm.Playback.StatusText = $"Exported video to {Path.GetFileName(destPath)}.";
        }
        catch (Exception ex)
        {
            vm.Playback.StatusText = $"Failed to export MP4: {ex.Message}";
        }
        finally
        {
            TryDeleteQuiet(render.DubTimelinePath);
            if (!string.Equals(render.MixedWithAmbiancePath, render.DubTimelinePath, StringComparison.OrdinalIgnoreCase))
                TryDeleteQuiet(render.MixedWithAmbiancePath);
        }
    }

    private async Task<IStorageFolder?> TryGetProjectExportFolderAsync(MainWindowViewModel vm)
    {
        if (!vm.Coordinator.CurrentSettings.StoreProjectsNextToMedia)
            return null;

        var projectDir = ProjectFolder.TryGetDefaultDirectory(vm.Coordinator.CurrentSession.SourceMediaPath);
        if (projectDir is null)
            return null;

        var writable = await Task.Run(() => ProjectFolder.TryEnsureWritable(projectDir)).ConfigureAwait(true);
        if (!writable)
            return null;

        return await StorageProvider.TryGetFolderFromPathAsync(projectDir).ConfigureAwait(true);
    }

    private static void TryDeleteQuiet(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup of temp render outputs.
        }
    }

    public void OnApiKeysClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var store = vm.Coordinator.KeyStore;
        if (store is null) return;
        var validationService = new ApiKeyValidationService(
            vm.Coordinator.TranscriptionRegistry,
            vm.Coordinator.TranslationRegistry,
            vm.Coordinator.TtsRegistry);
        var dialog = new ApiKeysDialog { DataContext = new ApiKeysViewModel(store, validationService) };
        _ = dialog.ShowDialog(this);
    }

    public void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var win = new SettingsWindow();
        win.DataContext = vm.CreateSettingsViewModel(win);
        _ = win.ShowDialog(this);
    }

    public void OnMinimizeWindowClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    public void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.Source is Visual sourceVisual && sourceVisual.FindAncestorOfType<Button>() is not null)
            return;

        if (e.ClickCount == 2)
        {
            OnToggleMaximizeWindowClick(sender, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        BeginMoveDrag(e);
    }

    public void OnToggleMaximizeWindowClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    public void OnCloseWindowClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    public void OnForceCloseWindowClick(object? sender, RoutedEventArgs e)
    {
        if (!BuildInfo.IsDevBuild)
        {
            Close();
            return;
        }

        ForceCloseCurrentProcess();
    }

    private static async Task TryDockerComposeDownAsync()
    {
        var dockerPath = DependencyLocator.FindDocker();
        var composeFilePath = ResolveComposeFilePath();
        if (string.IsNullOrWhiteSpace(dockerPath) || string.IsNullOrWhiteSpace(composeFilePath))
            return;

        var workingDirectory = Path.GetDirectoryName(composeFilePath) ?? AppContext.BaseDirectory;
        var psi = new ProcessStartInfo
        {
            FileName = dockerPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };

        psi.ArgumentList.Add("compose");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(composeFilePath);
        psi.ArgumentList.Add("down");

        using var process = Process.Start(psi);
        if (process is null)
            return;

        using var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Ignore timeout kill failures.
            }
        }
    }

    private static string? ResolveComposeFilePath()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; current is not null && depth < 6; depth++, current = current.Parent)
        {
            foreach (var candidateName in new[] { "docker-compose.yml", "compose.yml", "compose.yaml" })
            {
                var candidate = Path.Combine(current.FullName, candidateName);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static void ForceCloseCurrentProcess()
    {
        try
        {
            Process.GetCurrentProcess().Kill(entireProcessTree: true);
        }
        catch
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
                return;
            }

            Environment.Exit(-1);
        }
    }

#if BABEL_DEV
    private void WireDevToolbarClick(string name, EventHandler<RoutedEventArgs> handler)
    {
        var b = this.FindControl<Button>(name);
        if (b is not null)
            b.Click += handler;
    }

    public void OnDevLogClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var win = new Dev.DevLogWindow 
        { 
            DataContext = new ViewModels.Dev.DevLogViewModel(vm.Coordinator.DevLog, this.Clipboard) 
        };
        win.Show();
    }

    public void OnFreshStartClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        _ = vm.Coordinator.FreshStartAsync();
    }
#endif

    public void OnRecentSessionSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox cb && cb.SelectedItem is RecentSessionEntry entry
            && DataContext is MainWindowViewModel vm)
        {
            vm.Coordinator.RestoreSession(entry.SessionId);
            cb.SelectedItem = null;
        }
    }

    private void TryApplyPendingMediaReloadRequest()
    {
        if (DataContext is not MainWindowViewModel vm || _embeddedTransport is null)
            return;

        var pending = vm.Coordinator.PendingMediaReloadRequest;
        if (pending is null)
            return;

        var request = vm.Coordinator.ConsumePendingMediaReloadRequest();
        if (request is null || !System.IO.File.Exists(request.IngestedMediaPath))
            return;

        _embeddedTransport.Load(request.IngestedMediaPath);
        vm.Playback.Preview.ReapplySubtitlesIfActive();
        vm.Playback.Preview.IsSourcePaused = !request.AutoPlay;
        if (request.AutoPlay)
            _embeddedTransport.Play();
    }
}
