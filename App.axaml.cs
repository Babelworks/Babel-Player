using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;
using Babel.Player.ViewModels;
using Babel.Player.Views;

namespace Babel.Player;

public partial class App : Application
{
    private SessionWorkflowCoordinator? _sessionWorkflowCoordinator;
    private AppLog? _startupLog;
    private SettingsService? _settingsService;
    private ApiKeyStore? _apiKeyStore;
    private ManagedVenvHostManager? _primaryGpuManager;
    private System.Threading.Timer? _statusDebounceTimer;

    // Resolved once at startup so crash handlers can reference it without
    // touching the AppLog instance (which may itself be in a bad state).
    private string? _logFilePath;

    /// <summary>
    /// Loads the Avalonia XAML resources for the application.
    /// </summary>
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Performs global application initialization: configures logging, settings, secure credential storage, media/transport components, the session workflow coordinator, the main window and UI theme, global crash handlers, and background startup probes.
    /// </summary>
    /// <remarks>
    /// Side effects:
    /// - Subscribes to AppDomain.CurrentDomain.UnhandledException and TaskScheduler.UnobservedTaskException.
    /// - Initializes application data directories, log file, and SettingsService.
    /// - Chooses and configures an ISecureCredentialProvider and creates the ApiKeyStore.
    /// - Creates media/transport components and the session workflow coordinator via DependencyLocator.
    /// - Creates and shows the main window with its view model and sets application shutdown behavior.
    /// - Wires GPU bootstrap progress into the UI status bar (debounced) when a primary GPU manager is available.
    /// - Starts background tasks to gather bootstrap warmup data and detect hardware, posting results to the UI thread.
    /// <summary>
    /// Performs application startup: sets up global exception handlers and logging, loads settings and persistence stores, initializes credential and media subsystems, constructs the session coordinator and main window view model, wires desktop lifecycle handlers and UI status updates, and starts background warmup and hardware-detection probes.
    /// </summary>
    /// <remarks>
    /// Executed after the Avalonia framework has initialized. Initialization is performed only for a classic desktop lifetime; otherwise control falls through to the base implementation. This method also forces the app theme to dark and registers an exit handler to flush and dispose session resources on shutdown.
    /// </remarks>
    public override void OnFrameworkInitializationCompleted()
    {
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var appDataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BabelPlayer");

            _logFilePath = Path.Combine(appDataRoot, "logs", "babel-player.log");
            var appLog = new AppLog(_logFilePath);
            _startupLog = appLog;

            TryEnsureWindowsNativeDependencies(appLog, appDataRoot);
            LogResolvedAudioToolPaths(appLog, "startup");

            // Initialize Settings and other stores
            var settingsFilePath = Path.Combine(appDataRoot, "settings", "app-settings.json");
            _settingsService = new SettingsService(settingsFilePath, appLog);
            var appSettings = _settingsService.LoadOrDefault();
            if (!string.Equals(appSettings.EffectiveContainerizedServiceUrl, appSettings.ContainerizedServiceUrl, StringComparison.Ordinal))
            {
                appLog.Debug(
                    $"Environment override active: {AppSettings.InferenceServiceUrlEnvVar}={appSettings.EffectiveContainerizedServiceUrl}");
            }

            // Resolve and apply saved UI language.  "auto" tracks the OS locale each launch,
            // mirroring the Theme = "System" sentinel pattern.
            var effectiveLang = LocalizationService.ResolveAppLanguage(appSettings.AppLanguage);
            try
            {
                LocalizationService.Instance.SetCulture(new CultureInfo(effectiveLang));
            }
            catch (CultureNotFoundException)
            {
                appLog.Warning($"Unknown UI culture '{effectiveLang}', falling back to 'en'.");
                LocalizationService.Instance.SetCulture(new CultureInfo("en"));
            }

            // Apply saved theme preference — forced to Dark in App.axaml
            if (Application.Current is { } app)
            {
                app.RequestedThemeVariant = ThemeVariant.Dark;
            }

            var perSessionStore = new PerSessionSnapshotStore(
                Path.Combine(appDataRoot, "sessions"), appLog);
            var recentStore = new RecentSessionsStore(
                Path.Combine(appDataRoot, "state", "recent-sessions.json"), appLog);

            var legacyKeyPath = Path.Combine(appDataRoot, "state", "api-keys.json");
            ISecureCredentialProvider keyProvider = OperatingSystem.IsWindows()
                ? new WindowsCredentialProvider()
                : new FileSystemCredentialProvider(legacyKeyPath);

            _apiKeyStore = new ApiKeyStore(keyProvider, legacyKeyPath);
            var modelDownloader = new ModelDownloader(appLog);
            var transportManager = new MediaTransportManager(
                videoOptionsFactory: () => new VideoPlaybackOptions(
                    HwdecMode:      appSettings.VideoHwdec,
                    GpuApi:         appSettings.VideoGpuApi,
                    UseGpuNext:     appSettings.VideoUseGpuNext,
                    VsrEnabled:     appSettings.VideoVsrEnabled,
                    HdrPlaybackMode: appSettings.VideoHdrPlaybackMode,
                    AllowHdrPassthrough: appSettings.VideoHdrPlaybackMode != VideoHdrPlaybackMode.Off
                        && HardwareSnapshot.QueryActiveHdrDisplay(),
                    ToneMapping:    appSettings.VideoToneMapping,
                    TargetPeak:     appSettings.VideoTargetPeak,
                    HdrComputePeak: appSettings.VideoHdrComputePeak),
                log: appLog);
            _sessionWorkflowCoordinator = DependencyLocator.CreateSessionCoordinator(
                appLog, appSettings, perSessionStore, recentStore, _apiKeyStore, transportManager, 
                Path.Combine(appDataRoot, "state"), _startupLog, out var primaryGpuManager);
            _primaryGpuManager = primaryGpuManager;


            desktop.Exit += OnDesktopExit;
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;

            var errorDialogService = new AvaloniaErrorDialogService(appLog);
            var pipelineRefreshDialogService = new AvaloniaPipelineRefreshDialogService();
            var dialogService = new AvaloniaDialogService();

            var mainVm = new MainWindowViewModel(
                _sessionWorkflowCoordinator,
                _settingsService,
                modelDownloader,
                _apiKeyStore,
                errorDialogService,
                pipelineRefreshDialogService,
                dialogService,
                logFilePath: _logFilePath);

            desktop.MainWindow = new MainWindow { DataContext = mainVm };
            // SetCulture above ran before the main window existed, so re-apply
            // flow direction now that desktop.MainWindow is assigned.  This is
            // what makes first-launch RTL cultures (e.g. Arabic) lay out right-to-left.
            LocalizationService.Instance.ApplyFlowDirectionToOpenWindows();
            var coordinator = _sessionWorkflowCoordinator;

            // Wire live bootstrap progress into the status bar.
            // Debounce: create a new 150 ms one-shot timer on each line; swapping
            // it atomically disposes the previous pending update so rapid pip/uv
            // output doesn't flood the dispatcher queue.
            void PostStatus(string line)
            {
                var captured = line;
                var timer = new System.Threading.Timer(
                    _ => Dispatcher.UIThread.Post(() => coordinator.RuntimeWarmupStatusText = $"Setup: {captured}"),
                    null, 150, System.Threading.Timeout.Infinite);
                System.Threading.Interlocked.Exchange(ref _statusDebounceTimer, timer)?.Dispose();
            }

            // GPU venv — surface install progress (first-time and rebuilds).
            if (_primaryGpuManager is not null)
                _primaryGpuManager.BootstrapProgressCallback = PostStatus;

            // CPU runtime installation is intentionally not triggered during startup.
            // Defer any first-time bootstrap/download until a user-initiated CPU
            // transcription/TTS workflow explicitly requests it.

            coordinator.StartStartupWarmupTasks(
                warmup => Dispatcher.UIThread.Post(() => coordinator.ApplyBootstrapWarmupData(warmup)),
                hardware => Dispatcher.UIThread.Post(() => coordinator.HardwareSnapshot = hardware));
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void TryEnsureWindowsNativeDependencies(AppLog log, string appDataRoot)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var rid = WindowsPackagingPaths.NativeRidFolder;
        if (HasNativeDepsInOutput(rid))
            return;

        var repoRoot = ResolveRepoRootWithFetchScript();
        if (repoRoot is null)
            return;

        if (HasNativeDepsInRepo(repoRoot, rid))
        {
            CopyRepoNativeDepsToOutput(repoRoot, rid);
            log.Info("Startup native deps copied from repo cache.");
            LogResolvedAudioToolPaths(log, "startup-repo-cache");
            return;
        }

        var markerDir = Path.Combine(appDataRoot, "state");
        Directory.CreateDirectory(markerDir);
        var markerPath = Path.Combine(markerDir, "win-native-deps-bootstrap.marker");
        if (!ShouldAttemptNativeDepsBootstrap(markerPath))
            return;

        _ = Task.Run(async () =>
        {
            var scriptPath = Path.Combine(repoRoot, "scripts", "fetch-win-native-deps.ps1");
            var exitCode = await RunFetchScriptAsync(scriptPath).ConfigureAwait(false);
            File.WriteAllText(markerPath, $"{DateTimeOffset.UtcNow:O}|exit={exitCode}");

            if (exitCode == 0)
            {
                CopyRepoNativeDepsToOutput(repoRoot, rid);
                log.Info("Startup native deps bootstrap completed.");
                LogResolvedAudioToolPaths(log, "post-bootstrap");
            }
            else
            {
                log.Warning($"Startup native deps bootstrap skipped/failed (exit={exitCode}).");
            }
        });
    }

    private static bool HasNativeDepsInOutput(string rid)
    {
        var appDir = AppContext.BaseDirectory;
        return File.Exists(Path.Combine(appDir, "native", rid, "libmpv-2.dll"))
            && File.Exists(Path.Combine(appDir, "tools", rid, "uv.exe"))
            && File.Exists(Path.Combine(appDir, "tools", rid, "ffmpeg.exe"))
            && File.Exists(Path.Combine(appDir, "tools", rid, "ffprobe.exe"));
    }

    private static bool HasNativeDepsInRepo(string repoRoot, string rid)
    {
        return File.Exists(Path.Combine(repoRoot, "native", rid, "libmpv-2.dll"))
            && File.Exists(Path.Combine(repoRoot, "tools", rid, "uv.exe"))
            && File.Exists(Path.Combine(repoRoot, "tools", rid, "ffmpeg.exe"))
            && File.Exists(Path.Combine(repoRoot, "tools", rid, "ffprobe.exe"));
    }

    private static bool ShouldAttemptNativeDepsBootstrap(string markerPath)
    {
        if (!File.Exists(markerPath))
            return true;

        var age = DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(markerPath);
        return age > TimeSpan.FromHours(12);
    }

    private static string? ResolveRepoRootWithFetchScript()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; current is not null && depth < 10; depth++, current = current.Parent)
        {
            var scriptPath = Path.Combine(current.FullName, "scripts", "fetch-win-native-deps.ps1");
            if (File.Exists(scriptPath))
                return current.FullName;
        }

        return null;
    }

    private static async Task<int> RunFetchScriptAsync(string scriptPath)
    {
        var launchers = new[] { "pwsh", "powershell.exe" };
        foreach (var launcher in launchers)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = launcher,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? AppContext.BaseDirectory,
                };

                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-NonInteractive");
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
                psi.ArgumentList.Add("-File");
                psi.ArgumentList.Add(scriptPath);

                using var process = Process.Start(psi);
                if (process is null)
                    continue;

                await process.WaitForExitAsync().ConfigureAwait(false);
                return process.ExitCode;
            }
            catch
            {
                // Try the next launcher.
            }
        }

        return -1;
    }

    private static void CopyRepoNativeDepsToOutput(string repoRoot, string rid)
    {
        var appDir = AppContext.BaseDirectory;
        CopyIfExists(
            Path.Combine(repoRoot, "native", rid, "libmpv-2.dll"),
            Path.Combine(appDir, "native", rid, "libmpv-2.dll"));
        CopyIfExists(
            Path.Combine(repoRoot, "tools", rid, "uv.exe"),
            Path.Combine(appDir, "tools", rid, "uv.exe"));
        CopyIfExists(
            Path.Combine(repoRoot, "tools", rid, "ffmpeg.exe"),
            Path.Combine(appDir, "tools", rid, "ffmpeg.exe"));
        CopyIfExists(
            Path.Combine(repoRoot, "tools", rid, "ffprobe.exe"),
            Path.Combine(appDir, "tools", rid, "ffprobe.exe"));
    }

    private static void CopyIfExists(string source, string destination)
    {
        if (!File.Exists(source))
            return;

        var destinationDirectory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
            Directory.CreateDirectory(destinationDirectory);

        File.Copy(source, destination, overwrite: true);
    }

    private static void LogResolvedAudioToolPaths(AppLog log, string context)
    {
        // Delegate to DependencyLocator so the logged path matches the actual
        // runtime resolution (including the `-version` probe that filters out
        // binaries that exist but aren't runnable).
        var ffmpeg = DependencyLocator.FindFfmpeg() ?? "<missing>";
        var ffprobe = DependencyLocator.FindFfprobe() ?? "<missing>";
        log.Debug($"Audio tool resolution ({context}): ffmpeg={ffmpeg}; ffprobe={ffprobe}");
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        if (_sessionWorkflowCoordinator is null) return;
        _startupLog?.Info($"App shutdown requested: exitCode={e.ApplicationExitCode}");
        try
        {
            _sessionWorkflowCoordinator.FlushPendingSave();
        }
        catch (Exception ex)
        {
            _startupLog?.Error("Failed to save session on exit.", ex);
        }
        finally
        {
            _primaryGpuManager?.BootstrapProgressCallback = null;
            System.Threading.Interlocked.Exchange(ref _statusDebounceTimer, null)?.Dispose();
            _sessionWorkflowCoordinator.Dispose();
            (_startupLog as IDisposable)?.Dispose();
            _primaryGpuManager = null;
        }
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var msg = e.ExceptionObject is Exception ex ? ex.ToString() : e.ExceptionObject?.ToString() ?? "unknown";

        // 1. Always log to disk first — this is guaranteed to run even if the
        //    UI thread is in a bad state.
        _startupLog?.Error($"Unhandled exception (isTerminating={e.IsTerminating}).",
            new InvalidOperationException(msg));

        // 2. Show the full error to the user in a dedicated pop-up window.
        var header = e.IsTerminating
            ? $"FATAL — application will close after this dialog.\n\n{msg}"
            : msg;
        CrashReportWindow.ShowOnUiThread(header, _logFilePath);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // 1. Mark observed so the runtime does not re-throw and terminate.
        e.SetObserved();

        var msg = e.Exception.ToString();

        // 2. Log to disk.
        _startupLog?.Error("Unobserved task exception.", e.Exception);

        // 3. Show full error to the user.
        CrashReportWindow.ShowOnUiThread(msg, _logFilePath);
    }
}
