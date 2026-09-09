using System;
using System.ComponentModel;
using System.Linq;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Linq;
using Avalonia.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Babel.Player.Models;
using Babel.Player.Models.LanguageSupport;
using Babel.Player.Services;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;
using Babel.Player.Services.Transcription;
using Babel.Player.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SettingsService = Babel.Player.Services.Settings.SettingsService;

namespace Babel.Player.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase, IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly SessionWorkflowCoordinator _coordinator;
    private readonly ApiKeyStore? _apiKeyStore;
    private readonly Window _ownerWindow;
    private readonly IContainerizedInferenceManager _containerizedManager;
    private readonly Func<bool> _hdrDisplayStateProvider;
    private bool _isHdrDisplayActive;
    private CancellationTokenSource? _restartCts;
    private readonly DispatcherTimer _healthTimer;

    /// <summary>
    /// UI culture captured when the settings window opened.  Used by <see cref="Dispose"/>
    /// to revert live language changes (<see cref="OnSelectedAppLanguageChanged"/> calls
    /// <c>SetCulture</c> the moment the user picks a language so the effect is visible,
    /// but closing the dialog without Apply/OK must undo that global state change since
    /// nothing was persisted).
    /// </summary>
    private readonly CultureInfo _originalCulture;

    /// <summary>
    /// Set by <see cref="Apply"/> once the current language selection has been persisted
    /// to settings.  <see cref="Dispose"/> uses this to decide whether closing should
    /// revert the live preview to the culture captured at window open (<see cref="_originalCulture"/>),
    /// or snap it to the last persisted <c>AppLanguage</c> (so a later preview after Apply
    /// is not left applied when the user dismisses without saving again).
    /// </summary>
    private bool _languageChangeCommitted;
    private bool _suppressAppLanguageSelectionPreview;
    private IDisposable? _readinessSignalSubscription;

    /// <summary>
    /// Initializes the SettingsViewModel, populating view-model properties from the coordinator's current settings and starting background health polling and readiness subscriptions.
    /// </summary>
    /// <param name="settingsService">Service used for settings storage and retrieval.</param>
    /// <param name="coordinator">Coordinator that provides current settings, diagnostics, readiness signals, and change notifications.</param>
    /// <param name="ownerWindow">Window that will be closed by the view-model's OK/Cancel commands.</param>
    /// <param name="modelsTab">Injected ModelsTabViewModel instance exposed by this view-model.</param>
    /// <param name="containerizedManager">Optional inference manager used for backend status checks and restarts; when null a no-op implementation is used.</param>
    /// <param name="apiKeyStore">Optional API key store dependency.</param>
    /// <param name="hdrDisplayStateProvider">Optional function used to query whether an HDR-capable display is currently active; when null a platform default probe is used.</param>
    public SettingsViewModel(
        SettingsService settingsService,
        SessionWorkflowCoordinator coordinator,
        Window ownerWindow,
        ModelsTabViewModel modelsTab,
        IContainerizedInferenceManager? containerizedManager = null,
        ApiKeyStore? apiKeyStore = null,
        Func<bool>? hdrDisplayStateProvider = null)
    {
        _settingsService       = settingsService;
        _coordinator           = coordinator;
        _ownerWindow           = ownerWindow;
        ModelsTab              = modelsTab;
        _containerizedManager  = containerizedManager ?? NullInferenceManager.Instance;
        _apiKeyStore           = apiKeyStore;
        _hdrDisplayStateProvider = hdrDisplayStateProvider ?? HardwareSnapshot.QueryActiveHdrDisplay;
        _isHdrDisplayActive    = _hdrDisplayStateProvider();

        var current = _coordinator.CurrentSettings;
        SelectedVoice          = current.TtsVoice;
        SelectedTheme          = current.Theme;
        // Snapshot the live culture before any partial-method setter can mutate it
        // so Cancel() can revert language previews the user didn't persist.
        _originalCulture       = LocalizationService.Instance.CurrentCulture;
        AppLanguageOptions     = BuildAppLanguageOptions();
        SelectedAppLanguage    = AppLanguageOptions.FirstOrDefault(o =>
            string.Equals(o.Code, current.AppLanguage, StringComparison.OrdinalIgnoreCase))
            ?? AppLanguageOptions[0];
        MaxRecentSessions      = current.MaxRecentSessions;
        AutoSaveEnabled        = current.AutoSaveEnabled;
        StoreProjectsNextToMedia = current.StoreProjectsNextToMedia;
        ShowPipelinePane       = current.IsPipelinePaneVisible;
        ShowSegmentsPane       = current.IsSegmentsPaneVisible;
        SwapPaneSides          = current.SwapPaneSides;
        BilingualSubtitlesEnabled = current.BilingualSubtitlesEnabled;
        PreferredLocalGpuBackend = current.PreferredLocalGpuBackend;
        AdvancedGpuServiceUrl  = current.AdvancedGpuServiceUrl;
        AlwaysStartLocalGpuRuntimeAtAppStart = current.AlwaysStartLocalGpuRuntimeAtAppStart;
        VocalSeparationEnabled = current.VocalSeparationEnabled;
        TranscriptionCpuComputeType = current.TranscriptionCpuComputeType;
        TranscriptionCpuThreads = current.TranscriptionCpuThreads;
        TranscriptionNumWorkersUseAuto = current.TranscriptionNumWorkersUseAuto;
        TranscriptionNumWorkers = current.TranscriptionNumWorkers;
        DubTimingMode = current.DubTimingMode;

        // Theme options
        ThemeOptions = ["Light", "Dark", "System"];

        // TTS voice options
        TtsVoiceOptions = [.. EdgeTtsCatalog.VoiceIds];

        // Video hardware settings
        _videoHwdec          = current.VideoHwdec;
        _videoGpuApi         = current.VideoGpuApi;
        _videoExportEncoder  = current.VideoExportEncoder;
        _videoUseGpuNext     = current.VideoUseGpuNext;

        // Video enhancement settings
        _videoVsrEnabled     = current.VideoVsrEnabled;
        _videoHdrPlaybackMode = current.VideoHdrPlaybackMode;
        _videoToneMapping    = current.VideoToneMapping;
        _videoTargetPeak     = current.VideoTargetPeak;
        _videoHdrComputePeak = current.VideoHdrComputePeak;

        _coordinator.PropertyChanged += OnCoordinatorPropertyChanged;
        LocalizationService.Instance.CultureChanged += OnCultureChanged;

        // Hotkeys (default values)
        PlayPauseHotkey         = MainWindowShortcutDefaults.PlayPauseLabel;
        ToggleLeftPaneHotkey    = MainWindowShortcutDefaults.ToggleLeftPaneLabel;
        ToggleRightPaneHotkey   = MainWindowShortcutDefaults.ToggleRightPaneLabel;
        ToggleDubModeHotkey     = MainWindowShortcutDefaults.ToggleDubModeLabel;
        ToggleFullscreenHotkey  = MainWindowShortcutDefaults.ToggleFullscreenLabel;

        _healthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _healthTimer.Tick += (_, _) =>
        {
            try { UpdateBackendStatus(); }
            catch (Exception ex)
            {
                _coordinator.Log.Warning($"Health poll failed: {ex.Message}");
                BackendErrorDetail = $"Poll error: {ex.Message}";
                UpdateBackendStatus();
            }
        };
        _healthTimer.Start();
        
        UpdateBackendStatus();
        _readinessSignalSubscription = _coordinator.ReadinessSignals
            .Select(signal => $"{signal.Kind}:{signal.Source}:{signal.Summary}:{signal.ForceRefresh}")
            .DistinctUntilChanged(StringComparer.Ordinal)
            .Throttle(TimeSpan.FromMilliseconds(300))
            .Subscribe(_ => Dispatcher.UIThread.Post(UpdateBackendStatus));
    }

    // ── About ─────────────────────────────────────────────────────────────────

    public static string AppVersion   => $"Version {BuildInfo.Version}";
    public static string AppBuildDate => $"Build date: {BuildInfo.BuildDate}";

    // ── Backend restart ───────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRestartBackend))]
    private bool _isRestartingBackend;

    [ObservableProperty]
    private string _backendStatusText = "Idle";

    [ObservableProperty]
    private IBrush _backendStatusBrush = Brushes.Gray;

    [ObservableProperty]
    private string? _backendErrorDetail;

    public bool CanRestartBackend => !IsRestartingBackend;

    private int _backendUnavailableStreak;

    /// <summary>Diagnostics: counts consecutive probe results where the host was unavailable (resets on any other state).</summary>
    public string GpuHostProbeDiagnostics =>
        $"Unavailable probe streak: {_backendUnavailableStreak} (resets when the host responds).";

    private void UpdateBackendStatus()
    {
        if (IsRestartingBackend) return;

        var status = _containerizedManager.GetCurrentStatus(_coordinator.CurrentSettings);
        if (status.State == ContainerizedProbeState.Unavailable)
            _backendUnavailableStreak++;
        else
            _backendUnavailableStreak = 0;

        var age = DateTimeOffset.UtcNow - status.CheckedAtUtc;
        var freshness = age < TimeSpan.FromSeconds(1)
            ? "updated just now"
            : $"updated {Math.Max(1, (int)age.TotalSeconds).ToString(CultureInfo.CurrentCulture)}s ago";
        var staleTag = status.IsStale ? " (stale)" : string.Empty;
        var coldStart = DateTimeOffset.UtcNow - _coordinator.ProcessStartedAtUtc < TimeSpan.FromSeconds(90);
        const string warmHint = " Typical first warm-up after launch: 30 to 60 seconds.";

        BackendStatusText = status is { Busy: true, BusyReason: not null }
            ? $"Warming — busy: {status.BusyReason} · {freshness}{warmHint}"
            : status.State switch
            {
                ContainerizedProbeState.Available => $"Ready{staleTag} · {freshness}",
                ContainerizedProbeState.Unavailable when coldStart && _backendUnavailableStreak < 6 =>
                    $"Warming / waiting — host not reachable yet. " +
                    $"{(string.IsNullOrWhiteSpace(status.ErrorDetail) ? "If this persists for several minutes, use Restart below." : status.ErrorDetail)}" +
                    $"{warmHint} · {freshness}",
                ContainerizedProbeState.Unavailable =>
                    $"Unavailable — {status.ErrorDetail ?? "Use Restart inference backend or verify the service URL."} · {freshness}",
                ContainerizedProbeState.Checking =>
                    $"Warming — checking local inference host…{warmHint} · {freshness}",
                _ => $"{status.State} · {freshness}",
            };

        BackendStatusBrush = status.State switch
        {
            ContainerizedProbeState.Available => Brushes.Green,
            ContainerizedProbeState.Unavailable when coldStart && _backendUnavailableStreak < 6 => Brushes.Orange,
            ContainerizedProbeState.Unavailable => Brushes.Red,
            ContainerizedProbeState.Checking => Brushes.Orange,
            _ => Brushes.Gray
        };

        BackendErrorDetail = status.ErrorDetail;
        _healthTimer.Interval = status.State switch
        {
            ContainerizedProbeState.Checking => TimeSpan.FromSeconds(2),
            ContainerizedProbeState.Unavailable => TimeSpan.FromSeconds(4),
            ContainerizedProbeState.Available when status.IsStale => TimeSpan.FromSeconds(4),
            ContainerizedProbeState.Available => TimeSpan.FromSeconds(12),
            _ => TimeSpan.FromSeconds(5)
        };

        OnPropertyChanged(nameof(VocalSeparationAvailable));
        OnPropertyChanged(nameof(VocalSeparationAvailabilityHint));
        OnPropertyChanged(nameof(HasVocalSeparationAvailabilityHint));
        MaybeCoerceVocalSeparationDraftAndCoordinator();
        OnPropertyChanged(nameof(GpuHostProbeDiagnostics));
    }

    // ── Diagnostics ───────────────────────────────────────────────────────────

    public string CpuInfo => _coordinator.HardwareSnapshot.CpuLine;
    public string GpuInfo => _coordinator.HardwareSnapshot.GpuLine;
    public string RamInfo => _coordinator.HardwareSnapshot.RamLine;
    public string InferenceModeInfo => _coordinator.BootstrapDiagnostics.InferenceLine;
    public string RuntimeWarmupInfo => _coordinator.RuntimeWarmupStatusText ?? "No active warmup";
    public string TranslationFallbackInfo => _coordinator.TranslationFallbackNote ?? "None";
    public string PythonInfo => _coordinator.BootstrapDiagnostics.PythonAvailable
        ? _coordinator.BootstrapDiagnostics.PythonPath ?? "Path unavailable"
        : "Not found";
    public string FfmpegInfo => _coordinator.BootstrapDiagnostics.FfmpegAvailable
        ? _coordinator.BootstrapDiagnostics.FfmpegPath ?? "Path unavailable"
        : "Not found";

    [RelayCommand]
    private async Task RefreshDiagnostics()
    {
        try
        {
            _coordinator.RequestReadinessRefresh("Diagnostics refresh requested.");
            var warmupData = await Task.Run(() => _coordinator.GatherBootstrapWarmupData());
            _coordinator.ApplyBootstrapWarmupData(warmupData);

            OnPropertyChanged(nameof(CpuInfo));
            OnPropertyChanged(nameof(GpuInfo));
            OnPropertyChanged(nameof(RamInfo));
            OnPropertyChanged(nameof(InferenceModeInfo));
            OnPropertyChanged(nameof(RuntimeWarmupInfo));
            OnPropertyChanged(nameof(TranslationFallbackInfo));
            OnPropertyChanged(nameof(PythonInfo));
            OnPropertyChanged(nameof(FfmpegInfo));
            OnPropertyChanged(nameof(GpuHostProbeDiagnostics));
        }
        catch (Exception ex)
        {
            _coordinator.Log.Warning($"Diagnostics refresh failed: {ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanRestartBackend))]
    private async Task RestartBackend()
    {
        _restartCts?.Cancel();
        _restartCts = new CancellationTokenSource();
        var ct = _restartCts.Token;

        IsRestartingBackend = true;
        BackendStatusText   = "Restarting\u2026";

        var settings = _coordinator.CurrentSettings;

        try
        {
            var result = await _containerizedManager
                .EnsureStartedAsync(settings, ContainerizedStartupTrigger.Manual, ct)
                .ConfigureAwait(true);

            if (result == ContainerizedStartResult.AlreadyRunning || result == ContainerizedStartResult.Started)
            {
                BackendErrorDetail = null;
            }
            else
            {
                BackendErrorDetail = $"Unexpected result: {result}";
            }
            UpdateBackendStatus();
        }
        catch (OperationCanceledException)
        {
            BackendErrorDetail = "Restart cancelled by user";
            UpdateBackendStatus();
        }
        catch (Exception ex)
        {
            BackendErrorDetail = $"Restart failed: {ex.Message}";
            UpdateBackendStatus();
        }
        finally
        {
            IsRestartingBackend = false;
        }
    }

    // ── Theme ─────────────────────────────────────────────────────────────────

    [ObservableProperty]
    private string _selectedTheme = "Light";

    partial void OnSelectedThemeChanged(string value)
    {
        // Apply theme change immediately when user selects from dropdown
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = value switch
            {
                "Dark" => ThemeVariant.Dark,
                "Light" => ThemeVariant.Light,
                _ => ThemeVariant.Default // System
            };
        }
    }

    public string[] ThemeOptions { get; }

    // ── App UI language ───────────────────────────────────────────────────────

    /// <summary>A single selectable UI language in the settings combo box.</summary>
    public sealed record AppLanguageOption(string Code, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    public AppLanguageOption[] AppLanguageOptions { get; private set; }

    [ObservableProperty]
    private AppLanguageOption _selectedAppLanguage = null!;

    partial void OnSelectedAppLanguageChanged(AppLanguageOption value)
    {
        if (value is null) return;
        if (_suppressAppLanguageSelectionPreview) return;
        var effective = LocalizationService.ResolveAppLanguage(value.Code);
        try
        {
            LocalizationService.Instance.SetCulture(new CultureInfo(effective));
        }
        catch (CultureNotFoundException)
        {
            LocalizationService.Instance.SetCulture(new CultureInfo("en"));
        }
    }

    private static AppLanguageOption[] BuildAppLanguageOptions()
    {
        var currentCulture = LocalizationService.Instance.CurrentCulture;
        var options = new System.Collections.Generic.List<AppLanguageOption>
        {
            new("auto", Babel.Player.Resources.Strings.ResourceManager
                .GetString("Settings_Option_AutoSystem", currentCulture)
                ?? "Auto (system)"),
        };
        foreach (var code in LocalizationService.SupportedUiLanguages.OrderBy(c => c, StringComparer.Ordinal))
        {
            options.Add(new AppLanguageOption(code, LanguageDisplayNames.ForIso639(code, currentCulture)));
        }
        return options.ToArray();
    }

    // ── TTS Voice ─────────────────────────────────────────────────────────────

    [ObservableProperty]
    private string _selectedVoice;

    public string[] TtsVoiceOptions { get; }

    // ── Dub timing mode ───────────────────────────────────────────────────────

    [ObservableProperty]
    private SegmentTimingMode _dubTimingMode;

    public SegmentTimingMode[] DubTimingModeOptions { get; } =
        [SegmentTimingMode.Off, SegmentTimingMode.Stretch];

    /// <summary>CPU compute types available for the current hardware (AVX-512-only entries hidden when unsupported).</summary>
    public string[] TranscriptionCpuComputeTypeOptions =>
        TranscriptionCpuSettingsSanitizer.GetSelectableComputeTypes(_coordinator.HardwareSnapshot);

    // ── Models tab ────────────────────────────────────────────────────────────

    public ModelsTabViewModel ModelsTab { get; }

    // ── Recent Sessions ───────────────────────────────────────────────────────

    [ObservableProperty]
    private int _maxRecentSessions;

    // ── Navigation selection ─────────────────────────────────────────────────
    [ObservableProperty]
    private bool _isGeneralSelected = true;

    [ObservableProperty]
    private bool _isHotkeysSelected;

    [ObservableProperty]
    private bool _isVideoSelected;

    [ObservableProperty]
    private bool _isModelsSelected;

    [ObservableProperty]
    private bool _isAboutSelected;

    [ObservableProperty]
    private bool _isDiagnosticsSelected;

    // ── Auto-save ─────────────────────────────────────────────────────────────

    [ObservableProperty]
    private bool _autoSaveEnabled;

    [ObservableProperty]
    private bool _storeProjectsNextToMedia;

    [ObservableProperty]
    private bool _showPipelinePane;

    [ObservableProperty]
    private bool _showSegmentsPane;

    [ObservableProperty]
    private bool _swapPaneSides;

    /// <summary>When true, exported/embedded subtitles include both source and translated lines (see Settings ▸ Video).</summary>
    [ObservableProperty]
    private bool _bilingualSubtitlesEnabled;

    // ── Containerized local inference ─────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDockerBackend))]
    private GpuHostBackend _preferredLocalGpuBackend = GpuHostBackend.ManagedVenv;

    /// <summary>True when the Docker GPU backend is selected; controls whether the service URL field is editable.</summary>
    public bool IsDockerBackend => PreferredLocalGpuBackend == GpuHostBackend.DockerHost;

    [ObservableProperty]
    private string _advancedGpuServiceUrl = "http://127.0.0.1:8000";

    [ObservableProperty]
    private bool _alwaysStartLocalGpuRuntimeAtAppStart;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VocalSeparationAvailable))]
    [NotifyPropertyChangedFor(nameof(VocalSeparationAvailabilityHint))]
    [NotifyPropertyChangedFor(nameof(HasVocalSeparationAvailabilityHint))]
    private bool _vocalSeparationEnabled;

    // ── Advanced transcription CPU tuning ─────────────────────────────────────

    [ObservableProperty]
    private string _transcriptionCpuComputeType = "auto";

    [ObservableProperty]
    private int _transcriptionCpuThreads;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TranscriptionNumWorkersManualEnabled))]
    private bool _transcriptionNumWorkersUseAuto = true;

    /// <summary>When false, the Workers field is editable; when true, worker count is derived from hardware.</summary>
    public bool TranscriptionNumWorkersManualEnabled => !TranscriptionNumWorkersUseAuto;

    [ObservableProperty]
    private int _transcriptionNumWorkers = 1;

    /// <summary>Non-empty after Apply when CPU settings were corrected.</summary>
    [ObservableProperty]
    private string? _cpuAdvancedSettingsNotice;

    // ── Video hardware decode & encode ────────────────────────────────────────

    [ObservableProperty]
    private string _videoHwdec = "auto";

    [ObservableProperty]
    private string _videoGpuApi = "auto";

    [ObservableProperty]
    private string _videoExportEncoder = "auto";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HdrSettingsAvailable))]
    [NotifyPropertyChangedFor(nameof(HdrAvailabilityHintText))]
    [NotifyPropertyChangedFor(nameof(HasHdrAvailabilityHint))]
    [NotifyPropertyChangedFor(nameof(VsrSettingsAvailable))]
    [NotifyPropertyChangedFor(nameof(RtxHdrDriverModeAvailable))]
    [NotifyPropertyChangedFor(nameof(RtxVideoHardwareGateHint))]
    [NotifyPropertyChangedFor(nameof(HasRtxVideoHardwareGateHint))]
    private bool _videoUseGpuNext;

    public string[] HwdecOptions { get; } =
        ["auto", "auto-safe", "no", "d3d11va", "d3d11va-copy", "nvdec", "nvdec-copy", "qsv", "dxva2"];

    public string[] GpuApiOptions { get; } =
        ["auto", "d3d11", "vulkan", "opengl"];

    public GpuHostBackend[] GpuBackendOptions { get; } =
        [GpuHostBackend.ManagedVenv, GpuHostBackend.DockerHost];

    public string[] ExportEncoderOptions { get; } =
        ["auto", "h264_nvenc", "hevc_nvenc", "h264_amf", "hevc_amf",
         "h264_qsv", "hevc_qsv", "libx264", "libx265"];

    // ── Video enhancement settings ────────────────────────────────────────────

    [ObservableProperty]
    private bool _videoVsrEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHdrModeOff))]
    [NotifyPropertyChangedFor(nameof(IsHdrModeNvidia))]
    [NotifyPropertyChangedFor(nameof(IsHdrModeMpv))]
    [NotifyPropertyChangedFor(nameof(IsMpvHdrPassthroughDetailsVisible))]
    private VideoHdrPlaybackMode _videoHdrPlaybackMode;

    [ObservableProperty]
    private string _videoToneMapping = "bt.2390";

    [ObservableProperty]
    private string _videoTargetPeak = "auto";

    [ObservableProperty]
    private bool _videoHdrComputePeak = true;

    public string[] HdrToneMappingOptions { get; } =
        ["bt.2390", "mobius", "clip", "auto"];

    public bool IsHdrModeOff
    {
        get => VideoHdrPlaybackMode == VideoHdrPlaybackMode.Off;
        set { if (value) VideoHdrPlaybackMode = VideoHdrPlaybackMode.Off; }
    }

    public bool IsHdrModeNvidia
    {
        get => VideoHdrPlaybackMode == VideoHdrPlaybackMode.NvidiaDriverRtxHdr;
        set { if (value) VideoHdrPlaybackMode = VideoHdrPlaybackMode.NvidiaDriverRtxHdr; }
    }

    public bool IsHdrModeMpv
    {
        get => VideoHdrPlaybackMode == VideoHdrPlaybackMode.MpvHdrPassthrough;
        set { if (value) VideoHdrPlaybackMode = VideoHdrPlaybackMode.MpvHdrPassthrough; }
    }

    public bool IsMpvHdrPassthroughDetailsVisible =>
        VideoUseGpuNext && VideoHdrPlaybackMode == VideoHdrPlaybackMode.MpvHdrPassthrough;

    /// <summary>
    /// VSR requires gpu-next plus NVIDIA RTX Video hardware floor (GeForce RTX GPU + driver ≥ 551.23).
    /// </summary>
    public bool VsrSettingsAvailable =>
        VideoUseGpuNext && _coordinator.HardwareSnapshot.MeetsNvidiaRtxVideoHardwareGate;

    /// <summary>
    /// RTX HDR (driver) mode requires Windows HDR plus the same NVIDIA RTX Video hardware floor as VSR.
    /// mpv HDR passthrough does not use this gate.
    /// </summary>
    public bool RtxHdrDriverModeAvailable =>
        HdrSettingsAvailable && _coordinator.HardwareSnapshot.MeetsNvidiaRtxVideoHardwareGate;

    /// <summary>
    /// Explains why RTX Video features are disabled when gpu-next is on but hardware does not qualify.
    /// </summary>
    public string RtxVideoHardwareGateHint
    {
        get
        {
            if (!VideoUseGpuNext)
                return string.Empty;
            var s = _coordinator.HardwareSnapshot;
            if (s.IsDetecting)
                return string.Empty;
            if (s.MeetsNvidiaRtxVideoHardwareGate)
                return string.Empty;
            if (string.IsNullOrWhiteSpace(s.GpuName))
                return "No NVIDIA GPU was detected (nvidia-smi). RTX Video Super Resolution and RTX HDR require a supported NVIDIA GPU and driver.";
            if (!s.IsRtxCapable)
                return "RTX Video Super Resolution and RTX HDR require a GeForce RTX-class GPU (Turing or newer).";
            if (!s.IsVsrDriverSufficient)
            {
                var ver = string.IsNullOrWhiteSpace(s.NvidiaDriverVersion) ? "unknown" : s.NvidiaDriverVersion;
                return $"NVIDIA driver {ver} is below 551.23, the minimum for RTX Video (VSR and RTX HDR). Update GeForce Game Ready or Studio Driver.";
            }

            return string.Empty;
        }
    }

    public bool HasRtxVideoHardwareGateHint => !string.IsNullOrWhiteSpace(RtxVideoHardwareGateHint);

    public string VsrSupportHintText => _coordinator.VideoEnhancementDiagnostics.SupportHintText;
    public string VsrRequestedStateText => _coordinator.VideoEnhancementDiagnostics.RequestedStateText;
    public string VsrResolvedStateText => _coordinator.VideoEnhancementDiagnostics.ResolvedStateText;
    public string VsrReasonText => _coordinator.VideoEnhancementDiagnostics.LastReasonText;
    public string VsrFilterText => _coordinator.VideoEnhancementDiagnostics.LastFilterText;

    /// <summary>True when Windows HDR is currently active for at least one desktop output.</summary>
    public bool IsHdrDisplayActive => _isHdrDisplayActive;

    /// <summary>
    /// HDR passthrough requires both gpu-next and an active Windows HDR display pipeline.
    /// </summary>
    public bool HdrSettingsAvailable => VideoUseGpuNext && IsHdrDisplayActive;

    public string HdrAvailabilityHintText =>
        VideoUseGpuNext && !IsHdrDisplayActive
            ? "Enable HDR in Windows Display Settings to use HDR passthrough."
            : string.Empty;

    public bool HasHdrAvailabilityHint => !string.IsNullOrWhiteSpace(HdrAvailabilityHintText);

    public bool VocalSeparationAvailable => TryGetVocalSeparationCapability(out var ready, out _) && ready;

    public string VocalSeparationAvailabilityHint
    {
        get
        {
            _ = TryGetVocalSeparationCapability(out _, out var hint);
            return hint ?? "Requires a ready containerized inference host with audio-separator installed (produces vocals + ambiance stems).";
        }
    }

    public bool HasVocalSeparationAvailabilityHint =>
        !VocalSeparationAvailable && !string.IsNullOrWhiteSpace(VocalSeparationAvailabilityHint);

    public static string HdrDriverFeatureHintText =>
        "RTX HDR uses NVIDIA Control Panel (RTX Video / Auto HDR). HDR passthrough uses mpv instead — pick one mode; they are mutually exclusive.";

    private bool TryGetVocalSeparationCapability(out bool ready, out string? hint)
    {
        ready = false;
        hint = null;

        var probe = _coordinator.ContainerizedProbe;
        if (probe is null)
        {
            hint = "Containerized readiness probe is unavailable in this build.";
            return false;
        }

        var probeResult = probe.GetCurrentOrStartBackgroundProbe(_coordinator.CurrentSettings.EffectiveGpuServiceUrl);
        if (probeResult.State == ContainerizedProbeState.Checking)
        {
            hint = "Containerized host is still starting.";
            return false;
        }

        if (probeResult.State == ContainerizedProbeState.Unavailable)
        {
            hint = string.IsNullOrWhiteSpace(probeResult.ErrorDetail)
                ? "Containerized host is unavailable."
                : probeResult.ErrorDetail;
            return false;
        }

        if (probeResult.Capabilities is null)
        {
            hint = string.IsNullOrWhiteSpace(probeResult.CapabilitiesError)
                ? "Containerized capabilities are unavailable."
                : probeResult.CapabilitiesError;
            return false;
        }

        ready = probeResult.Capabilities.IsReady(ContainerCapabilityStage.VocalSeparation);
        hint = probeResult.Capabilities.Detail(ContainerCapabilityStage.VocalSeparation)
            ?? (ready ? "Audio separator is ready." : "Audio separator is not ready.");
        return true;
    }

    /// <summary>
    /// When the container reports a definitive capability snapshot and vocal separation is not ready,
    /// clear both the coordinator flag and this dialog draft so JSON edits cannot leave "on" in the UI.
    /// </summary>
    private void MaybeCoerceVocalSeparationDraftAndCoordinator()
    {
        if (!TryGetVocalSeparationCapability(out var ready, out _))
            return;
        if (ready)
            return;

        var changed = false;
        if (_coordinator.CurrentSettings.VocalSeparationEnabled)
        {
            _coordinator.CurrentSettings.VocalSeparationEnabled = false;
            _coordinator.NotifySettingsModified();
            changed = true;
        }

        if (VocalSeparationEnabled)
        {
            VocalSeparationEnabled = false;
            changed = true;
        }

        if (changed)
            _coordinator.Log.Info("Vocal separation disabled: audio separator is not ready on the inference host.");
    }

    // ── Hotkeys ───────────────────────────────────────────────────────────────

    [ObservableProperty]
    private string _playPauseHotkey;

    [ObservableProperty]
    private string _toggleLeftPaneHotkey;

    [ObservableProperty]
    private string _toggleRightPaneHotkey;

    [ObservableProperty]
    private string _toggleDubModeHotkey;

    [ObservableProperty]
    private string _toggleFullscreenHotkey;

    /// <summary>
    /// Writes the view-model's current settings into the coordinator's settings, applies the selected theme immediately, and signals that settings were modified.
    /// </summary>
    /// <remarks>
    /// Empty or whitespace values are normalized before assignment: the advanced GPU service URL is preserved when blank; transcription compute type defaults to "int8"; transcription threads are clamped to be at least 0; transcription workers are clamped to be at least 1; video tone mapping defaults to "bt.2390" and video target peak defaults to "auto". The selected dub timing mode is persisted.
    /// </remarks>

    [RelayCommand]
    private void Apply()
    {
        var settings = _coordinator.CurrentSettings;

        settings.TtsVoice           = SelectedVoice ?? settings.TtsVoice;
        settings.Theme              = SelectedTheme ?? settings.Theme;
        settings.AppLanguage        = string.IsNullOrWhiteSpace(SelectedAppLanguage?.Code)
            ? settings.AppLanguage
            : string.Equals(SelectedAppLanguage.Code, "auto", StringComparison.OrdinalIgnoreCase)
                ? "auto"
                : LocalizationService.ResolveAppLanguage(SelectedAppLanguage.Code);
        // Applied language is now persisted; suppress the Dispose-time revert to
        // _originalCulture even if the user later closes the window via the X button.
        _languageChangeCommitted = true;
        settings.MaxRecentSessions  = MaxRecentSessions;
        settings.AutoSaveEnabled    = AutoSaveEnabled;
        settings.StoreProjectsNextToMedia = StoreProjectsNextToMedia;
        settings.IsPipelinePaneVisible = ShowPipelinePane;
        settings.IsSegmentsPaneVisible = ShowSegmentsPane;
        settings.SwapPaneSides     = SwapPaneSides;
        settings.BilingualSubtitlesEnabled = BilingualSubtitlesEnabled;
        settings.PreferredLocalGpuBackend = PreferredLocalGpuBackend;
        settings.AdvancedGpuServiceUrl = string.IsNullOrWhiteSpace(AdvancedGpuServiceUrl)
            ? settings.AdvancedGpuServiceUrl
            : AdvancedGpuServiceUrl.Trim();
        settings.AlwaysStartLocalGpuRuntimeAtAppStart = AlwaysStartLocalGpuRuntimeAtAppStart;
        settings.VocalSeparationEnabled = VocalSeparationEnabled;
        settings.TranscriptionCpuComputeType = string.IsNullOrWhiteSpace(TranscriptionCpuComputeType)
            ? "auto"
            : TranscriptionCpuComputeType;
        settings.TranscriptionCpuThreads = Math.Max(0, TranscriptionCpuThreads);
        settings.TranscriptionNumWorkersUseAuto = TranscriptionNumWorkersUseAuto;
        settings.TranscriptionNumWorkers = Math.Max(1, TranscriptionNumWorkers);

        var corrections = TranscriptionCpuSettingsSanitizer.Sanitize(settings, _coordinator.HardwareSnapshot);
        CpuAdvancedSettingsNotice = corrections.Count > 0 ? string.Join(" ", corrections) : null;

        TranscriptionCpuComputeType = settings.TranscriptionCpuComputeType;
        TranscriptionCpuThreads = settings.TranscriptionCpuThreads;
        TranscriptionNumWorkers = settings.TranscriptionNumWorkers;

        settings.DubTimingMode       = DubTimingMode == SegmentTimingMode.Pause
            ? SegmentTimingMode.Off
            : DubTimingMode;

        settings.VideoHwdec          = VideoHwdec;
        settings.VideoGpuApi         = VideoGpuApi;
        settings.VideoExportEncoder  = VideoExportEncoder;
        settings.VideoUseGpuNext     = VideoUseGpuNext;
        settings.VideoVsrEnabled     = VideoVsrEnabled;
        settings.VideoHdrPlaybackMode = VideoHdrPlaybackMode;
        settings.VideoToneMapping    = string.IsNullOrWhiteSpace(VideoToneMapping)
            ? "bt.2390"
            : VideoToneMapping.Trim();
        settings.VideoTargetPeak     = string.IsNullOrWhiteSpace(VideoTargetPeak)
            ? "auto"
            : VideoTargetPeak.Trim();
        settings.VideoHdrComputePeak = VideoHdrComputePeak;

        // Apply theme change immediately when Save & Close is pressed
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = SelectedTheme switch
            {
                "Dark" => ThemeVariant.Dark,
                "Light" => ThemeVariant.Light,
                _ => ThemeVariant.Default // System
            };
        }

        _coordinator.NotifySettingsModified();
    }

    [RelayCommand]
    private void OK()
    {
        Apply();
        _ownerWindow.Close();
    }

    [RelayCommand]
    private void Cancel()
    {
        // The language-preview revert lives in Dispose() so it also catches the
        // OS X button / Alt+F4 close paths, which bypass this command but still
        // fire the Window.Closed event that disposes the view model.
        _ownerWindow.Close();
    }

    [RelayCommand]
    private static void OpenKofi()
    {
        try
        {
            const string kofiUrl = "https://ko-fi.com/babel_player";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = kofiUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to open Ko-fi link: {ex.Message}");
        }
    }

    [RelayCommand]
    private static void OpenGitHubSponsors()
    {
        try
        {
            const string sponsorsUrl = "https://github.com/sponsors/trackdubllc";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = sponsorsUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to open GitHub Sponsors link: {ex.Message}");
        }
    }

    public void Dispose()
    {
        // Drop any in-dialog preview: either back to the culture at open, or to the
        // last persisted AppLanguage (after Apply/OK) so a second preview cannot stick.
        var persistedIso = LocalizationService.ResolveAppLanguage(_coordinator.CurrentSettings.AppLanguage);
        var persistedCulture = CultureInfo.GetCultureInfo(persistedIso);
        if (!_languageChangeCommitted)
            LocalizationService.Instance.SetCulture(_originalCulture);
        else
            LocalizationService.Instance.SetCulture(persistedCulture);

        _healthTimer.Stop();
        _restartCts?.Cancel();
        _restartCts?.Dispose();
        _readinessSignalSubscription?.Dispose();
        _readinessSignalSubscription = null;
        _coordinator.PropertyChanged -= OnCoordinatorPropertyChanged;
        LocalizationService.Instance.CultureChanged -= OnCultureChanged;
    }

    private void OnCultureChanged(object? sender, CultureInfo newCulture)
    {
        var previousCode = SelectedAppLanguage?.Code;
        _suppressAppLanguageSelectionPreview = true;
        try
        {
            AppLanguageOptions = BuildAppLanguageOptions();
            SelectedAppLanguage = AppLanguageOptions.FirstOrDefault(o =>
                string.Equals(o.Code, previousCode, StringComparison.OrdinalIgnoreCase))
                ?? AppLanguageOptions[0];
            OnPropertyChanged(nameof(AppLanguageOptions));
        }
        finally
        {
            _suppressAppLanguageSelectionPreview = false;
        }
    }

    private void OnCoordinatorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionWorkflowCoordinator.RuntimeWarmupStatusText))
            OnPropertyChanged(nameof(RuntimeWarmupInfo));
        else if (e.PropertyName == nameof(SessionWorkflowCoordinator.TranslationFallbackNote))
            OnPropertyChanged(nameof(TranslationFallbackInfo));
        else if (e.PropertyName == nameof(SessionWorkflowCoordinator.HardwareSnapshot))
        {
            OnPropertyChanged(nameof(CpuInfo));
            OnPropertyChanged(nameof(GpuInfo));
            OnPropertyChanged(nameof(RamInfo));
            OnPropertyChanged(nameof(TranscriptionCpuComputeTypeOptions));
            var allowed = TranscriptionCpuComputeTypeOptions;
            if (allowed.Length > 0 && !allowed.Contains(TranscriptionCpuComputeType, StringComparer.OrdinalIgnoreCase))
                TranscriptionCpuComputeType = allowed[0];
            OnPropertyChanged(nameof(VsrSettingsAvailable));
            OnPropertyChanged(nameof(RtxHdrDriverModeAvailable));
            OnPropertyChanged(nameof(RtxVideoHardwareGateHint));
            OnPropertyChanged(nameof(HasRtxVideoHardwareGateHint));
            if (VideoHdrPlaybackMode == VideoHdrPlaybackMode.NvidiaDriverRtxHdr && !RtxHdrDriverModeAvailable)
                VideoHdrPlaybackMode = VideoHdrPlaybackMode.Off;
        }
        else if (e.PropertyName == nameof(SessionWorkflowCoordinator.BootstrapDiagnostics))
        {
            OnPropertyChanged(nameof(InferenceModeInfo));
            OnPropertyChanged(nameof(PythonInfo));
            OnPropertyChanged(nameof(FfmpegInfo));
        }
        else if (e.PropertyName == nameof(SessionWorkflowCoordinator.VideoEnhancementDiagnostics))
        {
            OnPropertyChanged(nameof(VsrSupportHintText));
            OnPropertyChanged(nameof(VsrRequestedStateText));
            OnPropertyChanged(nameof(VsrResolvedStateText));
            OnPropertyChanged(nameof(VsrReasonText));
            OnPropertyChanged(nameof(VsrFilterText));
        }
    }

    internal void RefreshHdrDisplayState()
    {
        _isHdrDisplayActive = _hdrDisplayStateProvider();
        if (!IsHdrDisplayActive && VideoHdrPlaybackMode != VideoHdrPlaybackMode.Off)
            VideoHdrPlaybackMode = VideoHdrPlaybackMode.Off;

        OnPropertyChanged(nameof(IsHdrDisplayActive));
        OnPropertyChanged(nameof(HdrSettingsAvailable));
        OnPropertyChanged(nameof(HdrAvailabilityHintText));
        OnPropertyChanged(nameof(HasHdrAvailabilityHint));
        OnPropertyChanged(nameof(RtxHdrDriverModeAvailable));
        OnPropertyChanged(nameof(IsMpvHdrPassthroughDetailsVisible));
        if (VideoHdrPlaybackMode == VideoHdrPlaybackMode.NvidiaDriverRtxHdr && !RtxHdrDriverModeAvailable)
            VideoHdrPlaybackMode = VideoHdrPlaybackMode.Off;
    }

    partial void OnVocalSeparationEnabledChanged(bool value)
    {
        if (!value)
            return;
        if (!TryGetVocalSeparationCapability(out var ready, out _))
            return;
        if (ready)
            return;
        VocalSeparationEnabled = false;
    }

    partial void OnVideoUseGpuNextChanged(bool value)
    {
        if (!value && VideoHdrPlaybackMode != VideoHdrPlaybackMode.Off)
            VideoHdrPlaybackMode = VideoHdrPlaybackMode.Off;
        OnPropertyChanged(nameof(IsMpvHdrPassthroughDetailsVisible));
        OnPropertyChanged(nameof(VsrSettingsAvailable));
        OnPropertyChanged(nameof(RtxHdrDriverModeAvailable));
        OnPropertyChanged(nameof(RtxVideoHardwareGateHint));
        OnPropertyChanged(nameof(HasRtxVideoHardwareGateHint));
    }

    // ── Null-object for tests / design-time ───────────────────────────────────

    private sealed class NullInferenceManager : IContainerizedInferenceManager
    {
        public static readonly NullInferenceManager Instance = new();
        public void RequestEnsureStarted(AppSettings s, ContainerizedStartupTrigger t) { }
        public Task<ContainerizedStartResult> EnsureStartedAsync(
            AppSettings s, ContainerizedStartupTrigger t, CancellationToken ct = default)
            => Task.FromResult(ContainerizedStartResult.AlreadyRunning);

        public ContainerizedProbeResult GetCurrentStatus(AppSettings s)
            => new(s?.EffectiveGpuServiceUrl ?? "N/A", ContainerizedProbeState.Available, DateTimeOffset.UtcNow, "No inference manager.");
    }
}
