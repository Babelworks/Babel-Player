using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Subjects;
using Babel.Player.Models;
using Babel.Player.Services.Orchestration;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Planning;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CoordinatorCoreServices = Babel.Player.Models.CoordinatorCoreServices;
using CoordinatorOptions = Babel.Player.Models.CoordinatorOptions;

namespace Babel.Player.Services;

public sealed partial class SessionWorkflowCoordinator : ObservableObject, IDisposable
{
    private readonly SessionSnapshotStore _store;
    private readonly AppLog _log;
    private readonly PerSessionSnapshotStore _perSessionStore;
    private readonly RecentSessionsStore _recentStore;
    private readonly SessionArtifactReader _artifactReader;
    private readonly SessionSwitchService _sessionSwitchService;
    private readonly string? _projectDirectoryOverride;
    private readonly ContainerizedServiceProbe? _containerizedProbe;
    private readonly IContainerizedInferenceManager? _containerizedInferenceManager;
    private readonly ManagedCpuRuntimeManager _cpuRuntimeManager;
    public ITranscriptionRegistry TranscriptionRegistry { get; }
    public ITranslationRegistry TranslationRegistry { get; }
    public ITtsRegistry TtsRegistry { get; }
    public IDiarizationRegistry? DiarizationRegistry { get; private set; }
    private ITranscriptionProvider? _transcriptionService;
    private ITranslationProvider? _translationService;
    private ITtsProvider? _ttsService;
    private IVocalSeparationProvider? _vocalSeparationProvider;
    private readonly ProviderLeaseManager<ITranslationProvider> _translationProviderLeases;
    private readonly ProviderLeaseManager<ITtsProvider> _ttsProviderLeases;
    private readonly ContainerizedRequestLeaseTracker? _requestLeaseTracker;
    private readonly List<Task> _pendingTtsTasks = [];
    private readonly object _pendingTtsTasksLock = new();
    private readonly IAudioProcessingService? _audioProcessingService;
    private readonly object _sessionLock = new();
    private long _settingsRevision;
    private long _sessionRevision;


    private readonly IInferenceExecutionEngine _inferenceEngine;
    private readonly IExecutionPlanner _executionPlanner;
    private readonly TranscriptionOrchestrator _transcriptionOrchestrator;
    private readonly TranslationOrchestrator _translationOrchestrator;
    private readonly DiarizationStageOrchestrator _diarizationStageOrchestrator;
    private readonly TtsPipelineOrchestrator _ttsPipelineOrchestrator;
    private readonly StreamingPipelineOrchestrator _streamingPipelineOrchestrator;
    private readonly IMediaTransportManager _transportManager;
    private bool _subscribedToSegmentEvents;
    private bool _subscribedToSourceDiagnostics;
    private readonly EventHandler _segmentEndedHandler;
    private readonly EventHandler<Exception> _segmentErrorHandler;
    /// <summary>When pause-mode TTS is waiting on segment Ended, <see cref="StopTtsPlayback"/> completes this so the wait does not hang.</summary>
    private TaskCompletionSource<bool>? _ttsPauseModeCompletion;
    private readonly Action<VsrDiagnosticSnapshot> _vsrDiagnosticChangedHandler;
    private VsrDiagnosticSnapshot? _latestVsrDiagnostic;
    private readonly ConcurrentDictionary<string, WorkflowSessionSnapshot> _mediaSnapshotCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Subject<ReadinessSignal> _readinessSignals = new();
    private readonly ConcurrentDictionary<string, ContainerizedProbeState> _lastProbeStates =
        new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty]
    private WorkflowSessionSnapshot _currentSession = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow);

    [ObservableProperty]
    private string _sessionSource = "Session not initialized.";

    [ObservableProperty]
    private string _persistenceStatus = "Persistence has not run yet.";

    [ObservableProperty]
    private string? _activeTtsSegmentId;

    [ObservableProperty]
    private PlaybackState _playbackState;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecentSessions))]
    private IReadOnlyList<RecentSessionEntry> _recentSessions = [];

    [ObservableProperty]
    private BootstrapDiagnostics _bootstrapDiagnostics = new(false, null, false, null, false, null, false, false, null, null, "Detecting...");

    [ObservableProperty]
    private HardwareSnapshot _hardwareSnapshot = HardwareSnapshot.Detecting;

    private VideoEnhancementDiagnostics _videoEnhancementDiagnostics = VideoEnhancementDiagnostics.Initial;

    [ObservableProperty]
    private InferenceMode _inferenceMode = InferenceMode.SubprocessCpu;

    [ObservableProperty]
    private MediaReloadRequest? _pendingMediaReloadRequest;

    [ObservableProperty]
    private double _ttsPlaybackRate = 1.0;

    [ObservableProperty]
    private double _ttsVolume = 1.0;

    [ObservableProperty]
    private string? _runtimeWarmupStatusText;

    [ObservableProperty]
    private DateTimeOffset _readinessLastUpdatedUtc;

    [ObservableProperty]
    private ReadinessSignal? _lastReadinessSignal;

    /// <summary>
    /// Set when the CTranslate2 translation provider fails and the pipeline
    /// automatically falls back to the NLLB PyTorch provider.
    /// Null when no fallback has occurred. Exposed so the UI can show a note
    /// in the Active Config panel (e.g. "NMT: CTranslate2 → NLLB fallback").
    /// </summary>
    [ObservableProperty]
    private string? _translationFallbackNote;

    public bool HasRecentSessions => RecentSessions.Count > 0;

    public AppSettings CurrentSettings { get; private set; }

    internal AppLog Log => _log;

    /// <summary>UTC time when this coordinator instance was created — used for cold-start UX (warm-up hints).</summary>
    public DateTimeOffset ProcessStartedAtUtc { get; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Raised when AppSettings are modified in-place (e.g. by left-panel dropdowns).
    /// Subscribers should call SettingsService.Save() in response.
    /// </summary>
    public event Action? SettingsModified;
    public IObservable<ReadinessSignal> ReadinessSignals => _readinessSignals;

    public ApiKeyStore? KeyStore { get; private set; }

    /// <summary>
    /// Initializes a new <see cref="SessionWorkflowCoordinator"/> with the provided core services, transport manager, and registries, and prepares internal orchestration, runtime, and probe wiring required to manage the session workflow.
    /// </summary>
    /// <param name="coreServices">Core application services and stores required by the coordinator (settings, persistence store, logging).</param>
    /// <param name="transportManager">Media transport manager used for playback and segment transport.</param>
    /// <param name="registries">Provider registries and per-session/recent session stores used to resolve transcription, translation, TTS, and persistence backends.</param>
    /// <param name="options">Optional coordinator extensions and test hooks (container probe/manager, audio processing, artifact reader, session switch service, key store, diarization registry). When null, sensible defaults are applied.</param>
    public SessionWorkflowCoordinator(
        CoordinatorCoreServices coreServices,
        IMediaTransportManager transportManager,
        RegistryBundle registries,
        CoordinatorOptions? options = null)
    {
        options ??= CoordinatorOptions.Empty;

        _store = coreServices.Store;
        _log = coreServices.Log;
        _perSessionStore = registries.PerSessionStore;
        _recentStore = registries.RecentStore;
        _containerizedProbe = options.ContainerizedProbe;
        _containerizedInferenceManager = options.ContainerizedInferenceManager;
        _audioProcessingService = options.AudioProcessingService;
        _artifactReader = options.ArtifactReader ?? new SessionArtifactReader();
        _sessionSwitchService = options.SessionSwitchService
            ?? new SessionSwitchService(registries.PerSessionStore, registries.RecentStore, _log);
        _projectDirectoryOverride = NormalizeProjectDirectory(options.ProjectDirectory);

        _cpuRuntimeManager = new ManagedCpuRuntimeManager(_log);
        TranscriptionRegistry = registries.TranscriptionRegistry;
        TranslationRegistry = registries.TranslationRegistry;
        TtsRegistry = registries.TtsRegistry;
        DiarizationRegistry = options.DiarizationRegistry;
        CurrentSettings = coreServices.Settings;
        KeyStore = options.KeyStore;
        _transportManager = transportManager;
        _inferenceEngine = options.InferenceExecutionEngine ?? DefaultInferenceExecutionEngine.Instance;
        _executionPlanner = options.ExecutionPlanner ?? DefaultExecutionPlanner.Instance;
        _requestLeaseTracker = options.RequestLeaseTracker;
        _translationProviderLeases = new ProviderLeaseManager<ITranslationProvider>(_log, "translation");
        _ttsProviderLeases = new ProviderLeaseManager<ITtsProvider>(_log, "tts");
        _transcriptionOrchestrator = new TranscriptionOrchestrator(this, this, this, this, _inferenceEngine, _log);
        _translationOrchestrator = new TranslationOrchestrator(this, this, this, this, _inferenceEngine, _log);
        _diarizationStageOrchestrator = new DiarizationStageOrchestrator(this, this, this, _log);
        _ttsPipelineOrchestrator = new TtsPipelineOrchestrator(this);
        _streamingPipelineOrchestrator = new StreamingPipelineOrchestrator(this);

        _segmentEndedHandler = OnSegmentPlayerEnded;
        _segmentErrorHandler = (_, _) => OnSegmentPlayerError();
        _vsrDiagnosticChangedHandler = RecordVsrDiagnosticSnapshot;
        if (_containerizedProbe is not null)
            _containerizedProbe.ProbeResultUpdated += OnProbeResultUpdated;

        RefreshVideoEnhancementDiagnostics();
    }

    /// <summary>
    /// Creates a <see cref="SessionWorkflowCoordinator"/> without a pre-built transport manager,
    /// constructing a default <see cref="MediaTransportManager"/> from optional segment/source players.
    /// Convenience overload for tests and minimal-host scenarios.
    /// </summary>
    public SessionWorkflowCoordinator(
        CoordinatorCoreServices coreServices,
        RegistryBundle registries,
        CoordinatorOptions? options = null,
        IMediaTransport? segmentPlayer = null,
        IMediaTransport? sourcePlayer = null)
        : this(
            coreServices,
            new MediaTransportManager(
                segmentPlayer,
                sourcePlayer,
                videoOptionsFactory: () => new VideoPlaybackOptions(
                    HwdecMode:           coreServices.Settings.VideoHwdec,
                    GpuApi:              coreServices.Settings.VideoGpuApi,
                    UseGpuNext:          coreServices.Settings.VideoUseGpuNext,
                    VsrEnabled:          coreServices.Settings.VideoVsrEnabled,
                    HdrPlaybackMode:     coreServices.Settings.VideoHdrPlaybackMode,
                    AllowHdrPassthrough: coreServices.Settings.VideoHdrPlaybackMode != VideoHdrPlaybackMode.Off
                        && HardwareSnapshot.QueryActiveHdrDisplay(),
                    ToneMapping:         coreServices.Settings.VideoToneMapping,
                    TargetPeak:          coreServices.Settings.VideoTargetPeak,
                    HdrComputePeak:      coreServices.Settings.VideoHdrComputePeak),
                log: coreServices.Log),
            registries,
            options)
    {
    }

    public string StateFilePath => _store.StateFilePath;

    /// <summary>
    /// Handles the segment-player "ended" event by either completing a pending TTS pause wait or stopping TTS playback.
    /// </summary>
    /// <remarks>
    /// Expected state on entry: invoked when a media segment playback finishes. If a pause-mode TTS await is active (represented by <c>_ttsPauseModeCompletion</c>), this method completes that <c>TaskCompletionSource&lt;bool&gt;</c> with <c>true</c>, causing awaiting code to resume. If no pause-mode await is active, the method stops TTS playback by calling <c>StopTtsPlayback()</c>. This method does not persist session state and completes synchronously. It does not throw on normal operation.
    /// </remarks>
    private void OnSegmentPlayerEnded(object? sender, EventArgs e)
    {
        if (_ttsPauseModeCompletion is null)
        {
            StopTtsPlayback();
            return;
        }

        _ttsPauseModeCompletion.TrySetResult(true);
    }

    /// <summary>
    /// Handles a playback error for the currently playing segment by either stopping TTS playback or signaling a pending TTS pause-mode wait with failure.
    /// </summary>
    /// <remarks>
    /// If a pause-mode TTS wait is active (indicated by <c>_ttsPauseModeCompletion</c>), the method completes that <see cref="TaskCompletionSource{bool}"/> with <c>false</c> to indicate an error. If no pause-mode wait is active, the method stops TTS playback via <see cref="StopTtsPlayback()"/>.
    /// This method is an event handler invoked when a segment player reports an error; it does not persist session state.
    /// </remarks>
    private void OnSegmentPlayerError()
    {
        if (_ttsPauseModeCompletion is null)
        {
            StopTtsPlayback();
            return;
        }

        _ttsPauseModeCompletion.TrySetResult(false);
    }

    public string LogFilePath => _log.LogFilePath;
    internal ContainerizedServiceProbe? ContainerizedProbe => _containerizedProbe;
    public IContainerizedInferenceManager? ContainerizedInferenceManager => _containerizedInferenceManager;
    internal VideoEnhancementDiagnostics VideoEnhancementDiagnostics
    {
        get => _videoEnhancementDiagnostics;
        private set => SetProperty(ref _videoEnhancementDiagnostics, value);
    }

    /// <summary>
    /// Bootstraps coordinator state by loading a persisted session snapshot or creating a new foundation session when none exists.
    /// </summary>
    /// <remarks>
    /// On entry: may be called at application startup; no specific pipeline stage is required.
    /// On success: restores CurrentSession to the persisted snapshot (possibly with its stage downgraded if artifacts are missing) or sets a newly created foundation session; updates persistence-related properties and recent-session list.
    /// Side effects: caches the session snapshot for the media key when applicable, enqueues a media reload request if the restored session is at or past MediaLoaded, and persists the current session to disk.
    /// Cancellation: this method is synchronous and does not support cancellation.
    /// </remarks>
    public void Initialize()
    {
        // Heavy bootstrap probes and per-session snapshot preloading are warmed in background.
        BootstrapDiagnostics = new BootstrapDiagnostics(false, null, false, null, false, null, false, false, null, null, "Detecting...");

        var nowUtc = DateTimeOffset.UtcNow;
        var loadResult = _store.Load();

        if (loadResult.Snapshot is null)
        {
            lock (_sessionLock)
            {
                CurrentSession = WorkflowSessionSnapshot.CreateNew(nowUtc);
            }
            SessionSource = "Created a new foundation session.";
        }
        else
        {
            var snapshot = loadResult.Snapshot;
            var validation = SessionSnapshotSemantics.ValidateArtifacts(snapshot);
            var validated = validation.Snapshot;

            // Log any artifacts that were dropped by validation
            if (snapshot.Stage != validated.Stage)
                _log.Warning($"Session stage downgraded on load: {snapshot.Stage} → {validated.Stage} (missing artifacts)");
            if (validation.OriginalStage != validated.Stage)
            {
                _log.Warning(
                    $"ValidateArtifacts[startup-load]: downgraded stage {validation.OriginalStage} -> {validated.Stage}; " +
                    $"cleared={string.Join(",", validation.ClearedArtifacts)}; provenance={SessionSnapshotSemantics.DescribeSessionProvenance(validated)}");
            }

            const string statusMessage = "Ready.";

            lock (_sessionLock)
            {
                CurrentSession = validated with
                {
                    LastUpdatedAtUtc = nowUtc,
                    StatusMessage = statusMessage,
                };
            }

            // Primary current-session.json is authoritative — overwrite per-session cache entry.
            if (!string.IsNullOrEmpty(CurrentSession.SourceMediaPath))
                CacheMediaSnapshot(MediaKey(CurrentSession.SourceMediaPath), CurrentSession);

            SessionSource = validated.Stage != snapshot.Stage
                ? $"Resumed session (stage downgraded from {snapshot.Stage} to {validated.Stage}: missing artifacts)."
                : validated.Stage >= SessionWorkflowStage.TtsGenerated
                ? "Resumed session with TTS."
                : validated.Stage >= SessionWorkflowStage.Translated
                    ? "Resumed session with translation."
                    : validated.Stage >= SessionWorkflowStage.Diarized
                        ? "Resumed session with speaker mapping."
                        : validated.Stage >= SessionWorkflowStage.Transcribed
                            ? "Resumed session with transcript."
                            : "Resumed the saved foundation session.";
        }

        PersistenceStatus = loadResult.StatusMessage;
        RecentSessions = _recentStore.Load();
        _log.Info(SessionSource);
        if (CurrentSession.Stage >= SessionWorkflowStage.MediaLoaded)
            QueueMediaReloadRequest(autoPlay: false, "initialize");
        SaveCurrentSession();
    }

    public BootstrapWarmupData GatherBootstrapWarmupData()
    {
        var diagnostics = BootstrapDiagnostics.Run(CurrentSettings.EffectiveGpuServiceUrl);
        var snapshots = _perSessionStore.LoadAll();
        var inferenceMode = ResolveInferenceMode(diagnostics);
        return new BootstrapWarmupData(diagnostics, snapshots, inferenceMode);
    }

    public void ApplyBootstrapWarmupData(BootstrapWarmupData warmup)
    {
        BootstrapDiagnostics = warmup.Diagnostics;
        InferenceMode = warmup.ResolvedInferenceMode;
        EmitReadinessSignal(
            ReadinessSignalKind.BootstrapApplied,
            summary: "Bootstrap diagnostics updated.",
            source: nameof(ApplyBootstrapWarmupData),
            forceRefresh: true);

        if (!BootstrapDiagnostics.AllDependenciesAvailable)
            _log.Warning($"Bootstrap: {BootstrapDiagnostics.DiagnosticSummary}");
        else
            _log.Info("Bootstrap: all dependencies available.");

        _log.Info($"Bootstrap: inference mode = {InferenceMode} ({BootstrapDiagnostics.InferenceLine})");

        foreach (var snapshot in warmup.Snapshots)
        {
            if (!string.IsNullOrEmpty(snapshot.SourceMediaPath))
                CacheMediaSnapshot(MediaKey(snapshot.SourceMediaPath), snapshot);
        }
    }

    private static InferenceMode ResolveInferenceMode(BootstrapDiagnostics diagnostics)
    {
        if (diagnostics.ContainerizedServiceAvailable)
        {
            return string.Equals(
                diagnostics.ContainerizedServiceUrl,
                AppSettings.ManagedGpuServiceUrl,
                StringComparison.OrdinalIgnoreCase)
                ? InferenceMode.ManagedVenv
                : InferenceMode.Containerized;
        }

        return InferenceMode.SubprocessCpu;
    }

    /// <summary>
    /// Loads the specified media file into the coordinator, restoring a cached session if available or creating a new session for the media.
    /// </summary>
    /// <param name="sourceMediaPath">Absolute or relative path to the source media file to ingest.</param>
    /// <remarks>
    /// Entry state: may be called at any time; if a current session has a SourceMediaPath, the coordinator treats this as a media switch.
    /// Exit state: on success the coordinator's CurrentSession will be at least <see cref="SessionWorkflowStage.MediaLoaded"/> and its media/artifact paths and status message will reflect either the restored snapshot or a newly created session; a media reload request is queued.
    /// Persistence: when switching media the existing session is stashed into the MRU/per-session store and the new/restored session is persisted (FlushPendingSave is invoked) so state survives restarts.
    /// Behavior: if a previously cached snapshot exists for the supplied media the method validates and restores that snapshot (copying the media into the snapshot's session directory) and retains restored artifact paths; otherwise it creates a new per-session directory, copies the media there, resets downstream artifacts, and sets a fresh SessionId when switching media.
    /// Cancellation: this method is synchronous and does not support cancellation.
    /// </remarks>
    /// <exception cref="FileNotFoundException">Thrown when the file at <paramref name="sourceMediaPath"/> does not exist.</exception>
    public void LoadMedia(string sourceMediaPath)
    {
        if (!File.Exists(sourceMediaPath))
            throw new FileNotFoundException($"Source media file not found: {sourceMediaPath}");

        var nowUtc = DateTimeOffset.UtcNow;

        // Stash current snapshot before switching — persist to disk so it survives restart.
        if (!string.IsNullOrEmpty(CurrentSession.SourceMediaPath))
        {
            RecentSessions = _sessionSwitchService.StashCurrentSession(
                CurrentSession,
                _mediaSnapshotCache,
                MediaSnapshotCacheLimit);
        }

        var newKey = MediaKey(sourceMediaPath);
        var switchingMedia = !string.IsNullOrEmpty(CurrentSession.SourceMediaPath)
            && !string.Equals(MediaKey(CurrentSession.SourceMediaPath), newKey,
                              StringComparison.OrdinalIgnoreCase);

        var cached = switchingMedia
            ? _sessionSwitchService.LoadSessionForMedia(sourceMediaPath, _mediaSnapshotCache)
            : null;
        if (cached is not null)
        {
            // Returning to a previously processed media — restore, validate, then copy into
            // that session's existing directory.
            var validation = SessionSnapshotSemantics.ValidateArtifacts(cached);
            var validated = validation.Snapshot;
            if (validation.OriginalStage != validated.Stage)
            {
                _log.Warning(
                    $"ValidateArtifacts[media-cache-restore]: downgraded stage {validation.OriginalStage} -> {validated.Stage}; " +
                    $"cleared={string.Join(",", validation.ClearedArtifacts)}; provenance={SessionSnapshotSemantics.DescribeSessionProvenance(validated)}");
            }

            var sessionDir = ResolveSessionDirectory(validated.SessionId, sourceMediaPath);
            var mediaDir = Path.Combine(sessionDir, "media");
            Directory.CreateDirectory(mediaDir);
            var ingestedPath = Path.Combine(mediaDir, Path.GetFileName(sourceMediaPath));
            File.Copy(sourceMediaPath, ingestedPath, overwrite: true);
            WriteMediaManifest(ingestedPath);
            _log.Debug($"Copied media to session artifact: {ingestedPath}");

            lock (_sessionLock)
            {
                CurrentSession = validated with
                {
                    IngestedMediaPath = ingestedPath,
                    VocalsAudioPath = validated.VocalsAudioPath,
                    AmbianceAudioPath = validated.AmbianceAudioPath,
                    MediaLoadedAtUtc = nowUtc,
                    LastUpdatedAtUtc = nowUtc,
                    StatusMessage = validated.Stage >= SessionWorkflowStage.TtsGenerated
                        ? "Restored prior TTS. Ready for playback."
                        : validated.Stage >= SessionWorkflowStage.Translated
                            ? "Restored translation. Ready for TTS/dubbing."
                        : validated.Stage >= SessionWorkflowStage.Diarized
                            ? "Restored speaker mapping state. Ready to resume translation/TTS."
                        : validated.Stage >= SessionWorkflowStage.Transcribed
                            ? "Restored transcript. Ready for translation."
                        : "Media loaded. Ready for transcription.",
                };
            }
            _log.Info($"Restored cached session for: {sourceMediaPath} (stage: {CurrentSession.Stage})");
        }
        else
        {
            // New uncached media — assign a fresh session ID so each media file gets its own
            // identity in the MRU list and per-session store. Re-use the current session ID
            // only when loading the first media (no prior source) so the coordinator's initial
            // session is promoted rather than orphaned.
            var newSessionId = switchingMedia ? Guid.NewGuid() : CurrentSession.SessionId;

            var sessionDir = ResolveSessionDirectory(newSessionId, sourceMediaPath);
            var mediaDir = Path.Combine(sessionDir, "media");
            Directory.CreateDirectory(mediaDir);
            var ingestedPath = Path.Combine(mediaDir, Path.GetFileName(sourceMediaPath));
            File.Copy(sourceMediaPath, ingestedPath, overwrite: true);
            WriteMediaManifest(ingestedPath);
            _log.Debug($"Copied media to session artifact: {ingestedPath}");

            lock (_sessionLock)
            {
                CurrentSession = CreateMediaLoadedSession(
                    newSessionId,
                    sourceMediaPath,
                    ingestedPath,
                    nowUtc);
            }
        }

        QueueMediaReloadRequest(autoPlay: false, "media-switch");
        FlushPendingSave();
    }

    /// <summary>
/// Produce the absolute filesystem path for a media file.
/// </summary>
/// <param name="path">A relative or absolute path to the media file.</param>
/// <returns>The absolute path corresponding to the provided path.</returns>
internal static string MediaKey(string path) => Path.GetFullPath(path);

    private const int MediaSnapshotCacheLimit = 20;

    /// <summary>
    /// Adds or updates a snapshot in the media cache, evicting the oldest entry
    /// when the cache exceeds <see cref="MediaSnapshotCacheLimit"/> to prevent unbounded growth.
    /// </summary>
    private void CacheMediaSnapshot(string key, WorkflowSessionSnapshot snapshot)
    {
        _sessionSwitchService.CacheCurrentSession(
            key,
            snapshot,
            _mediaSnapshotCache,
            MediaSnapshotCacheLimit);
    }

    public void InjectTestTranscript(string transcriptPath, string? translationPath = null)
    {
        lock (_sessionLock)
        {
            CurrentSession = CurrentSession with
            {
                Stage = translationPath != null ? SessionWorkflowStage.Translated : SessionWorkflowStage.Transcribed,
                TranscriptPath = transcriptPath,
                TranslationPath = translationPath,
                StatusMessage = translationPath != null
                    ? "Test transcript and translation injected."
                    : "Test transcript injected.",
            };
        }
        SaveCurrentSession();
    }

    /// <summary>
    /// Resets the session pipeline to the MediaLoaded stage and clears all downstream artifacts, provider/model selections, language settings, speaker metadata, and generated timestamps.
    /// </summary>
    /// <remarks>
    /// Entry condition: safe to call at any time; the method is a no-op if the current session's stage is already earlier than MediaLoaded.
    /// Exit state on success: <c>CurrentSession.Stage</c> is set to <see cref="SessionWorkflowStage.MediaLoaded"/> and all transcription/translation/diarization/TTS-related fields are cleared or nulled, with <c>StatusMessage</c> set to "Ready.".
    /// Persistence: this method updates the in-memory <c>CurrentSession</c> only and does not persist the session to storage.
    /// Cancellation: not applicable.
    /// </remarks>
    public void ResetPipelineToMediaLoaded()
    {
        if (CurrentSession.Stage < SessionWorkflowStage.MediaLoaded) return;

        lock (_sessionLock)
        {
            CurrentSession = ResetToMediaLoadedSession(CurrentSession);
        }
        MarkSessionInputsChanged("pipeline reset to media-loaded");
    }

    public void ResetPipelineToTranscribed()
    {
        if (CurrentSession.Stage < SessionWorkflowStage.Transcribed) return;
        
        lock (_sessionLock)
        {
            CurrentSession = CurrentSession with
            {
                Stage = SessionWorkflowStage.Transcribed,
                TranslationPath = null,
                TtsPath = null,
                MixedDubAudioPath = null,
                TtsVoice = null,
                TtsSegmentsPath = null,
                TtsSegmentAudioPaths = null,
                TtsSegmentDurations = null,
                SegmentTimingModeOverrides = null,
                TargetLanguage = null,
                TranslatedAtUtc = null,
                TtsGeneratedAtUtc = null,
                TranslationRuntime = null,
                TranslationProvider = null,
                TranslationModel = null,
                TtsRuntime = null,
                TtsProvider = null,
                DubTimingMode = null,
                AmbianceMixDb = null,
                SpeakerVoiceAssignments = null,
                SpeakerReferenceAudioPaths = null,
                DefaultTtsVoiceFallback = null,
                DiarizationProvider = null,
                SpeakersDetectedAtUtc = null,
                StatusMessage = "Reset to transcription."
            };
        }
        MarkSessionInputsChanged("pipeline reset to transcribed");
    }

    public void ResetPipelineToDiarized()
    {
        if (CurrentSession.Stage < SessionWorkflowStage.Diarized || !HasDiarizationMarker(CurrentSession))
            return;

        lock (_sessionLock)
        {
            CurrentSession = CurrentSession with
            {
                Stage = SessionWorkflowStage.Diarized,
                TranslationPath = null,
                TtsPath = null,
                MixedDubAudioPath = null,
                TtsVoice = null,
                TtsSegmentsPath = null,
                TtsSegmentAudioPaths = null,
                TtsSegmentDurations = null,
                SegmentTimingModeOverrides = null,
                TargetLanguage = null,
                TranslatedAtUtc = null,
                TtsGeneratedAtUtc = null,
                TranslationRuntime = null,
                TranslationProvider = null,
                TranslationModel = null,
                TtsRuntime = null,
                TtsProvider = null,
                DubTimingMode = null,
                AmbianceMixDb = null,
                StatusMessage = "Reset to speaker analysis."
            };
        }
        MarkSessionInputsChanged("pipeline reset to diarized");
    }

    public void ResetPipelineToTranslated()
    {
        if (CurrentSession.Stage < SessionWorkflowStage.Translated) return;
        
        lock (_sessionLock)
        {
            CurrentSession = CurrentSession with
            {
                Stage = SessionWorkflowStage.Translated,
                TtsPath = null,
                MixedDubAudioPath = null,
                TtsVoice = null,
                TtsSegmentsPath = null,
                TtsSegmentAudioPaths = null,
                TtsSegmentDurations = null,
                SegmentTimingModeOverrides = null,
                TtsGeneratedAtUtc = null,
                TtsRuntime = null,
                TtsProvider = null,
                DubTimingMode = null,
                AmbianceMixDb = null,
                StatusMessage = "Reset to translation."
            };
        }
        MarkSessionInputsChanged("pipeline reset to translated");
    }

    /// <summary>
    /// Clears translation and downstream artifacts so translation can be re-run, using the same rules as translation settings invalidation.
    /// </summary>
    public void ResetPipelineForTranslationRetry()
    {
        if (HasDiarizationMarker(CurrentSession))
            ResetPipelineToDiarized();
        else
            ResetPipelineToTranscribed();

        lock (_sessionLock)
        {
            CurrentSession = CurrentSession with { StatusMessage = "Ready." };
        }
        SaveCurrentSession();
    }

    public void ClearPipeline()
    {
        ResetPipelineToMediaLoaded();
        InvalidateAllProviderCaches();
        SaveCurrentSession();
    }

    /// <summary>
    /// Apply a new pipeline settings selection (transcription, translation, TTS providers/models/profiles and optional target language) and update pipeline state, provider instances, and persistence as needed.
    /// </summary>
    /// <param name="selection">The chosen provider/model/profile/voice and optional target language to apply to the pipeline.</param>
    /// <returns>
    /// A <see cref="PipelineSettingsApplyResult"/> describing which pipeline stage (if any) was invalidated, the resulting session stage, whether settings were applied, and a status message.
    /// </returns>
    /// <exception cref="ArgumentException">Thrown when any required field on <paramref name="selection"/> (transcription provider/model, translation provider/model, TTS provider/voice) is null, empty, or whitespace.</exception>
    public PipelineSettingsApplyResult ApplyPipelineSettings(PipelineSettingsSelection selection)
    {
        var stopwatch = Stopwatch.StartNew();
        if (!ApplyPipelineSelectionSettings(selection))
        {
            _log.Debug(
                $"ApplyPipelineSettings: no-op at stage {CurrentSession.Stage}; selection matched current settings.");
            return new PipelineSettingsApplyResult(
                PipelineInvalidation.None,
                CurrentSession.Stage,
                false,
                CurrentSession.StatusMessage);
        }

        var invalidation = CheckSettingsInvalidation();
        _log.Debug(
            $"ApplyPipelineSettings: stage={CurrentSession.Stage}, invalidation={invalidation}, " +
                $"selection=({selection.TranscriptionRuntime}/{selection.TranscriptionProvider}/{selection.TranscriptionModel}, " +
                $"{selection.TranslationRuntime}/{selection.TranslationProvider}/{selection.TranslationModel}, " +
            $"{selection.TtsRuntime}/{selection.TtsProvider}/{selection.TtsVoice}, target={selection.TargetLanguage ?? "<unchanged>"}, asrHint={selection.TranscriptionLanguageHint ?? "<auto>"}), " +
            $"provenance=({SessionSnapshotSemantics.DescribeSessionProvenance(CurrentSession)})");
        var statusMessage = invalidation switch
        {
            PipelineInvalidation.Transcription => "Transcription settings changed — pipeline reset to media-loaded state.",
            PipelineInvalidation.Translation => HasDiarizationMarker(CurrentSession)
                ? "Translation settings changed — pipeline reset to speaker-mapped state."
                : "Translation settings changed — pipeline reset to transcript state.",
            PipelineInvalidation.Tts => "TTS settings changed — pipeline reset to translation state.",
            _ => "Pipeline settings updated."
        };

        switch (invalidation)
        {
            case PipelineInvalidation.Transcription:
                ResetPipelineToMediaLoaded();
                lock (_sessionLock)
                {
                    CurrentSession = CurrentSession with { StatusMessage = statusMessage };
                }
                SaveCurrentSession();
                break;
            case PipelineInvalidation.Translation:
                if (HasDiarizationMarker(CurrentSession))
                    ResetPipelineToDiarized();
                else
                    ResetPipelineToTranscribed();
                lock (_sessionLock)
                {
                    CurrentSession = CurrentSession with { StatusMessage = statusMessage };
                }
                SaveCurrentSession();
                break;
            case PipelineInvalidation.Tts:
                ResetPipelineToTranslated();
                lock (_sessionLock)
                {
                    CurrentSession = CurrentSession with { StatusMessage = statusMessage };
                }
                SaveCurrentSession();
                break;
        }

        RequestContainerizedAutostartForSettings();
        NotifySettingsModified();
        stopwatch.Stop();
        _log.Debug(
            $"ApplyPipelineSettings complete: invalidation={invalidation}, stage={CurrentSession.Stage}, elapsedMs={stopwatch.ElapsedMilliseconds}");

        return new PipelineSettingsApplyResult(
            invalidation,
            CurrentSession.Stage,
            true,
            statusMessage);
    }

    /// <summary>
    /// Regenerates TTS audio for a single translated segment and updates the session's TTS segment paths.
    /// </summary>
    /// <param name="segmentId">The identifier of the translated segment to regenerate TTS for.</param>
    /// <remarks>
    /// Preconditions: <see cref="CurrentSession.TranslationPath"/> must be set and the translation file must exist; otherwise this method throws (<see cref="InvalidOperationException"/> or <see cref="FileNotFoundException"/>). The method ensures any required containerized runtime is started and checks provider readiness before generation; if the configured TTS provider is not ready for execution and a model download is not required, a <see cref="PipelineProviderException"/> is thrown. On success the session's <c>TtsSegmentAudioPaths</c> and <c>StatusMessage</c> are updated and the session is persisted via <see cref="SaveCurrentSession"/>. The operation supports cooperative cancellation through <paramref name="cancellationToken"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when no translation is available or the specified segment text is missing.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the translation file referenced by the session cannot be found.</exception>
    /// <exception cref="PipelineProviderException">Thrown when the configured TTS provider is not ready for execution and no model download is required.</exception>
    public async Task RegenerateSegmentTtsAsync(string segmentId, CancellationToken cancellationToken = default)
    {
        var translationPath = CurrentSession.TranslationPath;
        if (string.IsNullOrEmpty(translationPath))
        {
            throw new InvalidOperationException("No translation available. Please translate first.");
        }

        if (!File.Exists(translationPath))
        {
            throw new FileNotFoundException($"Translation file not found: {translationPath}");
        }

        var translation = await _artifactReader.LoadTranslationAsync(
            translationPath,
            cancellationToken);
        var targetSegment = translation.Segments?.FirstOrDefault(s => s.Id == segmentId);
        var segmentText = targetSegment?.TranslatedText;
        if (string.IsNullOrWhiteSpace(segmentText) || targetSegment is null)
            throw new InvalidOperationException($"Segment not found: {segmentId}");

        var defaultVoice = (CurrentSession.TtsVoice ?? CurrentSettings.TtsVoice)?.Trim();
        if (string.IsNullOrWhiteSpace(defaultVoice))
            throw new InvalidOperationException("No TTS voice configured. Please configure a voice in Settings before regenerating TTS.");

        var targetLanguage = CurrentSession.TargetLanguage ?? CurrentSettings.TargetLanguage;
        var stagePlan = ResolveAndApplyExecutionPlan(Planning.InferenceStage.Tts);
        var snapshot = await PrepareTtsExecutionSnapshotAsync(
                stagePlan,
                translationPath,
                defaultVoice,
                targetLanguage,
                progress: null,
                stageContext: null,
                cancellationToken)
            .ConfigureAwait(false);
        await using var ttsProviderLease = snapshot.ProviderLease;
        var segmentAudioPath = Path.Combine(snapshot.SegmentsDir, $"{segmentId}.mp3");
        var resolvedVoice = snapshot.ResolveVoiceForSegment(targetSegment);
        var referenceAudioPath = snapshot.ResolveReferenceAudioForSegment(targetSegment);

        _log.Info($"Regenerating TTS for segment {segmentId}: {segmentText[..Math.Min(30, segmentText.Length)]}...");

        var ttsTask = _inferenceEngine.GenerateSegmentTtsAsync(
            snapshot.Provider,
            new SingleSegmentTtsRequest(
                segmentText,
                segmentAudioPath,
                resolvedVoice,
                targetSegment?.SpeakerId,
                referenceAudioPath,
                Language: snapshot.Language,
                SourceVideoPath: snapshot.SourceVideoPath,
                TargetDurationSeconds: targetSegment is null ? null : Math.Max(0, targetSegment.End - targetSegment.Start),
                SourceStartSeconds: targetSegment?.Start,
                SourceEndSeconds: targetSegment?.End),
            cancellationToken);
        TrackPendingTtsTask(ttsTask);
        var result = await ttsTask;

        if (!result.Success)
        {
            var errorMsg = result.ErrorMessage ?? "Unknown TTS error";
            _log.Error($"Segment TTS regeneration failed: {errorMsg}", new Exception(errorMsg));
            throw new InvalidOperationException($"Segment TTS regeneration failed: {errorMsg}");
        }

        if (!snapshot.TranslationIdentity.Matches(CurrentSession.TranslationPath))
        {
            _log.Warning(
                $"Discarding segment TTS regeneration for {segmentId} from run {snapshot.RunId} because translation inputs changed.");
            return;
        }

        var currentTranslatedText = await _artifactReader.GetTranslatedTextAsync(
            CurrentSession.TranslationPath!,
            segmentId,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(currentTranslatedText, segmentText, StringComparison.Ordinal))
        {
            _log.Warning(
                $"Discarding segment TTS regeneration for {segmentId} from run {snapshot.RunId} because the translated text changed.");
            return;
        }

        var updatedSegmentPaths = CurrentSession.TtsSegmentAudioPaths is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [segmentId] = segmentAudioPath,
            }
            : new Dictionary<string, string>(CurrentSession.TtsSegmentAudioPaths, StringComparer.Ordinal)
            {
                [segmentId] = segmentAudioPath,
            };
        var updatedDurations = CurrentSession.TtsSegmentDurations is null
            ? new Dictionary<string, double>(StringComparer.Ordinal)
            : new Dictionary<string, double>(CurrentSession.TtsSegmentDurations, StringComparer.Ordinal);
        if (result.DurationSeconds.HasValue)
            updatedDurations[segmentId] = result.DurationSeconds.Value;

        lock (_sessionLock)
        {
            CurrentSession = SessionSnapshotSemantics.ClearTtsOutputs(CurrentSession) with
            {
                Stage = SessionWorkflowStage.Translated,
                TtsSegmentsPath = snapshot.SegmentsDir,
                TtsSegmentAudioPaths = updatedSegmentPaths,
                TtsSegmentDurations = updatedDurations.Count == 0 ? null : updatedDurations,
                TtsVoice = resolvedVoice,
                TtsRuntime = snapshot.Plan.Runtime,
                TtsProvider = snapshot.Plan.ProviderId,
                TtsSettingsDriftedSinceArtifact = TtsSettingsDrifted(snapshot),
                TtsRunId = snapshot.RunId,
                DubTimingMode = snapshot.DefaultTimingMode,
                AmbianceMixDb = snapshot.AmbianceMixDb,
                StatusMessage = TtsSettingsDrifted(snapshot)
                    ? $"Regenerated TTS for segment {segmentId} using previous settings. Re-render the dub to apply current settings."
                    : $"Regenerated TTS for segment {segmentId}. Re-render the dub to finalize it.",
            };
        }

        _log.Info($"Segment TTS regenerated: {segmentId} -> {segmentAudioPath}");
        SaveCurrentSession();
    }

    /// <summary>
    /// Regenerates the translated text for a single segment in the current session's translation artifact.
    /// </summary>
    /// <param name="segmentId">Identifier of the segment to regenerate (stable segment id produced by SegmentId).</param>
    /// <remarks>
    /// Preconditions: the session must have a translation artifact path (CurrentSession.TranslationPath) and that file must exist; source and target languages must be set on the session. The method ensures the translation execution runtime is ready before invoking translation. On success the session's status message is updated and the session is persisted via SaveCurrentSession; the session pipeline stage is not advanced by this operation. The operation supports cooperative cancellation through <paramref name="cancellationToken"/>.
    /// </remarks>
    /// <exception cref="FileNotFoundException">Thrown when the current session's translation file cannot be found on disk.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the session lacks a translation path, when the source or target language is not set, when the segment source text is not found, or when the translation attempt fails.</exception>
    public async Task RegenerateSegmentTranslationAsync(string segmentId, CancellationToken cancellationToken = default)
    {
        var translationPath = CurrentSession.TranslationPath;
        if (string.IsNullOrEmpty(translationPath))
        {
            throw new InvalidOperationException("No translation available. Please translate first.");
        }

        if (!File.Exists(translationPath))
        {
            throw new FileNotFoundException($"Translation file not found: {translationPath}");
        }

        var sourceText = await _artifactReader.GetSourceTextAsync(
            translationPath,
            segmentId,
            cancellationToken);

        if (string.IsNullOrEmpty(sourceText))
        {
            throw new InvalidOperationException($"Source text not found for segment: {segmentId}");
        }

        if (string.IsNullOrEmpty(CurrentSession.SourceLanguage))
            throw new InvalidOperationException("Source language is not set in the current session. Transcription must be completed first.");
            
        if (string.IsNullOrEmpty(CurrentSession.TargetLanguage))
            throw new InvalidOperationException("Target language is not set in the current session.");

        var sourceLanguage = CurrentSession.SourceLanguage;
        var targetLanguage = CurrentSession.TargetLanguage;
        if (string.IsNullOrWhiteSpace(CurrentSession.TranscriptPath) || !File.Exists(CurrentSession.TranscriptPath))
            throw new InvalidOperationException("Transcript is unavailable for translation regeneration.");
        var translationIdentity = ArtifactIdentity.Capture(translationPath);
        var stagePlan = ResolveAndApplyExecutionPlan(Planning.InferenceStage.Translation);
        var snapshot = await PrepareTranslationExecutionSnapshotAsync(
                stagePlan,
                CurrentSession.TranscriptPath,
                sourceLanguage,
                targetLanguage,
                progress: null,
                allowPendingTranscriptArtifact: false,
                cancellationToken)
            .ConfigureAwait(false);
        await using var translationProviderLease = snapshot.ProviderLease;

        _log.Info($"Regenerating translation for segment {segmentId}: {sourceText[..Math.Min(30, sourceText.Length)]}...");

        var result = await _inferenceEngine.TranslateSingleSegmentTextAsync(
            snapshot.Provider,
            new SingleSegmentTranslationTextRequest(
                sourceText,
                segmentId,
                sourceLanguage,
                targetLanguage,
                snapshot.Model),
            cancellationToken);

        if (!result.Success)
        {
            var errorMsg = result.ErrorMessage ?? "Unknown translation error";
            _log.Error($"Segment translation regeneration failed: {errorMsg}", new Exception(errorMsg));
            throw new InvalidOperationException($"Segment translation regeneration failed: {errorMsg}");
        }

        if (!translationIdentity.Matches(CurrentSession.TranslationPath))
        {
            _log.Warning(
                $"Discarding segment translation regeneration for {segmentId} from run {snapshot.RunId} because translation inputs changed.");
            return;
        }

        var currentSourceText = await _artifactReader.GetSourceTextAsync(
            CurrentSession.TranslationPath!,
            segmentId,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(currentSourceText, sourceText, StringComparison.Ordinal))
        {
            _log.Warning(
                $"Discarding segment translation regeneration for {segmentId} from run {snapshot.RunId} because the source text changed.");
            return;
        }

        var artifact = await _artifactReader.LoadTranslationAsync(translationPath, cancellationToken).ConfigureAwait(false);
        var matchedSegment = artifact.Segments?.FirstOrDefault(segment => string.Equals(segment.Id, segmentId, StringComparison.Ordinal));
        if (matchedSegment is null)
            throw new InvalidOperationException($"Translation segment not found for regeneration: {segmentId}");

        matchedSegment.TranslatedText = result.TranslatedText;
        artifact.SourceLanguage = result.SourceLanguage;
        artifact.TargetLanguage = result.TargetLanguage;
        await ArtifactPersistence.AtomicWriteTextAsync(
                translationPath,
                ArtifactJson.SerializeTranslation(artifact),
                cancellationToken)
            .ConfigureAwait(false);
        await WriteTranslationManifestAsync(
                translationPath,
                artifact,
                result.SourceLanguage,
                result.TargetLanguage,
                cancellationToken)
            .ConfigureAwait(false);

        var candidate = SessionSnapshotSemantics.ClearTtsOutputs(CurrentSession) with
        {
            Stage = SessionWorkflowStage.Translated,
            TranslationPath = translationPath,
            SourceLanguage = result.SourceLanguage,
            TargetLanguage = result.TargetLanguage,
            TranslationRuntime = snapshot.Plan.Runtime,
            TranslationProvider = snapshot.Plan.ProviderId,
            TranslationModel = snapshot.Model,
            TranslationSettingsDriftedSinceArtifact = TranslationSettingsDrifted(snapshot),
            TranslationRunId = snapshot.RunId,
            TranslatedAtUtc = DateTimeOffset.UtcNow,
            StatusMessage = TranslationSettingsDrifted(snapshot)
                ? $"Regenerated translation for segment {segmentId} using previous settings. Re-render the dub to apply current settings."
                : $"Regenerated translation for segment {segmentId}. Re-render the dub to finalize it.",
        };
        if (!ArtifactIntegrityValidator.ValidateTranslation(candidate, out var integrityError))
            throw new InvalidOperationException($"Translation integrity validation failed after segment regeneration: {integrityError}");

        _log.Info($"Segment translation regenerated: {segmentId}");
        lock (_sessionLock)
        {
            CurrentSession = candidate;
        }
        await SaveCurrentSessionAsync().ConfigureAwait(false);
    }

    public async Task<List<WorkflowSegmentState>> GetSegmentWorkflowListAsync()
    {
        return [.. await _artifactReader.BuildWorkflowSegmentsAsync(CurrentSession)];
    }

    // Stable segment ID derived from start time — must match the format written by translation providers.
    // Python: f"segment_{start}" → e.g. "segment_0.0", "segment_3.68"
    internal static string SegmentId(double start) =>
        start == (int)start
            ? FormattableString.Invariant($"segment_{start:0.0}")
            : FormattableString.Invariant($"segment_{start}");

    /// <summary>
    /// Restores a previously-opened session by ID, stashing the current one first.
    /// The coordinator queues a declarative media reload request for the view layer.
    /// </summary>
    public void RestoreSession(Guid sessionId)
    {
        // Try in-memory cache first, then fall back to disk.
        var restored = _sessionSwitchService.LoadSession(sessionId, _mediaSnapshotCache);

        if (restored is null)
        {
            _log.Warning($"RestoreSession: session {sessionId} not found in cache or on disk.");
            return;
        }

        // Stash and persist the current session before switching.
        if (!string.IsNullOrEmpty(CurrentSession.SourceMediaPath))
        {
            RecentSessions = _sessionSwitchService.StashCurrentSession(
                CurrentSession,
                _mediaSnapshotCache,
                MediaSnapshotCacheLimit);
        }

        var validation = SessionSnapshotSemantics.ValidateArtifacts(restored);
        var validated = validation.Snapshot;
        if (validation.OriginalStage != validated.Stage)
        {
            _log.Warning(
                $"ValidateArtifacts[session-restore]: downgraded stage {validation.OriginalStage} -> {validated.Stage}; " +
                $"cleared={string.Join(",", validation.ClearedArtifacts)}; provenance={SessionSnapshotSemantics.DescribeSessionProvenance(validated)}");
        }
        var nowUtc = DateTimeOffset.UtcNow;
        lock (_sessionLock)
        {
            CurrentSession = validated with
            {
                LastUpdatedAtUtc = nowUtc,
                StatusMessage = validated.Stage >= SessionWorkflowStage.TtsGenerated
                    ? "Restored session with TTS. Ready for playback."
                    : validated.Stage >= SessionWorkflowStage.Translated
                        ? "Restored session with translation. Ready for TTS/dubbing."
                    : validated.Stage >= SessionWorkflowStage.Diarized
                        ? "Restored session with speaker mapping. Ready to resume translation/TTS."
                        : validated.Stage >= SessionWorkflowStage.Transcribed
                            ? "Restored session with transcript. Ready for translation."
                        : validated.Stage >= SessionWorkflowStage.MediaLoaded
                                ? "Restored session with media. Ready for transcription."
                                : "Restored foundation session.",
            };
        }

        MarkSessionInputsChanged("session restored");
        _log.Info($"Restored session {sessionId} (stage: {CurrentSession.Stage}).");
        QueueMediaReloadRequest(autoPlay: false, "session-restore");
        SaveCurrentSession();
        RecentSessions = _recentStore.Load();
    }

    /// <summary>
    /// Computes which pipeline stages must be invalidated based on the current session's artifacts and the active pipeline settings.
    /// </summary>
    /// <returns>A <see cref="PipelineInvalidation"/> value that indicates which pipeline stages (if any) require reset.</returns>
    public PipelineInvalidation CheckSettingsInvalidation()
    {
        var cs = CurrentSession;
        var s  = CurrentSettings;

        var effectiveStage = SessionSnapshotSemantics.ResolveArtifactStage(cs);
        var invalidation = SessionSnapshotSemantics.ComputeInvalidation(cs, s);

        _log.Debug(
            $"CheckSettingsInvalidation: stage={cs.Stage}, effectiveStage={effectiveStage}, invalidation={invalidation}, provenance=({SessionSnapshotSemantics.DescribeSessionProvenance(cs)})");
        return invalidation;
    }

    /// <summary>
    /// Records a pending TTS generation task for later observation and removes any completed tasks from the internal tracking list.
    /// </summary>
    /// <param name="task">The TTS-related <see cref="Task"/> to track; callers may await or monitor the snapshot returned by <see cref="SnapshotPendingTtsTasks"/>.</param>

    internal void TrackPendingTtsTask(Task task)
    {
        lock (_pendingTtsTasksLock)
        {
            _pendingTtsTasks.RemoveAll(static t => t.IsCompleted);
            _pendingTtsTasks.Add(task);
        }
    }

    /// <summary>
    /// Create a snapshot of currently tracked pending TTS generation tasks after pruning completed tasks.
    /// </summary>
    /// <returns>An array of pending TTS <see cref="Task"/> instances with completed tasks removed.</returns>
    /// <remarks>
    /// This method is thread-safe: it prunes completed tasks and captures the remaining tasks while holding the internal pending-task lock.
    /// It does not await, start, cancel, or otherwise modify the returned tasks beyond removing completed entries from the internal tracker.
    /// </remarks>
    internal Task[] SnapshotPendingTtsTasks()
    {
        lock (_pendingTtsTasksLock)
        {
            _pendingTtsTasks.RemoveAll(static t => t.IsCompleted);
            return _pendingTtsTasks.ToArray();
        }
    }

    /// <summary>
    /// Processes an updated container probe result: records the probe state, updates runtime warmup text when the probe matches the current GPU service URL, updates bootstrap diagnostics and inference mode if any container-related diagnostics changed, and emits a readiness signal.
    /// </summary>
    /// <param name="probeResult">The latest probe result for a containerized inference service (URL, state, CUDA availability/version, and staleness).</param>
    /// <remarks>
    /// Side effects:
    /// - Updates the coordinator's probe-state cache for the probe's normalized base URL.
    /// - May update <see cref="RuntimeWarmupStatusText"/> when the probe URL matches the coordinator's effective GPU service URL and a non-empty warmup description is available.
    /// - Updates <see cref="BootstrapDiagnostics"/> container-related fields and recalculates <see cref="InferenceMode"/> when those diagnostics change.
    /// - Emits a readiness signal of kind <see cref="ReadinessSignalKind.ProbeResultUpdated"/>; <c>forceRefresh</c> is true when the probe URL is new, the probe state changed, or the probe state is not <see cref="ContainerizedProbeState.Checking"/>.
    /// </remarks>
    private void OnProbeResultUpdated(ContainerizedProbeResult probeResult)
    {
        var normalizedUrl = ContainerizedInferenceClient.NormalizeBaseUrl(probeResult.ServiceUrl);
        var hadPrevious = _lastProbeStates.TryGetValue(normalizedUrl, out var previousState);
        _lastProbeStates[normalizedUrl] = probeResult.State;
        var forceRefresh = !hadPrevious || previousState != probeResult.State || probeResult.State != ContainerizedProbeState.Checking;
        if (RequiresContainerizedRuntime()
            && string.Equals(
                normalizedUrl,
                ContainerizedInferenceClient.NormalizeBaseUrl(CurrentSettings.EffectiveGpuServiceUrl),
                StringComparison.OrdinalIgnoreCase))
        {
            var warmupText = DescribeRuntimeWarmupStatus(probeResult);
            if (!string.IsNullOrWhiteSpace(warmupText))
                RuntimeWarmupStatusText = warmupText;
        }

        var currentBootstrap = BootstrapDiagnostics;
        var updatedBootstrap = currentBootstrap with
        {
            ContainerizedServiceAvailable = probeResult.State == ContainerizedProbeState.Available,
            ContainerizedServiceUrl = string.IsNullOrWhiteSpace(probeResult.ServiceUrl)
                ? currentBootstrap.ContainerizedServiceUrl
                : probeResult.ServiceUrl,
            ContainerizedCudaAvailable = probeResult.CudaAvailable,
            ContainerizedCudaVersion = probeResult.CudaVersion ?? currentBootstrap.ContainerizedCudaVersion,
        };
        if (updatedBootstrap != currentBootstrap)
        {
            BootstrapDiagnostics = updatedBootstrap;
            InferenceMode = ResolveInferenceMode(updatedBootstrap);
        }

        EmitReadinessSignal(
            ReadinessSignalKind.ProbeResultUpdated,
            summary: $"Probe {probeResult.State}" + (probeResult.IsStale ? " (stale)" : string.Empty),
            source: normalizedUrl,
            forceRefresh: forceRefresh);
    }

    private void EmitReadinessSignal(
        ReadinessSignalKind kind,
        string summary,
        string? source = null,
        bool forceRefresh = false)
    {
        var signal = new ReadinessSignal(kind, DateTimeOffset.UtcNow, summary, source, forceRefresh);
        LastReadinessSignal = signal;
        ReadinessLastUpdatedUtc = signal.TimestampUtc;
        _readinessSignals.OnNext(signal);
    }

    partial void OnRuntimeWarmupStatusTextChanged(string? value) =>
        EmitReadinessSignal(
            ReadinessSignalKind.RuntimeWarmupStatusChanged,
            summary: string.IsNullOrWhiteSpace(value) ? "Runtime warmup idle." : value,
            source: nameof(RuntimeWarmupStatusText),
            forceRefresh: true);

    partial void OnBootstrapDiagnosticsChanged(BootstrapDiagnostics value) =>
        EmitReadinessSignal(
            ReadinessSignalKind.BootstrapApplied,
            summary: value.DiagnosticSummary,
            source: nameof(BootstrapDiagnostics),
            forceRefresh: true);

    public sealed record BootstrapWarmupData(
        BootstrapDiagnostics Diagnostics,
        IReadOnlyList<WorkflowSessionSnapshot> Snapshots,
        InferenceMode ResolvedInferenceMode);

    private static bool HasDiarizationMarker(WorkflowSessionSnapshot snapshot) =>
        SessionSnapshotSemantics.HasDiarizationMarker(snapshot);
}
