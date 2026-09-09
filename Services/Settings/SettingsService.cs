using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Babel.Player.Models;
using Babel.Player.Services;

namespace Babel.Player.Services.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> to a JSON file.
/// Never throws — missing or corrupt files fall back to defaults silently.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };

    private readonly string _filePath;
    private readonly AppLog _log;
    private readonly Lock _gate = new();

    public SettingsService(string filePath, AppLog log)
    {
        _filePath = filePath;
        _log = log;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
    }

    public string FilePath => _filePath;

    /// <summary>
    /// Returns saved settings, or a new <see cref="AppSettings"/> with defaults if the file
    /// is absent, empty, or unreadable.
    /// </summary>
    public AppSettings LoadOrDefault()
    {
        lock (_gate)
        {
            if (!File.Exists(_filePath))
            {
                var defaults = new AppSettings();
                defaults.NormalizeLegacyInferenceSettings();
                return defaults;
            }

            try
            {
                var json = File.ReadAllText(_filePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    RecoverUnreadableSettings("Settings file was empty or whitespace. Using defaults.");
                    var defaults = new AppSettings();
                    defaults.NormalizeLegacyInferenceSettings();
                    return defaults;
                }

                var file = JsonSerializer.Deserialize<AppSettingsFile>(json, SerializerOptions);
                if (file is null)
                {
                    RecoverUnreadableSettings("Settings file deserialized to null. Using defaults.");
                    var defaults = new AppSettings();
                    defaults.NormalizeLegacyInferenceSettings();
                    return defaults;
                }

                var settings = file.ToSettings();
                if (file.DubTimingMode == SegmentTimingMode.Pause)
                    _log.Info("Migrated legacy Pause session timing default to Off because Pause is preview-only.");
                settings.NormalizeLegacyInferenceSettings();
                return settings;
            }
            catch (JsonException ex)
            {
                RecoverUnreadableSettings("Settings JSON was invalid. Using defaults.", ex);
                var defaults = new AppSettings();
                defaults.NormalizeLegacyInferenceSettings();
                return defaults;
            }
            catch (Exception ex)
            {
                _log.Warning($"Settings load failed ({ex.Message}). Using defaults.");
                var defaults = new AppSettings();
                defaults.NormalizeLegacyInferenceSettings();
                return defaults;
            }
        }
    }

    /// <summary>Save settings. Failures are logged but non-fatal.</summary>
    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            try
            {
                settings.NormalizeLegacyInferenceSettings();
                var file = AppSettingsFile.FromSettings(settings);
                var json = JsonSerializer.Serialize(file, SerializerOptions);
                JsonStorePersistence.AtomicWriteText(_filePath, json);
            }
            catch (Exception ex)
            {
                _log.Error("Failed to save app settings.", ex);
            }
        }
    }

    private void RecoverUnreadableSettings(string statusMessage, Exception? ex = null)
    {
        if (ex is not null)
        {
            _log.Warning($"{statusMessage} {ex.Message}");
        }
        else
        {
            _log.Warning(statusMessage);
        }

        try
        {
            var backupPath = JsonStorePersistence.MoveUnreadableFileToBackup(_filePath);
            _log.Warning($"Unreadable settings file was moved to {backupPath}.");
        }
        catch (Exception moveEx)
        {
            _log.Error($"Failed to quarantine unreadable settings file '{_filePath}'.", moveEx);
        }
    }

    private sealed class AppSettingsFile
    {
        public string? TranscriptionProvider { get; set; }
        public ComputeProfile? TranscriptionProfile { get; set; }
        public InferenceRuntime? TranscriptionRuntime { get; set; }
        public string? TranscriptionModel { get; set; }
        public string? TranscriptionCpuComputeType { get; set; }
        public int? TranscriptionCpuThreads { get; set; }
        public bool? TranscriptionNumWorkersUseAuto { get; set; }
        public int? TranscriptionNumWorkers { get; set; }
        public string? DiarizationProvider { get; set; }
        public int? DiarizationMinSpeakers { get; set; }
        public int? DiarizationMaxSpeakers { get; set; }
        public bool? VocalSeparationEnabled { get; set; }
        public bool? KeepRenderArtifacts { get; set; }
        public bool? IsPipelinePaneVisible { get; set; }
        public bool? IsSegmentsPaneVisible { get; set; }
        public double? PipelinePaneWidth { get; set; }
        public double? SegmentsPaneWidth { get; set; }
        public bool? SwapPaneSides { get; set; }
        public bool? ShownManagedBackendWarmupNotice { get; set; }
        public string? TranslationProvider { get; set; }
        public ComputeProfile? TranslationProfile { get; set; }
        public InferenceRuntime? TranslationRuntime { get; set; }
        public string? TranslationModel { get; set; }
        public string? TtsProvider { get; set; }
        public ComputeProfile? TtsProfile { get; set; }
        public InferenceRuntime? TtsRuntime { get; set; }
        public string? TtsVoice { get; set; }
        public TtsVoiceAssignmentMode? TtsVoiceAssignmentMode { get; set; }
        public string? TranscriptionLanguageHint { get; set; }
        public string? TargetLanguage { get; set; }
        public string? PiperModelDir { get; set; }
        public string? ChatterboxModelDir { get; set; }
        public bool? ChatterboxVoiceCloneConsent { get; set; }
        public GpuHostBackend? PreferredLocalGpuBackend { get; set; }
        public bool? AlwaysStartLocalGpuRuntimeAtAppStart { get; set; }
        public string? AdvancedGpuServiceUrl { get; set; }

        // Legacy compatibility inputs.
        public string? ContainerizedServiceUrl { get; set; }
        public bool? AlwaysRunContainerAtAppStart { get; set; }

        public string? VideoHwdec { get; set; }
        public string? VideoGpuApi { get; set; }
        public bool? VideoUseGpuNext { get; set; }
        public bool? VideoVsrEnabled { get; set; }
        public VideoHdrPlaybackMode? VideoHdrPlaybackMode { get; set; }

        /// <summary>Legacy JSON; used only when migrating older settings files.</summary>
        public bool? VideoHdrEnabled { get; set; }

        /// <summary>Legacy JSON; used only when migrating older settings files.</summary>
        public bool? VideoPreferDriverAutoHdr { get; set; }

        public string? VideoToneMapping { get; set; }
        public string? VideoTargetPeak { get; set; }
        public bool? VideoHdrComputePeak { get; set; }
        public string? VideoExportEncoder { get; set; }
        public SegmentTimingMode? DubTimingMode { get; set; }
        public double? AmbianceMixDb { get; set; }
        public string? Theme { get; set; }
        public int? MaxRecentSessions { get; set; }
        public bool? AutoSaveEnabled { get; set; }
        public bool? StoreProjectsNextToMedia { get; set; }

        /// <summary>
        /// Produce an <see cref="AppSettings"/> populated from this file representation, applying legacy migrations and normalization.
        /// </summary>
        /// <returns>
        /// An <see cref="AppSettings"/> instance populated from the file's values; legacy settings are migrated and deprecated or compatibility-only fields are normalized or ignored.
        /// </returns>
        public AppSettings ToSettings()
        {
            var settings = new AppSettings();

            settings.TranscriptionProvider = TranscriptionProvider ?? settings.TranscriptionProvider;
            settings.TranscriptionProfile = ResolveProfile(
                TranscriptionProfile,
                TranscriptionRuntime,
                settings.TranscriptionProvider,
                InferenceRuntimeCatalog.InferTranscriptionProfile);
            settings.TranscriptionModel = TranscriptionModel ?? settings.TranscriptionModel;
            settings.TranscriptionCpuComputeType = TranscriptionCpuComputeType ?? settings.TranscriptionCpuComputeType;
            settings.TranscriptionCpuThreads = TranscriptionCpuThreads ?? settings.TranscriptionCpuThreads;
            settings.TranscriptionNumWorkersUseAuto = TranscriptionNumWorkersUseAuto ?? false;
            settings.TranscriptionNumWorkers = TranscriptionNumWorkers ?? settings.TranscriptionNumWorkers;

            settings.DiarizationProvider = DiarizationProvider ?? settings.DiarizationProvider;
            // Legacy diarization speaker bounds are ignored; providers auto-detect by default.
            settings.DiarizationMinSpeakers = null;
            settings.DiarizationMaxSpeakers = null;
            settings.VocalSeparationEnabled = VocalSeparationEnabled ?? settings.VocalSeparationEnabled;
            settings.KeepRenderArtifacts = KeepRenderArtifacts ?? settings.KeepRenderArtifacts;
            settings.IsPipelinePaneVisible = IsPipelinePaneVisible ?? settings.IsPipelinePaneVisible;
            settings.IsSegmentsPaneVisible = IsSegmentsPaneVisible ?? settings.IsSegmentsPaneVisible;
            settings.PipelinePaneWidth = NormalizePaneWidth(PipelinePaneWidth, settings.PipelinePaneWidth);
            settings.SegmentsPaneWidth = NormalizePaneWidth(SegmentsPaneWidth, settings.SegmentsPaneWidth);
            settings.SwapPaneSides = SwapPaneSides ?? settings.SwapPaneSides;
            settings.ShownManagedBackendWarmupNotice =
                ShownManagedBackendWarmupNotice ?? settings.ShownManagedBackendWarmupNotice;

            settings.TranslationProvider = TranslationProvider ?? settings.TranslationProvider;
            settings.TranslationProfile = ResolveProfile(
                TranslationProfile,
                TranslationRuntime,
                settings.TranslationProvider,
                InferenceRuntimeCatalog.InferTranslationProfile);
            settings.TranslationModel = TranslationModel ?? settings.TranslationModel;

            settings.TtsProvider = TtsProvider ?? settings.TtsProvider;
            settings.TtsProfile = ResolveProfile(
                TtsProfile,
                TtsRuntime,
                settings.TtsProvider,
                InferenceRuntimeCatalog.InferTtsProfile);
            settings.TtsVoice = TtsVoice ?? settings.TtsVoice;
            if (TtsVoiceAssignmentMode.HasValue)
                settings.TtsVoiceAssignmentMode = TtsVoiceAssignmentMode.Value;

            if (TranscriptionLanguageHint is not null)
                settings.TranscriptionLanguageHint = string.IsNullOrWhiteSpace(TranscriptionLanguageHint)
                    ? null
                    : TranscriptionLanguageHint;
            settings.TargetLanguage = TargetLanguage ?? settings.TargetLanguage;
            settings.PiperModelDir = PiperModelDir ?? settings.PiperModelDir;
            settings.ChatterboxModelDir = ChatterboxModelDir ?? settings.ChatterboxModelDir;
            settings.ChatterboxVoiceCloneConsent = ChatterboxVoiceCloneConsent ?? settings.ChatterboxVoiceCloneConsent;

            settings.PreferredLocalGpuBackend = PreferredLocalGpuBackend
                ?? ResolveLegacyGpuBackend();
            settings.AlwaysStartLocalGpuRuntimeAtAppStart =
                AlwaysStartLocalGpuRuntimeAtAppStart
                ?? AlwaysRunContainerAtAppStart
                ?? settings.AlwaysStartLocalGpuRuntimeAtAppStart;
            settings.AdvancedGpuServiceUrl =
                AdvancedGpuServiceUrl
                ?? ContainerizedServiceUrl
                ?? settings.AdvancedGpuServiceUrl;

            settings.VideoHwdec = VideoHwdec ?? settings.VideoHwdec;
            settings.VideoGpuApi = VideoGpuApi ?? settings.VideoGpuApi;
            settings.VideoUseGpuNext = VideoUseGpuNext ?? settings.VideoUseGpuNext;
            settings.VideoVsrEnabled = VideoVsrEnabled ?? settings.VideoVsrEnabled;
            if (VideoHdrPlaybackMode.HasValue)
                settings.VideoHdrPlaybackMode = VideoHdrPlaybackMode.Value;
            else if (VideoHdrEnabled == true)
                settings.VideoHdrPlaybackMode = VideoPreferDriverAutoHdr != false
                    ? Babel.Player.Models.VideoHdrPlaybackMode.NvidiaDriverRtxHdr
                    : Babel.Player.Models.VideoHdrPlaybackMode.MpvHdrPassthrough;
            settings.VideoToneMapping = VideoToneMapping ?? settings.VideoToneMapping;
            settings.VideoTargetPeak = VideoTargetPeak ?? settings.VideoTargetPeak;
            settings.VideoHdrComputePeak = VideoHdrComputePeak ?? settings.VideoHdrComputePeak;
            settings.VideoExportEncoder = VideoExportEncoder ?? settings.VideoExportEncoder;
            if (DubTimingMode.HasValue)
            {
                settings.DubTimingMode = DubTimingDefaults.NormalizeRenderTimingMode(DubTimingMode.Value);
            }
            if (AmbianceMixDb.HasValue)
                settings.AmbianceMixDb = AmbianceMixDb.Value;
            settings.Theme = Theme ?? settings.Theme;
            settings.MaxRecentSessions = MaxRecentSessions ?? settings.MaxRecentSessions;
            settings.AutoSaveEnabled = AutoSaveEnabled ?? settings.AutoSaveEnabled;
            settings.StoreProjectsNextToMedia = StoreProjectsNextToMedia ?? settings.StoreProjectsNextToMedia;

            return settings;
        }

        /// <summary>
        /// Creates an AppSettingsFile representation of the given runtime settings for JSON persistence.
        /// </summary>
        /// <param name="settings">The runtime AppSettings to convert into the persisted JSON model.</param>
        /// <returns>An AppSettingsFile populated from the provided settings suitable for serialization; diarization min/max speaker bounds are set to null for compatibility.</returns>
        public static AppSettingsFile FromSettings(AppSettings settings) => new()
        {
            TranscriptionProvider = settings.TranscriptionProvider,
            TranscriptionProfile = settings.TranscriptionProfile,
            TranscriptionModel = settings.TranscriptionModel,
            TranscriptionCpuComputeType = settings.TranscriptionCpuComputeType,
            TranscriptionCpuThreads = settings.TranscriptionCpuThreads,
            TranscriptionNumWorkersUseAuto = settings.TranscriptionNumWorkersUseAuto,
            TranscriptionNumWorkers = settings.TranscriptionNumWorkers,
            TranscriptionLanguageHint = settings.TranscriptionLanguageHint,
            DiarizationProvider = settings.DiarizationProvider,
            DiarizationMinSpeakers = null,
            DiarizationMaxSpeakers = null,
            VocalSeparationEnabled = settings.VocalSeparationEnabled,
            KeepRenderArtifacts = settings.KeepRenderArtifacts,
            IsPipelinePaneVisible = settings.IsPipelinePaneVisible,
            IsSegmentsPaneVisible = settings.IsSegmentsPaneVisible,
            PipelinePaneWidth = NormalizePaneWidth(settings.PipelinePaneWidth, AppSettings.PipelinePaneDefaultWidth),
            SegmentsPaneWidth = NormalizePaneWidth(settings.SegmentsPaneWidth, AppSettings.SegmentsPaneDefaultWidth),
            SwapPaneSides = settings.SwapPaneSides,
            ShownManagedBackendWarmupNotice = settings.ShownManagedBackendWarmupNotice,
            TranslationProvider = settings.TranslationProvider,
            TranslationProfile = settings.TranslationProfile,
            TranslationModel = settings.TranslationModel,
            TtsProvider = settings.TtsProvider,
            TtsProfile = settings.TtsProfile,
            TtsVoice = settings.TtsVoice,
            TtsVoiceAssignmentMode = settings.TtsVoiceAssignmentMode,
            TargetLanguage = settings.TargetLanguage,
            PiperModelDir = settings.PiperModelDir,
            ChatterboxModelDir = settings.ChatterboxModelDir,
            ChatterboxVoiceCloneConsent = settings.ChatterboxVoiceCloneConsent,
            PreferredLocalGpuBackend = settings.PreferredLocalGpuBackend,
            AlwaysStartLocalGpuRuntimeAtAppStart = settings.AlwaysStartLocalGpuRuntimeAtAppStart,
            AdvancedGpuServiceUrl = settings.AdvancedGpuServiceUrl,
            VideoHwdec = settings.VideoHwdec,
            VideoGpuApi = settings.VideoGpuApi,
            VideoUseGpuNext = settings.VideoUseGpuNext,
            VideoVsrEnabled = settings.VideoVsrEnabled,
            VideoHdrPlaybackMode = settings.VideoHdrPlaybackMode,
            VideoToneMapping = settings.VideoToneMapping,
            VideoTargetPeak = settings.VideoTargetPeak,
            VideoHdrComputePeak = settings.VideoHdrComputePeak,
            VideoExportEncoder = settings.VideoExportEncoder,
            DubTimingMode = DubTimingDefaults.NormalizeRenderTimingMode(settings.DubTimingMode),
            AmbianceMixDb = settings.AmbianceMixDb,
            Theme = settings.Theme,
            MaxRecentSessions = settings.MaxRecentSessions,
            AutoSaveEnabled = settings.AutoSaveEnabled,
            StoreProjectsNextToMedia = settings.StoreProjectsNextToMedia,
        };

        private ComputeProfile ResolveProfile(
            ComputeProfile? profile,
            InferenceRuntime? legacyRuntime,
            string? providerId,
            Func<string?, ComputeProfile> inferProfile)
        {
            if (profile.HasValue)
                return profile.Value;

            if (legacyRuntime.HasValue)
                return InferenceRuntimeCatalog.MapLegacyRuntimeToProfile(legacyRuntime.Value);

            return inferProfile(providerId);
        }

        private GpuHostBackend ResolveLegacyGpuBackend()
        {
            if (PreferredLocalGpuBackend.HasValue)
                return PreferredLocalGpuBackend.Value;

            if (TranscriptionRuntime == InferenceRuntime.Containerized
                || TranslationRuntime == InferenceRuntime.Containerized
                || TtsRuntime == InferenceRuntime.Containerized
                || AlwaysRunContainerAtAppStart == true
                || !string.IsNullOrWhiteSpace(ContainerizedServiceUrl))
            {
                return GpuHostBackend.DockerHost;
            }

            return GpuHostBackend.ManagedVenv;
        }

        private static double NormalizePaneWidth(double? value, double fallback)
        {
            if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value) || value.Value <= 0)
                return fallback;

            return value.Value;
        }
    }
}
