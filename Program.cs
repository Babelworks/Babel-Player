using Avalonia;
using Babel.Player.Services;
using System;
using System.Runtime.InteropServices;

namespace Babel.Player;

sealed class Program
{
    // AttachConsole lets WinExe binaries write to the parent terminal.
    // Returns false (harmlessly) when there is no parent console to attach to.
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    // [STAThread] is honoured only on synchronous entry points.
    // The benchmark path is headless; .GetAwaiter().GetResult() is safe there.
    [STAThread]
    public static int Main(string[] args)
    {
        // Intercept --benchmark before Avalonia sees the args.
        // All other args are forwarded to Avalonia as normal.
        if (Array.Exists(args, a =>
                string.Equals(a, "--benchmark", StringComparison.OrdinalIgnoreCase)))
        {
            if (OperatingSystem.IsWindows())
                AttachConsole(-1); // attach to parent terminal; no-op if none exists
            return BenchmarkCli.RunAsync(args).GetAwaiter().GetResult();
        }

        // Intercept --dub before Avalonia sees the args (headless E2E pipeline).
        if (Array.Exists(args, a =>
                string.Equals(a, "--dub", StringComparison.OrdinalIgnoreCase)))
        {
            if (OperatingSystem.IsWindows())
                AttachConsole(-1);
            return DubCli.RunAsync(args).GetAwaiter().GetResult();
        }

        // Intercept --tui before Avalonia sees the args (interactive terminal UI
        // over the same pipeline engine).
        if (Array.Exists(args, a =>
                string.Equals(a, "--tui", StringComparison.OrdinalIgnoreCase)))
        {
            if (OperatingSystem.IsWindows())
                AttachConsole(-1);
            return DubTui.RunAsync(args).GetAwaiter().GetResult();
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .WithDeveloperTools()
#if DEBUG
            .LogToTrace();
#else
            ;
#endif
}
