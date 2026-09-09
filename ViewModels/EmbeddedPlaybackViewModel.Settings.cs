using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Transcription;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Babel.Player.ViewModels;

public partial class EmbeddedPlaybackViewModel
{
    private readonly Dictionary<ComputeProfile, IReadOnlyList<ProviderDescriptor>> _transcriptionProvidersByRuntime = [];
    private readonly Dictionary<ComputeProfile, IReadOnlyList<ProviderDescriptor>> _translationProvidersByRuntime = [];
    private readonly Dictionary<ComputeProfile, IReadOnlyList<ProviderDescriptor>> _ttsProvidersByRuntime = [];
    private readonly Dictionary<ComputeProfile, IReadOnlyList<string>> _transcriptionProviderIdsByRuntime = [];
    private readonly Dictionary<ComputeProfile, IReadOnlyList<string>> _translationProviderIdsByRuntime = [];
    private readonly Dictionary<ComputeProfile, IReadOnlyList<string>> _ttsProviderIdsByRuntime = [];
    private readonly ObservableCollection<ProviderHealthSnapshot> _providerHealthSnapshots = [];
    private CancellationTokenSource? _providerHealthRefreshCts;
    private int _providerHealthRefreshVersion;
    private ProviderDiagnosticsSelectionSnapshot? _lastQueuedProviderHealthSnapshot;
    private DateTimeOffset _lastProviderHealthRefreshAtUtc = DateTimeOffset.MinValue;
    private IReadOnlyList<ModelOptionViewModel> _availableTranscriptionModels = [];
    private IReadOnlyList<ModelOptionViewModel> _availableTranslationModels = [];
    private IReadOnlyList<ModelOptionViewModel> _availableTtsOptions = [];
    private string _transcriptionKeyStatus = string.Empty;
    private string _translationKeyStatus = string.Empty;
    private string _ttsKeyStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TranscriptionProviders))]
    [NotifyPropertyChangedFor(nameof(AvailableTranscriptionModels))]
    [NotifyPropertyChangedFor(nameof(SelectedTranscriptionModel))]
    [NotifyPropertyChangedFor(nameof(TranscriptionKeyStatus))]
    private ComputeProfile _transcriptionRuntime = ComputeProfile.Cpu;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableTranscriptionModels))]
    [NotifyPropertyChangedFor(nameof(SelectedTranscriptionModel))]
    [NotifyPropertyChangedFor(nameof(TranscriptionKeyStatus))]
    private string _transcriptionProvider = ProviderNames.FasterWhisper;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedTranscriptionModel))]
    [NotifyPropertyChangedFor(nameof(TranscriptionKeyStatus))]
    private string _transcriptionModel = "base";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TranslationProviders))]
    [NotifyPropertyChangedFor(nameof(AvailableTranslationModels))]
    [NotifyPropertyChangedFor(nameof(SelectedTranslationModel))]
    [NotifyPropertyChangedFor(nameof(TranslationKeyStatus))]
    private ComputeProfile _translationRuntime = ComputeProfile.Cloud;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableTranslationModels))]
    [NotifyPropertyChangedFor(nameof(SelectedTranslationModel))]
    [NotifyPropertyChangedFor(nameof(TranslationKeyStatus))]
    private string _translationProvider = ProviderNames.Deepl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedTranslationModel))]
    [NotifyPropertyChangedFor(nameof(TranslationKeyStatus))]
    private string _translationModel = "default";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TtsProviders))]
    [NotifyPropertyChangedFor(nameof(AvailableTtsOptions))]
    [NotifyPropertyChangedFor(nameof(SelectedTtsOption))]
    [NotifyPropertyChangedFor(nameof(TtsKeyStatus))]
    private ComputeProfile _ttsRuntime = ComputeProfile.Cloud;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableTtsOptions))]
    [NotifyPropertyChangedFor(nameof(SelectedTtsOption))]
    [NotifyPropertyChangedFor(nameof(TtsKeyStatus))]
    [NotifyPropertyChangedFor(nameof(ShowTtsAssignmentModeSwitch))]
    [NotifyPropertyChangedFor(nameof(ShowPerSpeakerVoiceHint))]
    [NotifyPropertyChangedFor(nameof(TtsVoiceComboHeaderText))]
    [NotifyPropertyChangedFor(nameof(IsChatterboxTtsSelected))]
    private string _ttsProvider = ProviderNames.EdgeTts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedTtsOption))]
    [NotifyPropertyChangedFor(nameof(TtsKeyStatus))]
    private string _ttsModelOrVoice = "en-US-AriaNeural";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPerSpeakerVoiceHint))]
    [NotifyPropertyChangedFor(nameof(TtsVoiceComboHeaderText))]
    private TtsVoiceAssignmentMode _ttsVoiceAssignmentMode = TtsVoiceAssignmentMode.GlobalDefault;

    [ObservableProperty]
    private ModelOptionViewModel? _selectedTranscriptionModel;

    [ObservableProperty]
    private ModelOptionViewModel? _selectedTranslationModel;

    [ObservableProperty]
    private ModelOptionViewModel? _selectedTtsOption;

    /// <summary>Pipeline output language (dub/sub target). Extend <see cref="PipelineTargetLanguageOption.All"/> for more locales.</summary>
    [ObservableProperty]
    private PipelineTargetLanguageOption? _selectedTargetLanguageOption = PipelineTargetLanguageOption.English;

    /// <summary>Optional ASR language hint; first entry is auto-detect.</summary>
    [ObservableProperty]
    private SpokenLanguageOption? _selectedSpokenLanguageOption = SpokenLanguageOption.All[0];

    private static readonly IReadOnlyList<ComputeProfile> InferenceRuntimeOptionsWithGpu =
        [ComputeProfile.Cpu, ComputeProfile.Gpu, ComputeProfile.Cloud];

    private static readonly IReadOnlyList<ComputeProfile> InferenceRuntimeOptionsWithoutGpu =
        [ComputeProfile.Cpu, ComputeProfile.Cloud];

    public IReadOnlyList<ComputeProfile> InferenceRuntimeOptions => GetAvailableInferenceRuntimeOptions();

    public string ActiveTranscriptionConfigLine => $"{TranscriptionRuntime} / {TranscriptionProvider} / {TranscriptionModel}";

    public string ActiveCpuTuningLine
    {
        get
        {
            var settings = _coordinator.CurrentSettings;
            var hw = _coordinator.HardwareSnapshot;
            var threads = settings.TranscriptionCpuThreads > 0 ? settings.TranscriptionCpuThreads.ToString() : "auto";
            var w = settings.TranscriptionNumWorkersUseAuto
                ? $"auto (~{CpuTranscriptionRuntimePolicy.ComputeAutoNumWorkers(hw)})"
                : CpuTranscriptionRuntimePolicy.ClampManualNumWorkers(settings.TranscriptionNumWorkers, hw).ToString();
            return $"{settings.TranscriptionCpuComputeType} · threads {threads} · workers {w}";
        }
    }

    public string ActiveTranslationConfigLine
    {
        get
        {
            var fallback = _coordinator.TranslationFallbackNote;
            var target = _coordinator.CurrentSettings.TargetLanguage;
            if (fallback is not null)
                return $"{TranslationRuntime} / ⚠ {fallback} · target {target}";

            return $"{TranslationRuntime} / {TranslationProvider} / {TranslationModel} · target {target}";
        }
    }

    public string ActiveTtsConfigLine => $"{TtsRuntime} / {TtsProvider} / {TtsModelOrVoice}";
    public IReadOnlyList<string> TranscriptionProviders => GetTranscriptionProviderIds(TranscriptionRuntime);
    public IReadOnlyList<string> TranslationProviders => GetTranslationProviderIds(TranslationRuntime);
    public IReadOnlyList<string> TtsProviders => GetTtsProviderIds(TtsRuntime);
    public IReadOnlyList<ModelOptionViewModel> AvailableTranscriptionModels => _availableTranscriptionModels;
    public IReadOnlyList<ModelOptionViewModel> AvailableTranslationModels => _availableTranslationModels;
    public IReadOnlyList<ModelOptionViewModel> AvailableTtsOptions => _availableTtsOptions;
    public string TranscriptionKeyStatus => _transcriptionKeyStatus;
    public string TranslationKeyStatus => _translationKeyStatus;
    public string TtsKeyStatus => _ttsKeyStatus;
    public ObservableCollection<ProviderHealthSnapshot> ProviderHealthSnapshots => _providerHealthSnapshots;

    public IReadOnlyList<PipelineTargetLanguageOption> TargetLanguageOptions => PipelineTargetLanguageOption.All;

    public IReadOnlyList<SpokenLanguageOption> SpokenLanguageOptions => SpokenLanguageOption.All;

    public SegmentTimingMode[] DubTimingModeOptions { get; } =
        [SegmentTimingMode.Off, SegmentTimingMode.Stretch];

    public SegmentTimingMode DubTimingMode
    {
        get => _coordinator.CurrentSettings.DubTimingMode;
        set
        {
            var effective = value == SegmentTimingMode.Pause ? SegmentTimingMode.Off : value;

            if (_coordinator.CurrentSettings.DubTimingMode == effective)
                return;

            _coordinator.CurrentSettings.DubTimingMode = effective;
            _coordinator.NotifySettingsModified();
            OnPropertyChanged();
        }
    }

    public bool VocalSeparationEnabled
    {
        get => _coordinator.CurrentSettings.VocalSeparationEnabled;
        set
        {
            var effective = value;
            if (effective && TryGetVocalSeparationCapability(out var ready, out _) && !ready)
                effective = false;

            if (_coordinator.CurrentSettings.VocalSeparationEnabled == effective)
                return;

            _coordinator.CurrentSettings.VocalSeparationEnabled = effective;
            _coordinator.NotifySettingsModified();
            OnPropertyChanged();
            NotifyVocalSeparationCapabilityProperties();
        }
    }

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

    private void NotifyVocalSeparationCapabilityProperties()
    {
        OnPropertyChanged(nameof(VocalSeparationAvailable));
        OnPropertyChanged(nameof(VocalSeparationAvailabilityHint));
        OnPropertyChanged(nameof(HasVocalSeparationAvailabilityHint));
        CoerceVocalSeparationSettingWhenHostReportsNotReady();
    }

    /// <summary>
    /// When the container probe has a definitive capability snapshot and vocal separation is not ready,
    /// turn off the persisted flag so hand-edited settings or stale UI do not keep "enabled" while the run will fail.
    /// </summary>
    private void CoerceVocalSeparationSettingWhenHostReportsNotReady()
    {
        if (!_coordinator.CurrentSettings.VocalSeparationEnabled)
            return;
        if (!TryGetVocalSeparationCapability(out var ready, out _))
            return;
        if (ready)
            return;

        _coordinator.CurrentSettings.VocalSeparationEnabled = false;
        _coordinator.NotifySettingsModified();
        _coordinator.Log.Info("Vocal separation disabled: audio separator is not ready on the inference host.");
        OnPropertyChanged(nameof(VocalSeparationEnabled));
    }

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

    /// <summary>True when Piper/Edge show the global vs per-speaker voice UI.</summary>
    public bool ShowTtsAssignmentModeSwitch =>
        string.Equals(TtsProvider, ProviderNames.Piper, StringComparison.Ordinal)
        || string.Equals(TtsProvider, ProviderNames.EdgeTts, StringComparison.Ordinal);

    internal static bool IsChatterboxProvider(string? providerId) =>
        string.Equals(providerId, ProviderNames.Chatterbox, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the selected TTS provider performs voice cloning.</summary>
    public bool IsChatterboxTtsSelected => IsChatterboxProvider(TtsProvider);

    /// <summary>
    /// Persisted explicit opt-in to Chatterbox voice cloning. Read live by
    /// <see cref="TtsRegistry.CheckReadiness"/> on every run; unchecking takes
    /// effect immediately without touching the provider selection.
    /// </summary>
    public bool ChatterboxVoiceCloneConsent
    {
        get => _coordinator.CurrentSettings.ChatterboxVoiceCloneConsent;
        set
        {
            if (_coordinator.CurrentSettings.ChatterboxVoiceCloneConsent == value)
                return;

            _coordinator.CurrentSettings.ChatterboxVoiceCloneConsent = value;
            _coordinator.NotifySettingsModified();
            OnPropertyChanged();
        }
    }

    public bool ShowPerSpeakerVoiceHint =>
        ShowTtsAssignmentModeSwitch && TtsVoiceAssignmentMode == TtsVoiceAssignmentMode.PerSpeaker;

    public string TtsVoiceComboHeaderText =>
        ShowTtsAssignmentModeSwitch && TtsVoiceAssignmentMode == TtsVoiceAssignmentMode.PerSpeaker
            ? "Fallback voice"
            : "Voice / Model";

    /// <summary>Two-way with CheckBox: per-speaker mode uses Speaker Reference Wizard; fallback = <see cref="TtsModelOrVoice"/>.</summary>
    public bool TtsVoiceUsePerSpeakerWizard
    {
        get => TtsVoiceAssignmentMode == TtsVoiceAssignmentMode.PerSpeaker;
        set
        {
            var target = value ? TtsVoiceAssignmentMode.PerSpeaker : TtsVoiceAssignmentMode.GlobalDefault;
            if (TtsVoiceAssignmentMode == target) return;
            TtsVoiceAssignmentMode = target;
        }
    }

    internal sealed record ProviderDiagnosticsSelectionSnapshot(
        ComputeProfile TranscriptionRuntime,
        string TranscriptionProvider,
        string TranscriptionModel,
        ComputeProfile TranslationRuntime,
        string TranslationProvider,
        string TranslationModel,
        ComputeProfile TtsRuntime,
        string TtsProvider,
        string TtsModelOrVoice,
        string DiarizationProvider,
        string GpuServiceUrl);

    partial void OnTranscriptionRuntimeChanged(ComputeProfile value)
    {
        if (IsSynchronizingPipelineSettings)
            return;

        var provider = ResolveTranscriptionProviderForRuntime(value, TranscriptionProvider);
        var model = ResolveTranscriptionModelId(value, provider, TranscriptionModel);

        ApplyPipelineSettingsSelection(CreatePipelineSettingsSelection(
            transcriptionRuntime: value,
            transcriptionProvider: provider,
            transcriptionModel: model));
    }

    partial void OnTranscriptionProviderChanged(string value)
    {
        if (IsSynchronizingPipelineSettings || string.IsNullOrEmpty(value))
            return;

        var model = ResolveTranscriptionModelId(TranscriptionRuntime, value, TranscriptionModel);
        ApplyPipelineSettingsSelection(CreatePipelineSettingsSelection(
            transcriptionProvider: value,
            transcriptionModel: model));
    }

    partial void OnTranscriptionModelChanged(string value)
    {
        if (IsSynchronizingPipelineSettings || string.IsNullOrEmpty(value))
            return;

        ApplyPipelineSettingsSelection(CreatePipelineSettingsSelection(transcriptionModel: value));
    }

    partial void OnSelectedTranscriptionModelChanged(ModelOptionViewModel? value)
    {
        if (value is null || value.ModelId == TranscriptionModel)
            return;

        TranscriptionModel = value.ModelId;
    }

    partial void OnSelectedTranslationModelChanged(ModelOptionViewModel? value)
    {
        if (value is null || value.ModelId == TranslationModel)
            return;

        TranslationModel = value.ModelId;
    }

    partial void OnSelectedTtsOptionChanged(ModelOptionViewModel? value)
    {
        if (value is null || value.ModelId == TtsModelOrVoice)
            return;

        TtsModelOrVoice = value.ModelId;
    }

    partial void OnTranslationRuntimeChanged(ComputeProfile value)
    {
        if (IsSynchronizingPipelineSettings)
            return;

        var provider = ResolveTranslationProviderForRuntime(value, TranslationProvider);
        var model = ResolveTranslationModelId(value, provider, TranslationModel);

        ApplyPipelineSettingsSelection(CreatePipelineSettingsSelection(
            translationRuntime: value,
            translationProvider: provider,
            translationModel: model));
    }

    partial void OnTranslationProviderChanged(string value)
    {
        if (IsSynchronizingPipelineSettings || string.IsNullOrEmpty(value))
            return;

        var model = ResolveTranslationModelId(TranslationRuntime, value, TranslationModel);
        ApplyPipelineSettingsSelection(CreatePipelineSettingsSelection(
            translationProvider: value,
            translationModel: model));
    }

    partial void OnTranslationModelChanged(string value)
    {
        if (IsSynchronizingPipelineSettings || string.IsNullOrEmpty(value))
            return;

        ApplyPipelineSettingsSelection(CreatePipelineSettingsSelection(translationModel: value));
    }

    partial void OnTtsRuntimeChanged(ComputeProfile value)
    {
        if (IsSynchronizingPipelineSettings)
            return;

        var provider = ResolveTtsProviderForRuntime(value, TtsProvider);
        var model = ResolveTtsModelId(value, provider, TtsModelOrVoice);

        ApplyPipelineSettingsSelection(CreatePipelineSettingsSelection(
            ttsRuntime: value,
            ttsProvider: provider,
            ttsVoice: model));
    }

    partial void OnTtsProviderChanged(string value)
    {
        if (IsSynchronizingPipelineSettings || string.IsNullOrEmpty(value))
            return;

        var model = ResolveTtsModelId(TtsRuntime, value, TtsModelOrVoice);
        ApplyPipelineSettingsSelection(CreatePipelineSettingsSelection(
            ttsProvider: value,
            ttsVoice: model));
        NotifyTtsAssignmentModeUi();
    }

    partial void OnTtsVoiceAssignmentModeChanged(TtsVoiceAssignmentMode value)
    {
        if (IsSynchronizingPipelineSettings)
            return;

        _coordinator.CurrentSettings.TtsVoiceAssignmentMode = value;
        _coordinator.NotifySettingsModified();
        NotifyTtsAssignmentModeUi();
    }

    private void NotifyTtsAssignmentModeUi()
    {
        OnPropertyChanged(nameof(ShowTtsAssignmentModeSwitch));
        OnPropertyChanged(nameof(ShowPerSpeakerVoiceHint));
        OnPropertyChanged(nameof(TtsVoiceComboHeaderText));
        OnPropertyChanged(nameof(TtsVoiceUsePerSpeakerWizard));
    }

    partial void OnTtsModelOrVoiceChanged(string value)
    {
        if (IsSynchronizingPipelineSettings || string.IsNullOrEmpty(value))
            return;

        ApplyPipelineSettingsSelection(CreatePipelineSettingsSelection(ttsVoice: value));
    }

    partial void OnSelectedTargetLanguageOptionChanged(PipelineTargetLanguageOption? value)
    {
        if (IsSynchronizingPipelineSettings || value is null)
            return;

        ApplyPipelineSettingsSelection(CreatePipelineSettingsSelection());
    }

    partial void OnSelectedSpokenLanguageOptionChanged(SpokenLanguageOption? value)
    {
        if (IsSynchronizingPipelineSettings || value is null)
            return;

        ApplyPipelineSettingsSelection(CreatePipelineSettingsSelection());
    }

    private void OnCoordinatorSettingsModified()
    {
        Preview.SyncBilingualSubtitlesFromSettings();
        Preview.SyncPaneLayoutFromSettings();
        Preview.SyncDubMixControlFromSettings();
        SyncProviderModelFieldsFromSettings();
        NotifyActiveConfigChanged();
        OnPropertyChanged(nameof(VoiceModelLabel));
    }

    private void SyncProviderModelFieldsFromSettings()
    {
        IsSynchronizingPipelineSettings = true;
        try
        {
            TranscriptionRuntime = _coordinator.CurrentSettings.TranscriptionProfile;
            TranslationRuntime = _coordinator.CurrentSettings.TranslationProfile;
            TtsRuntime = _coordinator.CurrentSettings.TtsProfile;

            TranscriptionProvider = ResolveTranscriptionProviderForRuntime(
                TranscriptionRuntime,
                _coordinator.CurrentSettings.TranscriptionProvider);
            TranslationProvider = ResolveTranslationProviderForRuntime(
                TranslationRuntime,
                _coordinator.CurrentSettings.TranslationProvider);
            TtsProvider = ResolveTtsProviderForRuntime(
                TtsRuntime,
                _coordinator.CurrentSettings.TtsProvider);

            TranscriptionModel = ResolveTranscriptionModelId(
                TranscriptionRuntime,
                TranscriptionProvider,
                _coordinator.CurrentSettings.TranscriptionModel);
            TranslationModel = ResolveTranslationModelId(
                TranslationRuntime,
                TranslationProvider,
                _coordinator.CurrentSettings.TranslationModel);
            TtsModelOrVoice = ResolveTtsModelId(
                TtsRuntime,
                TtsProvider,
                _coordinator.CurrentSettings.TtsVoice);

            TtsVoiceAssignmentMode = _coordinator.CurrentSettings.TtsVoiceAssignmentMode;

            RebuildAllModelOptions();

            SelectedTranscriptionModel =
                _availableTranscriptionModels.FirstOrDefault(model => model.ModelId == TranscriptionModel)
                ?? (_availableTranscriptionModels.Count > 0 ? _availableTranscriptionModels[0] : null);
            SelectedTranslationModel =
                _availableTranslationModels.FirstOrDefault(model => model.ModelId == TranslationModel)
                ?? (_availableTranslationModels.Count > 0 ? _availableTranslationModels[0] : null);
            SelectedTtsOption =
                _availableTtsOptions.FirstOrDefault(model => model.ModelId == TtsModelOrVoice)
                ?? (_availableTtsOptions.Count > 0 ? _availableTtsOptions[0] : null);

            SelectedTargetLanguageOption =
                PipelineTargetLanguageOption.All.FirstOrDefault(o =>
                    string.Equals(o.Code, _coordinator.CurrentSettings.TargetLanguage, StringComparison.OrdinalIgnoreCase))
                ?? PipelineTargetLanguageOption.English;

            SelectedSpokenLanguageOption =
                SpokenLanguageOption.All.FirstOrDefault(o =>
                    SessionSnapshotSemantics.TranscriptionLanguageHintsMatch(
                        o.Code,
                        _coordinator.CurrentSettings.TranscriptionLanguageHint))
                ?? SpokenLanguageOption.All[0];

            OnPropertyChanged(nameof(AvailableTranscriptionModels));
            OnPropertyChanged(nameof(AvailableTranslationModels));
            OnPropertyChanged(nameof(AvailableTtsOptions));
            NotifyTtsAssignmentModeUi();
            OnPropertyChanged(nameof(VocalSeparationEnabled));
            OnPropertyChanged(nameof(ChatterboxVoiceCloneConsent));
            OnPropertyChanged(nameof(DubTimingMode));
            NotifyVocalSeparationCapabilityProperties();
            SpeakerRouting.SyncFromSettings();
            SpeakerRouting.NotifyTtsProviderChanged();
            RefreshProviderHealthDiagnostics();
        }
        finally
        {
            IsSynchronizingPipelineSettings = false;
        }
    }

    private void NotifyActiveConfigChanged()
    {
        OnPropertyChanged(nameof(ActiveTranscriptionConfigLine));
        OnPropertyChanged(nameof(ActiveCpuTuningLine));
        OnPropertyChanged(nameof(ActiveTranslationConfigLine));
        OnPropertyChanged(nameof(ActiveTtsConfigLine));
    }

    private void RebuildAllModelOptions()
    {
        RebuildTranscriptionModelOptions();
        RebuildTranslationModelOptions();
        RebuildTtsModelOptions();
    }

    private void RebuildTranscriptionModelOptions()
    {
        _availableTranscriptionModels =
        [
            .. _coordinator.TranscriptionRegistry
                .GetAvailableModels(TranscriptionProvider, TranscriptionRuntime, _coordinator.CurrentSettings)
                .Select(model => new ModelOptionViewModel(
                    model,
                    null,
                    GetTranscriptionModelAvailability(TranscriptionProvider, model)))
        ];
    }

    private void RebuildTranslationModelOptions()
    {
        _availableTranslationModels =
        [
            .. _coordinator.TranslationRegistry
                .GetAvailableModels(TranslationProvider, TranslationRuntime, _coordinator.CurrentSettings)
                .Select(model => new ModelOptionViewModel(
                    model,
                    null,
                    GetTranslationModelAvailability(TranslationProvider, model)))
        ];
    }

    private void RebuildTtsModelOptions()
    {
        _availableTtsOptions =
        [
            .. _coordinator.TtsRegistry
                .GetAvailableModels(TtsProvider, TtsRuntime, _coordinator.CurrentSettings)
                .Select(model => new ModelOptionViewModel(
                    model,
                    TtsProvider == ProviderNames.Qwen && model.StartsWith("Qwen/", StringComparison.Ordinal)
                        ? model[5..]
                        : null,
                    GetTtsModelAvailability(TtsProvider, model)))
        ];
    }

    private static bool? GetTranscriptionModelAvailability(string providerId, string model) =>
        providerId switch
        {
            ProviderNames.FasterWhisper => ModelDownloader.IsFasterWhisperDownloaded(model),
            _ => null,
        };

    private static bool? GetTranslationModelAvailability(string providerId, string model) =>
        providerId switch
        {
            ProviderNames.Nllb200 => ModelDownloader.IsNllbDownloaded(model),
            ProviderNames.CTranslate2 => ModelDownloader.IsCTranslate2TranslationModelDownloaded(model),
            _ => null,
        };

    private bool? GetTtsModelAvailability(string providerId, string model) =>
        providerId switch
        {
            ProviderNames.Piper => ModelDownloader.IsPiperVoiceDownloaded(model, _coordinator.CurrentSettings.PiperModelDir),
            _ => null,
        };

    private void ApplyPipelineSettingsSelection(PipelineSettingsSelection selection)
    {
        var result = _coordinator.ApplyPipelineSettings(selection);
        SyncProviderModelFieldsFromSettings();
        NotifyActiveConfigChanged();
        HandlePipelineSettingsApplyResult(result);
    }

    private void HandlePipelineSettingsApplyResult(PipelineSettingsApplyResult result)
    {
        if (!result.SettingsChanged)
            return;

        if (result.Invalidation != PipelineInvalidation.None)
            ResetInteractiveModes();

        StatusText = result.StatusMessage;
        ClearStatusErrorDetail();

        if (_coordinator.CurrentSession.Stage >= SessionWorkflowStage.Transcribed)
        {
            _ = Preview.RefreshSegmentsAsync();
        }
        else
        {
            Preview.ClearSegments();
        }
    }

    private void BuildProviderCaches()
    {
        var availableRuntimes = GetAvailableInferenceRuntimeOptions();

        BuildProviderCache(
            _transcriptionProvidersByRuntime,
            _transcriptionProviderIdsByRuntime,
            runtime => _coordinator.TranscriptionRegistry.GetAvailableProviders(runtime),
            availableRuntimes);

        BuildProviderCache(
            _translationProvidersByRuntime,
            _translationProviderIdsByRuntime,
            runtime => _coordinator.TranslationRegistry.GetAvailableProviders(runtime),
            availableRuntimes);

        BuildProviderCache(
            _ttsProvidersByRuntime,
            _ttsProviderIdsByRuntime,
            runtime => _coordinator.TtsRegistry.GetAvailableProviders(runtime),
            availableRuntimes);
    }

    private static void BuildProviderCache(
        Dictionary<ComputeProfile, IReadOnlyList<ProviderDescriptor>> descriptorCache,
        Dictionary<ComputeProfile, IReadOnlyList<string>> idCache,
        Func<ComputeProfile, IReadOnlyList<ProviderDescriptor>> providerFactory,
        IReadOnlyList<ComputeProfile> availableRuntimes)
    {
        descriptorCache.Clear();
        idCache.Clear();

        foreach (var runtime in availableRuntimes)
        {
            var providers = providerFactory(runtime)
                .Where(provider => provider.IsImplemented)
                .ToArray();
            descriptorCache[runtime] = providers;
            idCache[runtime] = [.. providers.Select(provider => provider.Id)];
        }
    }

    private IReadOnlyList<ComputeProfile> GetAvailableInferenceRuntimeOptions()
    {
        var hardware = _coordinator.HardwareSnapshot;
        return hardware.IsDetecting || hardware.HasCuda
            ? InferenceRuntimeOptionsWithGpu
            : InferenceRuntimeOptionsWithoutGpu;
    }

    private void RefreshRuntimeAvailabilityFromHardware()
    {
        BuildProviderCaches();
        OnPropertyChanged(nameof(InferenceRuntimeOptions));
        SyncProviderModelFieldsFromSettings();
    }

    private IReadOnlyList<string> GetTranscriptionProviderIds(ComputeProfile runtime) =>
        _transcriptionProviderIdsByRuntime.TryGetValue(runtime, out var providers)
            ? providers
            : [];

    private IReadOnlyList<string> GetTranslationProviderIds(ComputeProfile runtime) =>
        _translationProviderIdsByRuntime.TryGetValue(runtime, out var providers)
            ? providers
            : [];

    private IReadOnlyList<string> GetTtsProviderIds(ComputeProfile runtime) =>
        _ttsProviderIdsByRuntime.TryGetValue(runtime, out var providers)
            ? providers
            : [];

    private IReadOnlyList<ProviderDescriptor> GetTranscriptionProviderDescriptors(ComputeProfile runtime) =>
        _transcriptionProvidersByRuntime.TryGetValue(runtime, out var providers)
            ? providers
            : [];

    private IReadOnlyList<ProviderDescriptor> GetTranslationProviderDescriptors(ComputeProfile runtime) =>
        _translationProvidersByRuntime.TryGetValue(runtime, out var providers)
            ? providers
            : [];

    private IReadOnlyList<ProviderDescriptor> GetTtsProviderDescriptors(ComputeProfile runtime) =>
        _ttsProvidersByRuntime.TryGetValue(runtime, out var providers)
            ? providers
            : [];

    private string ResolveTranscriptionProviderForRuntime(ComputeProfile runtime, string? providerId)
    {
        var providers = GetTranscriptionProviderDescriptors(runtime);
        var normalized = InferenceRuntimeCatalog.NormalizeTranscriptionProvider(runtime, providerId);
        return providers.Any(provider => provider.Id == normalized)
            ? normalized
            : (providers.Count > 0 ? providers[0].Id : normalized);
    }

    private string ResolveTranslationProviderForRuntime(ComputeProfile runtime, string? providerId)
    {
        var providers = GetTranslationProviderDescriptors(runtime);
        var normalized = InferenceRuntimeCatalog.NormalizeTranslationProvider(runtime, providerId);
        return providers.Any(provider => provider.Id == normalized)
            ? normalized
            : (providers.Count > 0 ? providers[0].Id : normalized);
    }

    private string ResolveTtsProviderForRuntime(ComputeProfile runtime, string? providerId)
    {
        var providers = GetTtsProviderDescriptors(runtime);
        var normalized = InferenceRuntimeCatalog.NormalizeTtsProvider(runtime, providerId);
        return providers.Any(provider => provider.Id == normalized)
            ? normalized
            : (providers.Count > 0 ? providers[0].Id : normalized);
    }

    private string ResolveTranscriptionModelId(ComputeProfile runtime, string providerId, string? preferredModel) =>
        ResolveModelId(
            _coordinator.TranscriptionRegistry.GetAvailableModels(providerId, runtime, _coordinator.CurrentSettings),
            preferredModel);

    private string ResolveTranslationModelId(ComputeProfile runtime, string providerId, string? preferredModel) =>
        ResolveModelId(
            _coordinator.TranslationRegistry.GetAvailableModels(providerId, runtime, _coordinator.CurrentSettings),
            preferredModel);

    private string ResolveTtsModelId(ComputeProfile runtime, string providerId, string? preferredModel) =>
        ResolveModelId(
            _coordinator.TtsRegistry.GetAvailableModels(providerId, runtime, _coordinator.CurrentSettings),
            preferredModel);

    private static string ResolveModelId(IReadOnlyList<string> supportedModels, string? preferredModel)
    {
        if (supportedModels.Count == 0)
            return "default";

        if (!string.IsNullOrWhiteSpace(preferredModel)
            && supportedModels.Contains(preferredModel, StringComparer.Ordinal))
        {
            return preferredModel;
        }

        return supportedModels[0];
    }

    private PipelineSettingsSelection CreatePipelineSettingsSelection(
        ComputeProfile? transcriptionRuntime = null,
        string? transcriptionProvider = null,
        string? transcriptionModel = null,
        ComputeProfile? translationRuntime = null,
        string? translationProvider = null,
        string? translationModel = null,
        ComputeProfile? ttsRuntime = null,
        string? ttsProvider = null,
        string? ttsVoice = null,
        string? targetLanguageOverride = null,
        string? transcriptionLanguageHintOverride = null) =>
        new(
            transcriptionRuntime ?? TranscriptionRuntime,
            transcriptionProvider ?? TranscriptionProvider,
            transcriptionModel ?? TranscriptionModel,
            translationRuntime ?? TranslationRuntime,
            translationProvider ?? TranslationProvider,
            translationModel ?? TranslationModel,
            ttsRuntime ?? TtsRuntime,
            ttsProvider ?? TtsProvider,
            ttsVoice ?? TtsModelOrVoice,
            targetLanguageOverride
                ?? SelectedTargetLanguageOption?.Code
                ?? _coordinator.CurrentSettings.TargetLanguage,
            transcriptionLanguageHintOverride
                ?? SessionSnapshotSemantics.NormalizeTranscriptionLanguageHint(SelectedSpokenLanguageOption?.Code));

    private static string GetReadinessStatus(ProviderReadiness readiness)
    {
        if (readiness.IsReady)
            return string.Empty;

        if (readiness.RequiresModelDownload)
            return "⬇ Download required (will run automatically)";

        if (readiness.BlockingReason?.Contains(" is starting at ", StringComparison.Ordinal) == true)
            return $"⏳ {readiness.BlockingReason}";

        return $"⚠️ {readiness.BlockingReason}";
    }

    internal void RefreshProviderHealthDiagnostics(bool force = false)
    {
        var snapshot = CaptureProviderHealthSelectionSnapshot();
        QueueProviderHealthRefresh(snapshot, force);
    }

    internal ProviderDiagnosticsSelectionSnapshot CaptureProviderHealthSelectionSnapshot() =>
        new(
            TranscriptionRuntime,
            TranscriptionProvider,
            TranscriptionModel,
            TranslationRuntime,
            TranslationProvider,
            TranslationModel,
            TtsRuntime,
            TtsProvider,
            TtsModelOrVoice,
            SpeakerRouting.DiarizationProvider,
            _coordinator.CurrentSettings.EffectiveContainerizedServiceUrl);

    private void QueueProviderHealthRefresh(ProviderDiagnosticsSelectionSnapshot snapshot, bool force = false)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        if (!ShouldQueueProviderHealthRefresh(snapshot, force, nowUtc))
            return;

        _lastQueuedProviderHealthSnapshot = snapshot;
        var version = Interlocked.Increment(ref _providerHealthRefreshVersion);
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _providerHealthRefreshCts, cts);
        previous?.Cancel();
        previous?.Dispose();

        _coordinator.Log.Debug(
            $"Provider diagnostics refresh queued: v={version}, " +
            $"selection=({snapshot.TranscriptionRuntime}/{snapshot.TranscriptionProvider}/{snapshot.TranscriptionModel}, " +
            $"{snapshot.TranslationRuntime}/{snapshot.TranslationProvider}/{snapshot.TranslationModel}, " +
            $"{snapshot.TtsRuntime}/{snapshot.TtsProvider}/{snapshot.TtsModelOrVoice}, " +
            $"{snapshot.DiarizationProvider}), " +
            $"gpuServiceUrl={snapshot.GpuServiceUrl}");

        _ = RefreshProviderHealthDiagnosticsAsync(snapshot, version, cts.Token);
    }

    private async Task RefreshProviderHealthDiagnosticsAsync(
        ProviderDiagnosticsSelectionSnapshot snapshot,
        int version,
        CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);

            var health = await ComputeProviderHealthSnapshotsAsync(snapshot, cancellationToken);
            await ApplyProviderHealthSnapshotsAsync(health, version, cancellationToken);

            if (UsesContainerizedRuntime(snapshot)
                && ContainsStartingStatus(health)
                && _coordinator.ContainerizedProbe is not null)
            {
                _ = await _coordinator.ContainerizedProbe.WaitForProbeAsync(
                    snapshot.GpuServiceUrl,
                    forceRefresh: false,
                    waitTimeout: TimeSpan.FromSeconds(30),
                    cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                var settledHealth = await ComputeProviderHealthSnapshotsAsync(snapshot, cancellationToken);
                await ApplyProviderHealthSnapshotsAsync(settledHealth, version, cancellationToken);
            }

            stopwatch.Stop();
            _coordinator.Log.Debug(
                $"Provider diagnostics refresh complete: v={version}, elapsedMs={stopwatch.ElapsedMilliseconds}");
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            _coordinator.Log.Debug(
                $"Provider diagnostics refresh canceled: v={version}, elapsedMs={stopwatch.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _coordinator.Log.Error(
                $"Provider diagnostics refresh failed: v={version}, elapsedMs={stopwatch.ElapsedMilliseconds}",
                ex);
        }
    }

    private Task<IReadOnlyList<ProviderHealthSnapshot>> ComputeProviderHealthSnapshotsAsync(
        ProviderDiagnosticsSelectionSnapshot snapshot,
        CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var transcription = BuildTranscriptionHealthSnapshot(snapshot);
            var translation = BuildTranslationHealthSnapshot(snapshot);
            var tts = BuildTtsHealthSnapshot(snapshot);
            var diarization = BuildDiarizationHealthSnapshot(snapshot);
            return (IReadOnlyList<ProviderHealthSnapshot>)[transcription, translation, tts, diarization];
        }, cancellationToken);

    private async Task ApplyProviderHealthSnapshotsAsync(
        IReadOnlyList<ProviderHealthSnapshot> health,
        int version,
        CancellationToken cancellationToken)
    {
        void Apply()
        {
            if (version != _providerHealthRefreshVersion || cancellationToken.IsCancellationRequested)
                return;

            _providerHealthSnapshots.Clear();
            foreach (var snapshot in health)
                _providerHealthSnapshots.Add(snapshot);
            _lastProviderHealthRefreshAtUtc = DateTimeOffset.UtcNow;

            var transcription = health.FirstOrDefault(entry => string.Equals(entry.Section, "Transcription", StringComparison.Ordinal));
            var translation = health.FirstOrDefault(entry => string.Equals(entry.Section, "Translation", StringComparison.Ordinal));
            var tts = health.FirstOrDefault(entry => string.Equals(entry.Section, "TTS", StringComparison.Ordinal));
            var diarization = health.FirstOrDefault(entry => string.Equals(entry.Section, "Diarization", StringComparison.Ordinal));

            ApplyReadinessStatus(ref _transcriptionKeyStatus, transcription?.InlineStatus ?? string.Empty, nameof(TranscriptionKeyStatus));
            ApplyReadinessStatus(ref _translationKeyStatus, translation?.InlineStatus ?? string.Empty, nameof(TranslationKeyStatus));
            ApplyReadinessStatus(ref _ttsKeyStatus, tts?.InlineStatus ?? string.Empty, nameof(TtsKeyStatus));
            SpeakerRouting.SetAutoSpeakerDetectionStatus(
                diarization?.InlineStatus ?? "Manual speaker mapping is the default release flow.");
        }

        if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            Apply();
        }
        else
        {
            await Dispatcher.UIThread.InvokeAsync(Apply);
        }
    }

    private static string GetRuntimeHostLabel(ComputeProfile runtime) => runtime switch
    {
        ComputeProfile.Gpu => "Managed local GPU host",
        ComputeProfile.Cpu => "Managed local CPU runtime",
        ComputeProfile.Cloud => "Cloud service",
        _ => "Managed local CPU runtime",
    };

    internal ProviderHealthSnapshot BuildTranscriptionHealthSnapshot(ProviderDiagnosticsSelectionSnapshot snapshot) =>
        BuildHealthSnapshot(
            section: "Transcription",
            providerId: snapshot.TranscriptionProvider,
            selectionLabel: $"{snapshot.TranscriptionRuntime} / {snapshot.TranscriptionProvider} / {snapshot.TranscriptionModel}",
            runtimeLabel: snapshot.TranscriptionRuntime.ToString(),
            isContainerized: snapshot.TranscriptionRuntime == ComputeProfile.Gpu,
            gpuServiceUrl: snapshot.GpuServiceUrl,
            readinessFactory: () => _coordinator.TranscriptionRegistry.CheckReadiness(
                snapshot.TranscriptionProvider,
                snapshot.TranscriptionModel,
                _coordinator.CurrentSettings,
                _apiKeyStore,
                snapshot.TranscriptionRuntime),
            statusLineFactory: readiness => readiness.IsReady ? "Ready" : GetReadinessStatus(readiness),
            inlineStatusFactory: GetReadinessStatus,
            hostLabel: GetRuntimeHostLabel(snapshot.TranscriptionRuntime));

    internal ProviderHealthSnapshot BuildTranslationHealthSnapshot(ProviderDiagnosticsSelectionSnapshot snapshot) =>
        BuildHealthSnapshot(
            section: "Translation",
            providerId: snapshot.TranslationProvider,
            selectionLabel: $"{snapshot.TranslationRuntime} / {snapshot.TranslationProvider} / {snapshot.TranslationModel}",
            runtimeLabel: snapshot.TranslationRuntime.ToString(),
            isContainerized: snapshot.TranslationRuntime == ComputeProfile.Gpu,
            gpuServiceUrl: snapshot.GpuServiceUrl,
            readinessFactory: () => _coordinator.TranslationRegistry.CheckReadiness(
                snapshot.TranslationProvider,
                snapshot.TranslationModel,
                _coordinator.CurrentSettings,
                _apiKeyStore,
                snapshot.TranslationRuntime),
            statusLineFactory: readiness => readiness.IsReady ? "Ready" : GetReadinessStatus(readiness),
            inlineStatusFactory: GetReadinessStatus,
            hostLabel: GetRuntimeHostLabel(snapshot.TranslationRuntime));

    internal ProviderHealthSnapshot BuildTtsHealthSnapshot(ProviderDiagnosticsSelectionSnapshot snapshot) =>
        BuildHealthSnapshot(
            section: "TTS",
            providerId: snapshot.TtsProvider,
            selectionLabel: $"{snapshot.TtsRuntime} / {snapshot.TtsProvider} / {snapshot.TtsModelOrVoice}",
            runtimeLabel: snapshot.TtsRuntime.ToString(),
            isContainerized: snapshot.TtsRuntime == ComputeProfile.Gpu,
            gpuServiceUrl: snapshot.GpuServiceUrl,
            readinessFactory: () => _coordinator.TtsRegistry.CheckReadiness(
                snapshot.TtsProvider,
                snapshot.TtsModelOrVoice,
                _coordinator.CurrentSettings,
                _apiKeyStore,
                snapshot.TtsRuntime),
            statusLineFactory: readiness => readiness.IsReady ? "Ready" : GetReadinessStatus(readiness),
            inlineStatusFactory: GetReadinessStatus,
            hostLabel: GetRuntimeHostLabel(snapshot.TtsRuntime));

    internal ProviderHealthSnapshot BuildDiarizationHealthSnapshot(ProviderDiagnosticsSelectionSnapshot snapshot)
    {
        var registry = _coordinator.DiarizationRegistry;
        if (registry is null)
        {
            return BuildManualDiarizationSnapshot(
                "⚠ Speaker diarization is unavailable in this build. Manual mapping remains available.");
        }

        if (string.IsNullOrWhiteSpace(snapshot.DiarizationProvider))
            return BuildManualDiarizationSnapshot("Manual speaker mapping is the default release flow.");

        var provider = registry
            .GetAvailableProviders()
            .FirstOrDefault(descriptor => string.Equals(descriptor.Id, snapshot.DiarizationProvider, StringComparison.Ordinal));

        if (provider is null)
        {
            return BuildManualDiarizationSnapshot(
                $"⚠ Unknown diarization provider '{snapshot.DiarizationProvider}'. Manual mapping will still work.");
        }

        var isContainerized = provider.EffectiveDefaultRuntime == InferenceRuntime.Containerized;
        var hostLabel = isContainerized ? "Managed local GPU host" : "Managed local CPU runtime";
        return BuildHealthSnapshot(
            section: "Diarization",
            providerId: provider.Id,
            selectionLabel: $"{provider.EffectiveDefaultRuntime} / {provider.DisplayName}",
            runtimeLabel: provider.EffectiveDefaultRuntime.ToString(),
            isContainerized: isContainerized,
            gpuServiceUrl: snapshot.GpuServiceUrl,
            readinessFactory: () => registry.CheckReadiness(provider.Id, _coordinator.CurrentSettings, _apiKeyStore),
            statusLineFactory: readiness => readiness.IsReady ? "Ready" : GetReadinessStatus(readiness),
            inlineStatusFactory: readiness =>
                readiness.IsReady
                    ? $"Speaker diarization is enabled via {provider.DisplayName}."
                    : $"⚠ {provider.DisplayName} is not ready: {readiness.BlockingReason}. Manual mapping will still work.",
            hostLabel: hostLabel);
    }

    private ProviderHealthSnapshot BuildManualDiarizationSnapshot(string inlineStatus) =>
        new(
            "Diarization",
            ProviderNames.Manual,
            "Manual speaker mapping",
            "Local",
            "Not configured",
            NormalizeDiagnosticText(inlineStatus),
            "No diarization provider selected.",
            "No diarization provider selected.",
            string.Empty,
            IsReady: false,
            IsLive: false,
            IsStale: false,
            CheckedAtText: DateTimeOffset.UtcNow.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture),
            History: CreateSingleProviderHealthHistoryLine(
                DateTimeOffset.UtcNow,
                "Not configured",
                "No diarization provider selected.",
                isReady: false));

    private ProviderHealthSnapshot BuildHealthSnapshot(
        string section,
        string providerId,
        string selectionLabel,
        string runtimeLabel,
        bool isContainerized,
        string? gpuServiceUrl,
        Func<ProviderReadiness> readinessFactory,
        Func<ProviderReadiness, string> statusLineFactory,
        Func<ProviderReadiness, string> inlineStatusFactory,
        string hostLabel)
    {
        ProviderReadiness readiness;
        try
        {
            readiness = readinessFactory();
        }
        catch (Exception ex)
        {
            var checkedAtUtc = DateTimeOffset.UtcNow;
            var hostStateText = isContainerized
                ? $"{hostLabel} unavailable"
                : $"{hostLabel} ({runtimeLabel})";
            var statusLineText = $"⚠ {section} readiness check failed";
            var inlineStatusText = $"⚠ {section} readiness check failed: {ex.Message}";
            var historyEntries = CreateSingleProviderHealthHistoryLine(
                checkedAtUtc,
                statusLineText,
                hostStateText,
                isReady: false);

            return new ProviderHealthSnapshot(
                section,
                providerId,
                selectionLabel,
                runtimeLabel,
                statusLineText,
                inlineStatusText,
                ex.Message,
                hostStateText,
                string.Empty,
                IsReady: false,
                IsLive: false,
                IsStale: false,
                CheckedAtText: checkedAtUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture),
                History: historyEntries);
        }

        ContainerizedProbeResult? probeResult = null;
        if (isContainerized && _coordinator.ContainerizedProbe is not null && !string.IsNullOrWhiteSpace(gpuServiceUrl))
            probeResult = _coordinator.ContainerizedProbe.GetCurrentOrStartBackgroundProbe(gpuServiceUrl);

        var remoteProviderHealth = ResolveRemoteProviderHealth(probeResult, section, providerId);
        var checkedAt = DateTimeOffset.UtcNow;
        var statusLine = NormalizeDiagnosticText(statusLineFactory(readiness));
        var inlineStatus = NormalizeDiagnosticText(inlineStatusFactory(readiness));
        var detail = string.IsNullOrWhiteSpace(remoteProviderHealth?.Detail)
            ? readiness.RequiresModelDownload
                ? readiness.ModelDownloadDescription ?? readiness.BlockingReason ?? "Model download required."
                : readiness.BlockingReason ?? (readiness.IsReady ? "Ready" : "Not ready")
            : remoteProviderHealth!.Detail!;
        var hostState = BuildHostStateText(hostLabel, runtimeLabel, probeResult, isContainerized);
        var metricsText = BuildMetricsText(section, providerId, probeResult, remoteProviderHealth);
        var history = remoteProviderHealth is { History.Count: > 0 }
            ? new[]
            {
                FormatProviderHistoryEntry(remoteProviderHealth.History[^1]),
            }
            : CreateSingleProviderHealthHistoryLine(
                checkedAt,
                statusLine,
                hostState,
                readiness.IsReady);
        var checkedAtText = TryFormatCheckedAt(remoteProviderHealth?.CheckedAt)
            ?? checkedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);

        return new ProviderHealthSnapshot(
            section,
            providerId,
            selectionLabel,
            runtimeLabel,
            statusLine,
            inlineStatus,
            detail,
            hostState,
            metricsText,
            IsReady: readiness.IsReady,
            IsLive: isContainerized ? probeResult?.State == ContainerizedProbeState.Available : readiness.IsReady,
            IsStale: probeResult?.IsStale == true || remoteProviderHealth?.IsStale == true,
            CheckedAtText: checkedAtText,
            History: history);
    }

    private static string NormalizeDiagnosticText(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? text
            : text
                .Replace("âš  ", "Warning: ", StringComparison.Ordinal)
                .Replace("âœ“", "ok", StringComparison.Ordinal)
                .Replace("Â·", "|", StringComparison.Ordinal);

    private static string BuildHostStateText(
        string hostLabel,
        string runtimeLabel,
        ContainerizedProbeResult? probeResult,
        bool isContainerized)
    {
        if (!isContainerized)
            return $"{hostLabel} ({runtimeLabel})";

        if (probeResult is null)
            return $"{hostLabel} checking";

        return probeResult.State switch
        {
            ContainerizedProbeState.Checking => $"{hostLabel} checking",
            ContainerizedProbeState.Unavailable => $"{hostLabel} unavailable",
            ContainerizedProbeState.Available when probeResult.IsStale => $"{hostLabel} live (stale)",
            ContainerizedProbeState.Available => $"{hostLabel} live",
            _ => $"{hostLabel} checking",
        };
    }

    private static ContainerProviderHealthSnapshot? ResolveRemoteProviderHealth(
        ContainerizedProbeResult? probeResult,
        string section,
        string providerId)
    {
        if (probeResult is null || string.IsNullOrWhiteSpace(providerId))
            return null;

        if (string.Equals(section, "TTS", StringComparison.Ordinal))
        {
            if (probeResult.Capabilities?.TryGetTtsProviderHealth(providerId, out var ttsHealth) == true)
                return ttsHealth;

            if (probeResult.ProviderHealth is not null && probeResult.ProviderHealth.TryGetValue(providerId, out var liveTtsHealth))
                return liveTtsHealth;
        }

        if (string.Equals(section, "Diarization", StringComparison.Ordinal))
        {
            if (probeResult.Capabilities?.TryGetDiarizationProviderHealth(providerId, out var diarizationHealth) == true)
                return diarizationHealth;

            if (probeResult.ProviderHealth is not null && probeResult.ProviderHealth.TryGetValue(providerId, out var liveDiarizationHealth))
                return liveDiarizationHealth;
        }

        return null;
    }

    private static string BuildMetricsText(
        string section,
        string providerId,
        ContainerizedProbeResult? probeResult,
        ContainerProviderHealthSnapshot? remoteProviderHealth)
    {
        if (!string.Equals(section, "TTS", StringComparison.Ordinal)
            || !string.Equals(providerId, ProviderNames.Qwen, StringComparison.Ordinal)
            || probeResult is null)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if (probeResult.QwenMaxConcurrency > 0)
            parts.Add($"Qwen concurrency {probeResult.QwenMaxConcurrency}");
        if (probeResult.QwenQueueDepth > 0)
            parts.Add($"queue {probeResult.QwenQueueDepth}");
        if (probeResult.ActiveQwenRequests > 0)
            parts.Add($"active {probeResult.ActiveQwenRequests}");
        if (probeResult.QwenLastQueueWaitMs.HasValue)
            parts.Add($"last wait {probeResult.QwenLastQueueWaitMs.Value:F0} ms");
        if (probeResult.QwenLastReferencePrepMs.HasValue)
            parts.Add($"ref {probeResult.QwenLastReferencePrepMs.Value:F0} ms");
        if (probeResult.QwenLastGenerationMs.HasValue)
            parts.Add($"gen {probeResult.QwenLastGenerationMs.Value:F0} ms");
        if (probeResult.QwenLastWarmupMs.HasValue)
            parts.Add($"warmup {probeResult.QwenLastWarmupMs.Value:F0} ms");

        if (parts.Count == 0 && remoteProviderHealth?.Metrics is { Count: > 0 })
        {
            foreach (var metric in remoteProviderHealth.Metrics)
                parts.Add($"{metric.Key}={metric.Value}");
        }

        return parts.Count == 0 ? string.Empty : string.Join(" · ", parts);
    }

    private static string FormatProviderHistoryEntry(ContainerProviderHealthHistoryEntry entry)
    {
        var timestamp = TryFormatCheckedAt(entry.Timestamp) ?? "unknown";
        var state = entry.Ready ? "ready" : "not ready";
        var detail = string.IsNullOrWhiteSpace(entry.Detail) ? string.Empty : $" · {entry.Detail}";
        var category = string.IsNullOrWhiteSpace(entry.FailureCategory) ? string.Empty : $" · {entry.FailureCategory}";
        return $"{timestamp} · {state}{detail}{category}";
    }

    private static string? TryFormatCheckedAt(string? isoTimestamp)
    {
        if (string.IsNullOrWhiteSpace(isoTimestamp))
            return null;

        if (!DateTimeOffset.TryParse(isoTimestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            return isoTimestamp;

        return parsed.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
    }

    /// <summary>Single latest line for tests/diagnostics; main UI uses Detail/HostState/StatusLine only.</summary>
    private static IReadOnlyList<string> CreateSingleProviderHealthHistoryLine(
        DateTimeOffset checkedAtUtc,
        string statusLine,
        string hostState,
        bool isReady)
    {
        var entry =
            $"{checkedAtUtc.ToLocalTime():HH:mm:ss} · {(isReady ? "ready" : "not ready")} · {statusLine}" +
            (string.IsNullOrWhiteSpace(hostState) ? string.Empty : $" · {hostState}");
        return [entry];
    }

    private bool UsesContainerizedRuntime(ProviderDiagnosticsSelectionSnapshot snapshot) =>
        snapshot.TranscriptionRuntime == ComputeProfile.Gpu
        || snapshot.TranslationRuntime == ComputeProfile.Gpu
        || snapshot.TtsRuntime == ComputeProfile.Gpu
        || IsContainerizedDiarizationProvider(snapshot.DiarizationProvider);

    private bool IsContainerizedDiarizationProvider(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId) || _coordinator.DiarizationRegistry is null)
            return false;

        var provider = _coordinator.DiarizationRegistry
            .GetAvailableProviders()
            .FirstOrDefault(descriptor => string.Equals(descriptor.Id, providerId, StringComparison.Ordinal));
        return provider?.EffectiveDefaultRuntime == InferenceRuntime.Containerized;
    }

    private static bool ContainsStartingStatus(IReadOnlyList<ProviderHealthSnapshot> health) =>
        health.Any(snapshot => IsStartingStatus(snapshot.StatusLine));

    private static bool IsStartingStatus(string status) => status.StartsWith('⏳');
    private bool HasTransientProviderHealthState() =>
        _providerHealthSnapshots.Any(snapshot =>
            snapshot.IsStale
            || IsStartingStatus(snapshot.StatusLine)
            || snapshot.HostState.Contains("checking", StringComparison.OrdinalIgnoreCase));

    internal bool ShouldQueueProviderHealthRefresh(
        ProviderDiagnosticsSelectionSnapshot snapshot,
        bool force,
        DateTimeOffset nowUtc)
    {
        if (force || snapshot != _lastQueuedProviderHealthSnapshot)
            return true;

        var ttl = HasTransientProviderHealthState()
            ? TimeSpan.FromSeconds(3)
            : TimeSpan.FromSeconds(8);
        return nowUtc - _lastProviderHealthRefreshAtUtc >= ttl;
    }

    internal void RecordProviderHealthRefreshForTests(
        ProviderDiagnosticsSelectionSnapshot snapshot,
        DateTimeOffset refreshedAtUtc)
    {
        _lastQueuedProviderHealthSnapshot = snapshot;
        _lastProviderHealthRefreshAtUtc = refreshedAtUtc;
    }

    internal string ResolveDiarizationProviderLabel()
    {
        var diarizationProvider = SpeakerRouting.DiarizationProvider;
        if (string.IsNullOrWhiteSpace(diarizationProvider))
            return "speaker";

        var registry = _coordinator.DiarizationRegistry;
        return registry?
            .GetAvailableProviders()
            .FirstOrDefault(provider => string.Equals(provider.Id, diarizationProvider, StringComparison.Ordinal))
            ?.DisplayName
            ?? diarizationProvider;
    }

    private void ApplyReadinessStatus(ref string field, string value, string propertyName)
    {
        if (string.Equals(field, value, StringComparison.Ordinal))
            return;

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void DisposeProviderHealthDiagnostics()
    {
        _providerHealthRefreshCts?.Cancel();
        _providerHealthRefreshCts?.Dispose();
        _providerHealthRefreshCts = null;
    }
}
