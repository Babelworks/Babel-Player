using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

/// <summary>
/// Headless end-to-end pipeline driver.
///
/// Invoked when the app is started with the <c>--dub</c> flag:
/// <code>
/// BabelPlayer.exe --dub --media "clip.mp4"
/// BabelPlayer.exe --dub --media "clip.mp4" --lang es --out "C:\out" --no-mp4
/// </code>
///
/// Runs the real <see cref="SessionWorkflowCoordinator"/> (the same composition root
/// as the desktop app) through transcription, optional diarization, translation, and
/// TTS, then writes SRT, MP3, and MP4 exports.
///
/// Exit codes:
///   0    Success.
///   1    Bad arguments.
///   2    Pipeline or runtime failure.
///   130  Cancelled (Ctrl+C).
/// </summary>
public static class DubCli
{
    private const int ExitSuccess = 0;
    private const int ExitArgumentError = 1;
    private const int ExitPipelineFailure = 2;
    private const int ExitCancelled = 130;

    public static async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        if (args.Any(a => string.Equals(a, "--help", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(a, "-h", StringComparison.OrdinalIgnoreCase)))
        {
            PrintUsage();
            return ExitSuccess;
        }

        string? media = BenchmarkCli.GetArg(args, "--media");
        string? lang = BenchmarkCli.GetArg(args, "--lang");
        string? outDir = BenchmarkCli.GetArg(args, "--out");
        string? ttsOverride = BenchmarkCli.GetArg(args, "--tts");
        string? voiceOverride = BenchmarkCli.GetArg(args, "--voice");
        string? projectDir = BenchmarkCli.GetArg(args, "--project-dir");
        bool noDiarization = HasFlag(args, "--no-diarization");
        bool noMp4 = HasFlag(args, "--no-mp4");
        bool consentClone = HasFlag(args, "--consent-clone");
        bool keepRenders = HasFlag(args, "--keep-renders");

        var known = new[] { "--dub", "--media", "--lang", "--out", "--tts", "--voice", "--project-dir", "--no-diarization", "--no-mp4", "--consent-clone", "--keep-renders", "--help", "-h" };
        var unknown = args.Where(a => a.StartsWith('-') && !known.Contains(a, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0)
        {
            Console.Error.WriteLine($"[dub] Unknown flag(s): {string.Join(", ", unknown)}");
            PrintUsage();
            return ExitArgumentError;
        }

        if (media is null)
        {
            Console.Error.WriteLine("[dub] --media <path> is required.");
            PrintUsage();
            return ExitArgumentError;
        }

        if (!File.Exists(media))
        {
            Console.Error.WriteLine($"[dub] Media file not found: {media}");
            return ExitArgumentError;
        }

        if (projectDir is not null && !IsValidProjectDir(projectDir))
        {
            Console.Error.WriteLine($"[dub] Invalid project directory: {projectDir}");
            return ExitArgumentError;
        }

        media = Path.GetFullPath(media);
        string outputDir = string.IsNullOrWhiteSpace(outDir)
            ? Path.GetDirectoryName(media) ?? Environment.CurrentDirectory
            : Path.GetFullPath(outDir);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        ConsoleCancelEventHandler? cancelHandler = null;

        var appDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BabelPlayer");

        var logPath = Path.Combine(appDataRoot, "logs", "dub.log");
        var log = new AppLog(logPath);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var startedUtc = DateTimeOffset.UtcNow;

        Console.WriteLine();
        Console.WriteLine("┌──────────────────────────────────────┐");
        Console.WriteLine("│  Babel Player Dub CLI                 │");
        Console.WriteLine("├──────────────────────────────────────┤");
        Console.WriteLine($"│  media    : {Trim(media),-25} │");
        Console.WriteLine($"│  output   : {Trim(outputDir),-25} │");
        Console.WriteLine("└──────────────────────────────────────┘");
        Console.WriteLine();

        SessionWorkflowCoordinator? coordinator = null;
        try
        {
            Directory.CreateDirectory(outputDir);

            cancelHandler = (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;
            var settingsService = new SettingsService(
                Path.Combine(appDataRoot, "settings", "app-settings.json"), log);
            var settings = settingsService.LoadOrDefault();

            if (!string.IsNullOrWhiteSpace(lang))
                settings.TargetLanguage = lang.Trim().ToLowerInvariant();
            if (noDiarization)
                settings.DiarizationProvider = string.Empty;
            if (!string.IsNullOrWhiteSpace(ttsOverride))
            {
                settings.TtsProvider = ttsOverride.Trim().ToLowerInvariant();
                settings.TtsProfile = InferenceRuntimeCatalog.InferTtsProfile(settings.TtsProvider);
            }
            if (!string.IsNullOrWhiteSpace(voiceOverride))
                settings.TtsVoice = voiceOverride.Trim();

            if (consentClone)
                settings.ChatterboxVoiceCloneConsent = true;
            if (keepRenders)
                settings.KeepRenderArtifacts = true;

            Console.WriteLine($"[dub] transcription : {settings.TranscriptionProvider} ({settings.TranscriptionModel})");
            Console.WriteLine($"[dub] translation  : {settings.TranslationProvider} -> {settings.TargetLanguage}");
            Console.WriteLine($"[dub] tts          : {settings.TtsProvider}");
            Console.WriteLine($"[dub] diarization  : {(string.IsNullOrEmpty(settings.DiarizationProvider) ? "off" : settings.DiarizationProvider)}");
            Console.WriteLine();

            var sessionsRoot = ResolveSessionsRoot(appDataRoot, projectDir);
            Console.WriteLine($"[dub] sessions    : {sessionsRoot}");
            var perSessionStore = new PerSessionSnapshotStore(sessionsRoot, log);
            var recentStore = new RecentSessionsStore(
                Path.Combine(appDataRoot, "state", "recent-sessions.json"), log);

            var legacyKeyPath = Path.Combine(appDataRoot, "state", "api-keys.json");
            ISecureCredentialProvider keyProvider = OperatingSystem.IsWindows()
                ? new WindowsCredentialProvider()
                : new FileSystemCredentialProvider(legacyKeyPath);
            var apiKeyStore = new ApiKeyStore(keyProvider, legacyKeyPath);

            var transportManager = new MediaTransportManager(
                videoOptionsFactory: () => new VideoPlaybackOptions(
                    HwdecMode: settings.VideoHwdec,
                    GpuApi: settings.VideoGpuApi,
                    UseGpuNext: settings.VideoUseGpuNext,
                    VsrEnabled: settings.VideoVsrEnabled,
                    HdrPlaybackMode: settings.VideoHdrPlaybackMode,
                    AllowHdrPassthrough: settings.VideoHdrPlaybackMode != VideoHdrPlaybackMode.Off
                        && HardwareSnapshot.QueryActiveHdrDisplay(),
                    ToneMapping: settings.VideoToneMapping,
                    TargetPeak: settings.VideoTargetPeak,
                    HdrComputePeak: settings.VideoHdrComputePeak),
                log: log);

            coordinator = DependencyLocator.CreateSessionCoordinator(
                log, settings, perSessionStore, recentStore, apiKeyStore, transportManager,
                ResolveStateRoot(appDataRoot, projectDir), log, out _);

            Console.WriteLine("[dub] loading media…");
            coordinator.LoadMedia(media);
            Console.WriteLine($"[dub] session {coordinator.CurrentSession.SessionId} at stage {coordinator.CurrentSession.Stage}");

            if ((!string.IsNullOrWhiteSpace(ttsOverride) || !string.IsNullOrWhiteSpace(voiceOverride)) &&
                coordinator.CurrentSession.Stage >= SessionWorkflowStage.Translated)
            {
                Console.WriteLine("[dub] re-running TTS under the requested provider.");
                coordinator.ResetPipelineToTranslated();
            }

            await RunPipelineAsync(coordinator, cts.Token).ConfigureAwait(false);

            var stem = Path.GetFileNameWithoutExtension(media);
            var segments = await coordinator.GetSegmentWorkflowListAsync().ConfigureAwait(false);

            var srtPath = Path.Combine(outputDir, $"{stem}-captions.srt");
            File.WriteAllText(srtPath, SrtGenerator.Generate(segments));
            Console.WriteLine($"[dub] wrote {srtPath}");

            var render = await coordinator.TryRenderDubAudioForExportAsync(cts.Token).ConfigureAwait(false);
            if (render is null)
            {
                Console.Error.WriteLine("[dub] Dub render returned no output (need translation + TTS clips).");
                return ExitPipelineFailure;
            }

            var mp3Path = Path.Combine(outputDir, $"{stem}-dub.mp3");
            File.Copy(render.MixedWithAmbiancePath ?? render.DubTimelinePath, mp3Path, overwrite: true);
            if (Path.GetFullPath(mp3Path).Equals(Path.GetFullPath(media), StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("[dub] Cannot overwrite source media. Output would overwrite input file.");
                return ExitPipelineFailure;
            }
            Console.WriteLine($"[dub] wrote {mp3Path}");

            var exitCode = ExitSuccess;
            string? writtenMp4Path = null;

            if (!noMp4)
            {
                var mp4Path = Path.Combine(outputDir, $"{stem}-dub.mp4");
                var session = coordinator.CurrentSession;
                var encoder = HardwareEncoderHelper.ResolveEncoder(coordinator.CurrentSettings, coordinator.HardwareSnapshot);
                var planner = new VideoExportPlanner();
                var options = new ExportVideoOptions(
                    mp4Path,
                    IncludeTtsAudio: true,
                    IncludeSoftCaptions: segments.Count > 0,
                    BurnInCaptions: false,
                    OverwriteExisting: true,
                    Encoder: encoder,
                    DubAudioPathOverride: render.MixedWithAmbiancePath ?? render.DubTimelinePath);

                var validation = planner.Validate(session, segments, options);
                if (!validation.CanExport)
                {
                    Console.Error.WriteLine($"[dub] MP4 export rejected: {string.Join(" ", validation.Issues)}");
                    exitCode = ExitPipelineFailure;
                }
                else
                {
                    var plan = planner.BuildPlan(session, segments, options);
                    await FfmpegVideoExportRunner.RunPlanAsync(plan, coordinator.Log, cts.Token).ConfigureAwait(false);
                    Console.WriteLine($"[dub] wrote {mp4Path}");
                    writtenMp4Path = mp4Path;
                }
            }

            if (settings.KeepRenderArtifacts)
            {
                Console.WriteLine($"[dub] kept {render.DubTimelinePath}");
                if (!string.Equals(render.MixedWithAmbiancePath, render.DubTimelinePath, StringComparison.OrdinalIgnoreCase))
                    Console.WriteLine($"[dub] kept {render.MixedWithAmbiancePath}");
            }
            else
            {
                TryDeleteQuiet(render.DubTimelinePath);
                if (!string.Equals(render.MixedWithAmbiancePath, render.DubTimelinePath, StringComparison.OrdinalIgnoreCase))
                    TryDeleteQuiet(render.MixedWithAmbiancePath);
            }

            WriteExportManifest(outputDir, media, coordinator, segments, srtPath, mp3Path, writtenMp4Path, startedUtc, exitCode);

            if (exitCode != ExitSuccess)
                return exitCode;

            stopwatch.Stop();
            Console.WriteLine();
            Console.WriteLine($"[dub] complete in {stopwatch.Elapsed:mm\\:ss}. log: {logPath}");
            return exitCode;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("[dub] Cancelled.");
            return ExitCancelled;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[dub] Pipeline failure: {ex.Message}");
            Console.Error.WriteLine($"[dub] Log: {logPath}");
            log.Error("Dub CLI pipeline failure.", ex);
            return ExitPipelineFailure;
        }
        finally
        {
            if (cancelHandler is not null)
            {
                Console.CancelKeyPress -= cancelHandler;
            }

            try
            {
                coordinator?.Dispose();
            }
            catch
            {
            }
        }
    }

    private static void WriteExportManifest(
        string outputDir,
        string media,
        SessionWorkflowCoordinator coordinator,
        System.Collections.Generic.List<WorkflowSegmentState> segments,
        string srtPath,
        string mp3Path,
        string? mp4Path,
        DateTimeOffset startedUtc,
        int exitCode)
    {
        try
        {
            var current = coordinator.CurrentSettings;
            var manifest = new DubExportManifest(
                Path.GetFullPath(media),
                current.TargetLanguage,
                current.TranscriptionProvider,
                current.TranslationProvider,
                current.TtsProvider,
                current.TtsVoice,
                !string.IsNullOrEmpty(current.DiarizationProvider),
                mp4Path is not null,
                srtPath,
                mp3Path,
                mp4Path,
                startedUtc,
                DateTimeOffset.UtcNow,
                segments.Count,
                exitCode);
            Console.WriteLine($"[dub] wrote {DubManifest.Write(outputDir, manifest)}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[dub] Could not write export manifest: {ex.Message}");
        }
    }

    private static async Task RunPipelineAsync(
        SessionWorkflowCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        using var watcherCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var lastStage = coordinator.CurrentSession.Stage;
        var watcher = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(500, watcherCts.Token).ConfigureAwait(false);
                    var stage = coordinator.CurrentSession.Stage;
                    if (stage != lastStage)
                    {
                        lastStage = stage;
                        Console.WriteLine($"[dub] stage -> {stage}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);

        try
        {
            Console.WriteLine($"[dub] pipeline running from {lastStage}…");
            await coordinator.AdvancePipelineAsync(
                new Progress<double>(p => Console.Write($"\r[dub] progress {p,3:F0}%   ")),
                cancellationToken).ConfigureAwait(false);
            Console.WriteLine();
            Console.WriteLine($"[dub] pipeline finished at {coordinator.CurrentSession.Stage}");
        }
        finally
        {
            watcherCts.Cancel();
            try
            {
                await watcher.ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Session storage root for a headless run: the portable project directory
    /// when <c>--project-dir</c> is given (a <c>sessions</c> folder is created
    /// inside it so snapshot, transcripts, and translations travel with the
    /// project), otherwise the machine-local app data sessions folder.
    /// </summary>
    internal static string ResolveSessionsRoot(string appDataRoot, string? projectDir) =>
        string.IsNullOrWhiteSpace(projectDir)
            ? Path.Combine(appDataRoot, "sessions")
            : Path.Combine(Path.GetFullPath(projectDir.Trim()), "sessions");

    /// <summary>
    /// State directory for current session snapshot: project-local when <c>--project-dir</c> is
    /// given (so session state is isolated per-project), otherwise the machine-local app data
    /// state folder.
    /// </summary>
    internal static string ResolveStateRoot(string appDataRoot, string? projectDir) =>
        string.IsNullOrWhiteSpace(projectDir)
            ? Path.Combine(appDataRoot, "state")
            : Path.Combine(Path.GetFullPath(projectDir.Trim()), "sessions", "state");

    internal static bool IsValidProjectDir(string projectDir)
    {
        try
        {
            _ = Path.GetFullPath(projectDir);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static string Trim(string value) =>
        value.Length <= 25 ? value : value[..22] + "…";

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
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine();
        Console.WriteLine("Babel Player Dub CLI (headless E2E pipeline)");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  BabelPlayer.exe --dub --media <path> [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --media <path>          Source media file (required)");
        Console.WriteLine("  --lang <code>           Translation target language (default: settings)");
        Console.WriteLine("  --out <dir>             Output directory (default: alongside media)");
        Console.WriteLine("  --tts <provider>        TTS provider override (e.g. chatterbox)");
        Console.WriteLine("  --voice <id>            TTS voice/model override (default: settings)");
        Console.WriteLine("  --no-diarization        Skip diarization for this run");
        Console.WriteLine("  --no-mp4                Skip MP4 export (SRT + MP3 only)");
        Console.WriteLine("  --consent-clone         Grant voice-cloning consent for this run");
        Console.WriteLine("  --project-dir <dir>     Portable session storage (default: app-local)");
        Console.WriteLine("  --keep-renders          Keep intermediate render files for debugging");
        Console.WriteLine("  --help, -h              Show this help");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  BabelPlayer.exe --dub --media clip.mp4 --lang es");
        Console.WriteLine("  BabelPlayer.exe --dub --media clip.mp4 --no-diarization --no-mp4");
        Console.WriteLine("  BabelPlayer.exe --dub --media clip.mp4 --tts chatterbox --consent-clone");
        Console.WriteLine();
    }
}
