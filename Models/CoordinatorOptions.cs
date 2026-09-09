using Babel.Player.Services;
using Babel.Player.Services.Planning;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;

namespace Babel.Player.Models;

/// <summary>
/// Bundles the optional infrastructure dependencies for <see cref="SessionWorkflowCoordinator"/>.
/// Required dependencies (stores, registries, transport, settings) are passed directly to the
/// constructor. This record carries everything that has a reasonable default or may be absent in
/// test or minimal-host scenarios.
/// </summary>
public sealed record CoordinatorOptions
{
    /// <summary>API key store. Null in environments where no cloud providers are used.</summary>
    public ApiKeyStore? KeyStore { get; init; }

    /// <summary>
    /// Reads per-session artifacts from disk. When null the coordinator creates a default instance.
    /// </summary>
    public SessionArtifactReader? ArtifactReader { get; init; }

    /// <summary>
    /// Manages session switching and recent session history. When null the coordinator creates a
    /// default instance backed by the supplied per-session and recent-session stores.
    /// </summary>
    public SessionSwitchService? SessionSwitchService { get; init; }

    /// <summary>
    /// Diarization provider registry. Null disables speaker diarization.
    /// </summary>
    public IDiarizationRegistry? DiarizationRegistry { get; init; }

    /// <summary>
    /// Probes the containerized inference service for health and availability.
    /// Null when no containerized inference is configured.
    /// </summary>
    public ContainerizedServiceProbe? ContainerizedProbe { get; init; }

    /// <summary>
    /// Manages containerized or managed-venv inference host lifecycle.
    /// Null when no containerized inference is configured.
    /// </summary>
    public IContainerizedInferenceManager? ContainerizedInferenceManager { get; init; }

    /// <summary>
    /// Audio processing service (ffmpeg-backed). Null disables audio pre-processing steps.
    /// </summary>
    public IAudioProcessingService? AudioProcessingService { get; init; }

    /// <summary>
    /// Executes transcription, translation, TTS, and diarization provider calls. When null,
    /// <see cref="DefaultInferenceExecutionEngine.Instance"/> is used.
    /// </summary>
    public IInferenceExecutionEngine? InferenceExecutionEngine { get; init; }

    /// <summary>
    /// Stage execution planner used to resolve effective provider/runtime for each pipeline stage.
    /// When null, <see cref="DefaultExecutionPlanner.Instance"/> is used.
    /// </summary>
    public IExecutionPlanner? ExecutionPlanner { get; init; }

    /// <summary>
    /// Shared lease tracker for containerized inference requests. When provided, vocal separation
    /// requests are counted as active container requests so the managed host waits for them before
    /// restarting. Null disables lease tracking for vocal separation.
    /// </summary>
    public ContainerizedRequestLeaseTracker? RequestLeaseTracker { get; init; }

    /// <summary>
    /// When set, session artifacts (transcripts, translations, TTS, stems) use
    /// <c>{ProjectDirectory}/sessions/{sessionId}</c> instead of a media-sibling
    /// <c>{filename}.babel</c> folder. Headless <c>--project-dir</c> sets this so
    /// artifacts stay with the portable snapshot root.
    /// </summary>
    public string? ProjectDirectory { get; init; }

    /// <summary>Returns a <see cref="CoordinatorOptions"/> with all fields at their defaults (all null).</summary>
    public static CoordinatorOptions Empty { get; } = new();
}
