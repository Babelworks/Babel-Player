using System;
using System.Linq;
using System.Text.Json.Serialization;
using Babel.Player.Models;

namespace Babel.Player.Services.Settings;

/// <summary>
/// User-configurable application preferences. Serialised to app-settings.json.
/// </summary>
public sealed class AppSettings
{
    public const string InferenceServiceUrlEnvVar = "INFERENCE_SERVICE_URL";
    public const string ManagedGpuServiceUrl = "http://127.0.0.1:18000";
    public const double PipelinePaneDefaultWidth = 260;
    public const double SegmentsPaneDefaultWidth = 340;

    /// <summary>Transcription provider identifier (e.g. "faster-whisper", "openai-whisper-api").</summary>
    public string TranscriptionProvider { get; set; } = ProviderNames.FasterWhisper;

    /// <summary>Public compute profile used for transcription: CPU, GPU, or cloud.</summary>
    public ComputeProfile TranscriptionProfile { get; set; } = ComputeProfile.Cpu;

    /// <summary>Transcription model name within the selected provider (e.g. "tiny", "base", "large-v3").</summary>
    public string TranscriptionModel { get; set; } = "tiny";

    /// <summary>
    /// CPU compute type used by transcription providers when running on CPU.
    /// "auto" resolves to int8_float16 when AVX-512F is available, otherwise int8.
    /// </summary>
    public string TranscriptionCpuComputeType { get; set; } = "auto";

    /// <summary>
    /// CPU thread count hint for transcription providers.
    /// 0 means provider/runtime default auto-selection.
    /// </summary>
    public int TranscriptionCpuThreads { get; set; } = 0;

    /// <summary>
    /// When true, <see cref="TranscriptionNumWorkers"/> is derived from hardware (cores + RAM).
    /// When false, <see cref="TranscriptionNumWorkers"/> is used and clamped to safe bounds.
    /// Legacy settings files without this field deserialize as false (manual).
    /// </summary>
    public bool TranscriptionNumWorkersUseAuto { get; set; } = true;

    /// <summary>
    /// Manual worker count when <see cref="TranscriptionNumWorkersUseAuto"/> is false; ignored when auto.
    /// </summary>
    public int TranscriptionNumWorkers { get; set; } = 1;

    /// <summary>
    /// Optional Whisper/source-language hint (ISO-639-1, e.g. <c>es</c>). Null or empty = auto-detect.
    /// </summary>
    public string? TranscriptionLanguageHint { get; set; }

    /// <summary>
    /// When non-empty, enables speaker separation using a local diarization provider
    /// (WeSpeaker or SortFormer). Empty string skips diarization (recommended for single-speaker media).
    /// </summary>
    public string DiarizationProvider { get; set; } = string.Empty;

    /// <summary>
    /// Enables optional vocal separation before transcription when container capability is available.
    /// </summary>
    public bool VocalSeparationEnabled { get; set; } = false;

    /// <summary>
    /// Whether the pipeline pane is open in the main window.
    /// </summary>
    public bool IsPipelinePaneVisible { get; set; } = true;

    /// <summary>
    /// Whether the segments pane is open in the main window.
    /// </summary>
    public bool IsSegmentsPaneVisible { get; set; } = true;

    /// <summary>
    /// Persisted width of the pipeline pane.
    /// </summary>
    public double PipelinePaneWidth { get; set; } = PipelinePaneDefaultWidth;

    /// <summary>
    /// Persisted width of the segments pane.
    /// </summary>
    public double SegmentsPaneWidth { get; set; } = SegmentsPaneDefaultWidth;

    /// <summary>
    /// When true, the pipeline and segments panes swap physical sides.
    /// </summary>
    public bool SwapPaneSides { get; set; }

    /// <summary>
    /// Optional lower bound on the number of speakers to detect.
    /// null means no lower bound hint.
    /// </summary>
    public int? DiarizationMinSpeakers { get; set; } = null;

    /// <summary>
    /// Optional upper bound on the number of speakers to detect.
    /// null means no upper bound hint.
    /// </summary>
    public int? DiarizationMaxSpeakers { get; set; } = null;

    /// <summary>Translation provider identifier (e.g. "ctranslate2", "openai").</summary>
    public string TranslationProvider { get; set; } = ProviderNames.CTranslate2;

    /// <summary>Public compute profile used for translation: CPU, GPU, or cloud.</summary>
    public ComputeProfile TranslationProfile { get; set; } = ComputeProfile.Cpu;

    /// <summary>Translation model name within the selected provider.</summary>
    public string TranslationModel { get; set; } = "nllb-200-distilled-600M";

    /// <summary>TTS provider identifier (e.g. "piper", "edge-tts", "elevenlabs").</summary>
    public string TtsProvider { get; set; } = ProviderNames.Piper;

    /// <summary>Public compute profile used for TTS: CPU, GPU, or cloud.</summary>
    public ComputeProfile TtsProfile { get; set; } = ComputeProfile.Cpu;

    /// <summary>
    /// TTS voice or model selection. For Piper this is a voice model filename (e.g. en_US-lessac-medium);
    /// for cloud providers it is the voice name.
    /// </summary>
    public string TtsVoice { get; set; } = "en_US-lessac-medium";

    /// <summary>For Piper/Edge: use one global voice vs assign per speaker in the Speaker Reference Wizard (fallback = <see cref="TtsVoice"/>).</summary>
    public TtsVoiceAssignmentMode TtsVoiceAssignmentMode { get; set; } = TtsVoiceAssignmentMode.GlobalDefault;

    /// <summary>BCP-47 language code for the translation target.</summary>
    public string TargetLanguage { get; set; } = "en";

    /// <summary>
    /// Directory where Piper voice model (.onnx) files are stored.
    /// Empty string = use platform default (%LOCALAPPDATA%\piper\voices on Windows,
    /// ~/.local/share/piper/voices on Linux/macOS).
    /// </summary>
    public string PiperModelDir { get; set; } = "";

    /// <summary>
    /// Directory where Chatterbox ONNX model files are stored.
    /// Empty string = use platform default (%LOCALAPPDATA%\BabelPlayer\models\chatterbox-multilingual on Windows).
    /// </summary>
    public string ChatterboxModelDir { get; set; } = "";

    /// <summary>
    /// Directory where SortFormer ONNX model files are stored.
    /// Empty string = use platform default under LocalApplicationData
    /// (<c>BabelPlayer/models/sortformer-4spk</c> on all platforms).
    /// </summary>
    public string SortFormerModelDir { get; set; } = "";

    /// <summary>
    /// Explicit user consent for Chatterbox voice cloning. Cloning never runs unless
    /// this is true (Settings checkbox or explicit headless opt-in). Mandatory and
    /// non-bypassable by design.
    /// </summary>
    public bool ChatterboxVoiceCloneConsent { get; set; } = false;

    /// <summary>
    /// Preferred local GPU hosting backend when a stage uses the GPU compute profile.
    /// </summary>
    public GpuHostBackend PreferredLocalGpuBackend { get; set; } = GpuHostBackend.ManagedVenv;

    /// <summary>
    /// When true, the app will attempt to start the selected local GPU host
    /// during app startup even if no stage is currently set to the GPU profile.
    /// </summary>
    public bool AlwaysStartLocalGpuRuntimeAtAppStart { get; set; } = true;

    /// <summary>
    /// Base URL of the advanced Docker-hosted inference service used when
    /// <see cref="PreferredLocalGpuBackend"/> is <see cref="GpuHostBackend.DockerHost"/>.
    /// </summary>
    public string AdvancedGpuServiceUrl { get; set; } = "http://127.0.0.1:8000";

    [JsonIgnore]
    public InferenceRuntime TranscriptionRuntime
    {
        get => InferenceRuntimeCatalog.ResolveRuntime(TranscriptionProfile);
        set => TranscriptionProfile = InferenceRuntimeCatalog.MapLegacyRuntimeToProfile(value);
    }

    [JsonIgnore]
    public InferenceRuntime TranslationRuntime
    {
        get => InferenceRuntimeCatalog.ResolveRuntime(TranslationProfile);
        set => TranslationProfile = InferenceRuntimeCatalog.MapLegacyRuntimeToProfile(value);
    }

    [JsonIgnore]
    public InferenceRuntime TtsRuntime
    {
        get => InferenceRuntimeCatalog.ResolveRuntime(TtsProfile);
        set => TtsProfile = InferenceRuntimeCatalog.MapLegacyRuntimeToProfile(value);
    }

    [JsonIgnore]
    public string ContainerizedServiceUrl
    {
        get => AdvancedGpuServiceUrl;
        set => AdvancedGpuServiceUrl = value;
    }

    [JsonIgnore]
    public bool AlwaysRunContainerAtAppStart
    {
        get => AlwaysStartLocalGpuRuntimeAtAppStart;
        set => AlwaysStartLocalGpuRuntimeAtAppStart = value;
    }

    [JsonIgnore]
    public string EffectiveGpuServiceUrl
    {
        get
        {
            if (PreferredLocalGpuBackend == GpuHostBackend.ManagedVenv)
                return ManagedGpuServiceUrl;

            var overrideValue = Environment.GetEnvironmentVariable(InferenceServiceUrlEnvVar);
            return string.IsNullOrWhiteSpace(overrideValue)
                ? AdvancedGpuServiceUrl
                : overrideValue.Trim();
        }
    }

    [JsonIgnore]
    public string EffectiveContainerizedServiceUrl => EffectiveGpuServiceUrl;

    public void NormalizeLegacyInferenceSettings() =>
        InferenceRuntimeCatalog.NormalizeSettings(this);

    /// <summary>
    /// libmpv hardware video decode method.
    /// "auto" lets mpv pick the best available decoder for the current GPU.
    /// Options: auto, auto-safe, no, d3d11va, d3d11va-copy, nvdec, nvdec-copy, qsv, dxva2.
    /// Takes effect on app restart.
    /// </summary>
    public string VideoHwdec { get; set; } = "auto";

    /// <summary>
    /// libmpv GPU rendering API.
    /// "auto" lets mpv pick (d3d11 on Windows, opengl elsewhere).
    /// Options: auto, d3d11, vulkan, opengl.
    /// Takes effect on app restart.
    /// </summary>
    public string VideoGpuApi { get; set; } = "auto";

    /// <summary>
    /// Use the gpu-next video output backend instead of the legacy gpu backend.
    /// Required for RTX Video Super Resolution and mpv HDR passthrough.
    /// Takes effect on app restart. Default true (modern Windows + GPU drivers handle this well).
    /// </summary>
    public bool VideoUseGpuNext { get; set; } = true;

    /// <summary>
    /// Enable NVIDIA RTX Video Super Resolution via the d3d11vpp filter.
    /// Requires gpu-next backend, RTX GPU, driver >= 551.23, and
    /// "RTX Video Enhancement" enabled in NVIDIA Control Panel.
    /// VSR quality remains driver-controlled; mpv does not expose a per-filter quality setting.
    /// Takes effect on app restart.
    /// </summary>
    public bool VideoVsrEnabled { get; set; } = false;

    /// <summary>
    /// HDR strategy for embedded playback: off, NVIDIA driver RTX/Auto HDR, or mpv HDR passthrough.
    /// Modes are mutually exclusive. Driver RTX HDR avoids mpv HDR init options; passthrough applies
    /// tone-mapping settings below. Requires gpu-next, and a non-off mode requires Windows HDR active.
    /// Takes effect on app restart.
    /// </summary>
    public VideoHdrPlaybackMode VideoHdrPlaybackMode { get; set; } = VideoHdrPlaybackMode.Off;

    /// <summary>
    /// HDR tone-mapping algorithm used by mpv when <see cref="VideoHdrPlaybackMode"/> is
    /// <see cref="VideoHdrPlaybackMode.MpvHdrPassthrough"/>. Options: bt.2390, mobius, clip, auto.
    /// Takes effect on app restart.
    /// </summary>
    public string VideoToneMapping { get; set; } = "bt.2390";

    /// <summary>
    /// Display peak brightness target in nits, or "auto".
    /// Used by mpv when <see cref="VideoHdrPlaybackMode"/> is <see cref="VideoHdrPlaybackMode.MpvHdrPassthrough"/>.
    /// Takes effect on app restart.
    /// </summary>
    public string VideoTargetPeak { get; set; } = "auto";

    /// <summary>
    /// Enable dynamic per-frame peak detection for HDR tone-mapping (mpv passthrough mode only).
    /// May cause brightness instability on some content. Default true.
    /// Takes effect on app restart.
    /// </summary>
    public bool VideoHdrComputePeak { get; set; } = true;

    /// <summary>
    /// ffmpeg video encoder used for MP4 export (main window → Export → to .mp4).
    /// "auto" = resolve at export time from hardware detection.
    /// Options: auto, h264_nvenc, hevc_nvenc, h264_amf, hevc_amf, h264_qsv, hevc_qsv, libx264, libx265.
    /// </summary>
    public string VideoExportEncoder { get; set; } = "auto";

    /// <summary>
    /// Session-level dub timing strategy applied during rendered dub composition and as the
    /// default timing mode for dub preview when a segment does not carry its own override.
    /// </summary>
    public SegmentTimingMode DubTimingMode { get; set; } = SegmentTimingMode.Off;

    /// <summary>
    /// Ambiance attenuation in dB applied when a separated ambiance stem is mixed back under the dub.
    /// 0 dB keeps the ambiance at full level; more negative values make it quieter.
    /// </summary>
    public double AmbianceMixDb { get; set; } = -15.0;

    /// <summary>
    /// When true, headless dub renders keep the intermediate timeline and mix files
    /// next to the delivery set instead of deleting them after export. Debug aid only.
    /// </summary>
    public bool KeepRenderArtifacts { get; set; } = false;

    /// <summary>
    /// When true, session artifacts (and headless delivery files) live in a
    /// <c>{filename}.babel</c> folder next to the source media (for example
    /// <c>clip.mp4.babel</c>). Falls back to
    /// app-local storage when that folder cannot be created.
    /// </summary>
    public bool StoreProjectsNextToMedia { get; set; } = true;

    /// <summary>UI theme: "Light", "Dark", or "System".</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Maximum entries kept in the recent-sessions list (1-20).</summary>
    public int MaxRecentSessions { get; set; } = 10;

    /// <summary>Whether to auto-save the session snapshot on app exit.</summary>
    public bool AutoSaveEnabled { get; set; } = true;

    /// <summary>Whether to show both original and translated text in the subtitle track.</summary>
    public bool BilingualSubtitlesEnabled { get; set; } = false;

    /// <summary>When false, the app may show a one-time notice about managed GPU host warm-up time.</summary>
    public bool ShownManagedBackendWarmupNotice { get; set; }

    private string _appLanguage = "auto";

    /// <summary>
    /// UI language.  <c>"auto"</c> means "follow the OS locale on each launch" (mirrors the
    /// <see cref="Theme"/> <c>"System"</c> sentinel); otherwise a lowercase ISO 639-1 code
    /// supported by the app's localization catalog (e.g. <c>"de"</c>, <c>"ja"</c>).
    /// Values are normalized to lowercase on set; unknown values are replaced with "auto".
    /// </summary>
    public string AppLanguage
    {
        get => _appLanguage;
        set
        {
            var normalized = value?.Trim().ToLowerInvariant() ?? "auto";
            if (string.Equals(normalized, "auto", StringComparison.OrdinalIgnoreCase))
            {
                _appLanguage = "auto";
            }
            else if (LocalizationService.SupportedUiLanguages.Contains(normalized))
            {
                _appLanguage = normalized;
            }
            else
            {
                _appLanguage = "auto";
            }
        }
    }
}