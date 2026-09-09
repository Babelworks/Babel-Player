using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Models.LanguageSupport;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

/// <summary>
/// Interactive terminal UI for the dubbing pipeline.
///
/// Invoked with the <c>--tui</c> flag. Menu-driven setup (media picker, language,
/// provider, options) collects inputs, then drives the exact same
/// <see cref="DubCli"/> engine via an argument vector — no separate pipeline
/// implementation exists here. Numbered menus read lines from stdin so sessions
/// are scriptable as well as interactive.
///
/// Exit codes mirror <see cref="DubCli"/>: 0 success, 1 args, 2 pipeline failure,
/// 130 cancelled.
/// </summary>
public static class DubTui
{
    private static readonly string[] MediaExtensions =
    [
        ".mp4", ".mkv", ".avi", ".mov", ".m4v", ".webm",
        ".mp3", ".wav", ".m4a", ".flac", ".ogg", ".opus", ".wma",
    ];

    private static readonly HashSet<string> AudioOnlyExtensions =
    new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".m4a", ".flac", ".ogg", ".opus", ".wma",
    };

    private static readonly (string Id, string Label)[] TtsChoices =
    [
        ("", "Use settings default"),
        (ProviderNames.Piper, "Piper (local CPU voices)"),
        (ProviderNames.Chatterbox, "Chatterbox (voice cloning, local)"),
        (ProviderNames.EdgeTts, "Edge TTS (cloud, no key needed)"),
    ];

    internal static IReadOnlyList<string> TargetLanguages => NllbLanguageCatalog.IsoCodes;

    internal static bool IsAudioOnlyMedia(string mediaPath) =>
        !string.IsNullOrEmpty(mediaPath) && AudioOnlyExtensions.Contains(Path.GetExtension(mediaPath));

    internal static bool IsSupportedTargetLanguage(string code) =>
        !string.IsNullOrWhiteSpace(code) &&
        NllbLanguageCatalog.IsoToFloresToken.ContainsKey(code.Trim().ToLowerInvariant());

    internal static string ResolveEffectiveTtsProvider(string? ttsChoice)
    {
        if (!string.IsNullOrWhiteSpace(ttsChoice))
            return ttsChoice.Trim().ToLowerInvariant();

        try
        {
            var settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BabelPlayer", "settings", "app-settings.json");
            if (File.Exists(settingsPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
                if (document.RootElement.TryGetProperty("TtsProvider", out var value) &&
                    value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(value.GetString()))
                    return value.GetString()!.Trim().ToLowerInvariant();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Fall through to the compiled default below.
        }

        return new AppSettings().TtsProvider.Trim().ToLowerInvariant();
    }

    internal static bool RequiresCloneConsent(string? ttsChoice) =>
        string.Equals(
            ResolveEffectiveTtsProvider(ttsChoice),
            ProviderNames.Chatterbox,
            StringComparison.OrdinalIgnoreCase);

    internal static string[] BuildDubArgv(
        string media,
        string lang,
        string? tts,
        bool diarization,
        bool mp4,
        string? outDir,
        bool consentClone)
    {
        var argv = new List<string> { "--dub", "--media", media, "--lang", lang };
        if (!string.IsNullOrEmpty(tts))
            argv.AddRange(["--tts", tts]);
        if (!diarization)
            argv.Add("--no-diarization");
        if (!mp4)
            argv.Add("--no-mp4");
        if (!string.IsNullOrWhiteSpace(outDir))
            argv.AddRange(["--out", outDir]);
        if (consentClone)
            argv.Add("--consent-clone");
        return argv.ToArray();
    }

    public static async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        if (args.Any(a => string.Equals(a, "--help", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(a, "-h", StringComparison.OrdinalIgnoreCase)))
        {
            PrintUsage();
            return 0;
        }

        var known = new[] { "--tui", "--media", "--lang", "--help", "-h" };
        var unknown = args.Where(a => a.StartsWith('-') && !known.Contains(a, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0)
        {
            Console.Error.WriteLine($"[tui] Unknown flag(s): {string.Join(", ", unknown)}");
            PrintUsage();
            return 1;
        }

        string? presetMedia = BenchmarkCli.GetArg(args, "--media");
        string? presetLang = BenchmarkCli.GetArg(args, "--lang");
        if (presetMedia is not null && !File.Exists(presetMedia))
        {
            Console.Error.WriteLine($"[tui] Media file not found: {presetMedia}");
            return 1;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Console.WriteLine();
            Console.WriteLine("Babel Player Dub TUI");
            Console.WriteLine("  1) Run dubbing pipeline");
            Console.WriteLine("  2) View log tail");
            Console.WriteLine("  3) Show current settings");
            Console.WriteLine("  0) Quit");
            var choice = PromptText("Select", "0");

            switch (choice.Trim())
            {
                case "1":
                    await RunPipelineWizardAsync(presetMedia, presetLang, cancellationToken).ConfigureAwait(false);
                    presetMedia = null;
                    presetLang = null;
                    break;
                case "2":
                    ShowLogTail();
                    break;
                case "3":
                    ShowSettings();
                    break;
                case "0":
                case "q":
                case "quit":
                case "exit":
                    return 0;
                default:
                    Console.WriteLine("Unknown choice. Enter 0-3 or q to quit.");
                    break;
            }
        }
    }

    private static async Task<int?> RunPipelineWizardAsync(
        string? presetMedia,
        string? presetLang,
        CancellationToken cancellationToken)
    {
        string? media = presetMedia ?? PickMediaFile();
        if (media is null)
            return null;

        string? lang = presetLang ?? PickLanguage();
        if (lang is null)
            return null;

        string? tts = PickTtsProvider();
        if (tts is null)
            return null;

        bool diarization = Confirm("Enable speaker diarization?", defaultYes: false);
        bool mp4 = Confirm("Export MP4 video?", defaultYes: !IsAudioOnlyMedia(media));
        string outDir = PromptText("Output directory (empty = alongside media)", string.Empty).Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(outDir))
        {
            try
            {
                _ = Path.GetFullPath(outDir);
            }
            catch (ArgumentException)
            {
                Console.WriteLine("[tui] Invalid output directory.");
                return null;
            }
        }

        bool consentClone = false;
        if (RequiresCloneConsent(tts))
        {
            Console.WriteLine("Chatterbox performs voice cloning, which requires explicit consent.");
            consentClone = Confirm("Grant voice-cloning consent for this run?", defaultYes: false);
            if (!consentClone)
            {
                Console.WriteLine("Cancelled: cloning consent is mandatory and non-bypassable.");
                return;
                return null;
        }

        var argv = BuildDubArgv(media, lang, tts, diarization, mp4, outDir, consentClone);

        Console.WriteLine();
        Console.WriteLine($"[tui] launching: BabelPlayer.exe {string.Join(" ", argv.Select(Quote))}");
        Console.WriteLine();
        int exitCode = await DubCli.RunAsync(argv, cancellationToken).ConfigureAwait(false);
        Console.WriteLine();
        Console.WriteLine(exitCode switch
        {
            0 => "[tui] pipeline complete.",
            130 => "[tui] pipeline cancelled.",
            _ => $"[tui] pipeline failed (exit {exitCode}). See the log for details.",
        });
        PromptText("Press Enter to continue", string.Empty);
        return exitCode;
    }

    private static string? PickMediaFile()
    {
        while (true)
        {
            string directory = Directory.GetCurrentDirectory();
            string[] files;
            try
            {
                files = Directory.GetFiles(directory)
                    .Where(f => MediaExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                Console.Error.WriteLine($"[tui] Cannot list {directory}: {ex.Message}");
                Console.WriteLine("  (type a different directory or full path; empty cancels)");
                string recovery = PromptText("Media", string.Empty).Trim();
                if (string.IsNullOrEmpty(recovery))
                    return null;
                if (Directory.Exists(recovery))
                {
                    try
                    {
                        Directory.SetCurrentDirectory(recovery);
                    }
                    catch (Exception setEx) when (setEx is UnauthorizedAccessException or IOException)
                    {
                        Console.Error.WriteLine($"[tui] Cannot enter {recovery}: {setEx.Message}");
                    }
                }
                else if (File.Exists(recovery))
                {
                    return Path.GetFullPath(recovery);
                }
                else
                {
                    Console.WriteLine("Not found. Try a valid path.");
                }
                continue;
            }

            Console.WriteLine();
            Console.WriteLine($"Media files in {directory}:");
            for (int index = 0; index < files.Length; index++)
                Console.WriteLine($"  {index + 1}) {Path.GetFileName(files[index])}");
            Console.WriteLine("  (or type a file name, directory, or full path; empty cancels)");

            string input = PromptText("Media", string.Empty).Trim();
            if (string.IsNullOrEmpty(input))
                return null;

            if (int.TryParse(input, out int number) && number >= 1 && number <= files.Length)
                return files[number - 1];

            string candidate = Path.IsPathFullyQualified(input)
                ? input
                : Path.Combine(directory, input);
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
            if (Directory.Exists(candidate))
            {
                try
                {
                    Directory.SetCurrentDirectory(candidate);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    Console.Error.WriteLine($"[tui] Cannot enter {candidate}: {ex.Message}");
                }
                continue;
            }

            Console.WriteLine("Not found. Try a listed number or a valid path.");
        }
    }

    private static string? PickLanguage()
    {
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine("Target language:");
            for (int index = 0; index < TargetLanguages.Count; index++)
            {
                var code = TargetLanguages[index];
                Console.WriteLine($"  {index + 1}) {code} - {LanguageDisplayNames.ForIso639(code, CultureInfo.InvariantCulture)}");
            }
            Console.WriteLine("  (or type a language code; empty cancels)");

            string input = PromptText("Language", string.Empty).Trim();
            if (string.IsNullOrEmpty(input))
                return null;

            if (int.TryParse(input, out int number) && number >= 1 && number <= TargetLanguages.Count)
                return TargetLanguages[number - 1];

            string typedCode = input.Trim().ToLowerInvariant();
            if (IsSupportedTargetLanguage(typedCode))
                return typedCode;

            Console.WriteLine("Unknown language. Pick a listed number or code.");
        }
    }

    private static string? PickTtsProvider()
    {
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine("TTS provider:");
            for (int index = 0; index < TtsChoices.Length; index++)
                Console.WriteLine($"  {index + 1}) {TtsChoices[index].Label}");
            Console.WriteLine("  (empty cancels)");

            string input = PromptText("TTS provider", string.Empty).Trim();
            if (string.IsNullOrEmpty(input))
                return null;

            if (int.TryParse(input, out int number) && number >= 1 && number <= TtsChoices.Length)
                return TtsChoices[number - 1].Id;

            var match = TtsChoices.FirstOrDefault(c =>
                string.Equals(c.Id, input, StringComparison.OrdinalIgnoreCase));
            if (match != default)
                return match.Id;

            Console.WriteLine("Unknown provider. Pick a listed number.");
        }
    }

    private static void ShowLogTail()
    {
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BabelPlayer", "logs", "dub.log");

        Console.WriteLine();
        if (!File.Exists(logPath))
        {
            Console.WriteLine("No dub log yet. Run the pipeline first.");
            return;
        }

        try
        {
foreach (var line in File.ReadLines(logPath).TakeLast(40))
    Console.WriteLine(line);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"[tui] Could not read log: {ex.Message}");
        }

        PromptText("Press Enter to continue", string.Empty);
    }

    private static void ShowSettings()
    {
        var settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BabelPlayer", "settings", "app-settings.json");

        Console.WriteLine();
        if (!File.Exists(settingsPath))
        {
            Console.WriteLine("No settings file yet (defaults apply on first run).");
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            foreach (var key in new[]
                     {
                         "TranscriptionProvider", "TranslationProvider", "TtsProvider",
                         "TargetLanguage", "DiarizationProvider", "TtsVoice",
                     })
            {
                if (document.RootElement.TryGetProperty(key, out var value))
                    Console.WriteLine($"  {key}: {value}");
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[tui] Could not read settings: {ex.Message}");
        }

        PromptText("Press Enter to continue", string.Empty);
    }

    private static bool Confirm(string prompt, bool defaultYes)
    {
        while (true)
        {
            string hint = defaultYes ? "Y/n" : "y/N";
            string input = PromptText($"{prompt} [{hint}]", string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(input))
                return defaultYes;
            if (input is "y" or "yes")
                return true;
            if (input is "n" or "no")
                return false;
            Console.WriteLine("Please answer y or n.");
        }
    }

    private static string PromptText(string prompt, string defaultValue)
    {
        if (!string.IsNullOrEmpty(defaultValue))
            Console.Write($"{prompt} [{defaultValue}]: ");
        else
            Console.Write($"{prompt}: ");

        string? input = Console.ReadLine();
        if (string.IsNullOrEmpty(input))
            return defaultValue;
        return input;
    }

    private static string Quote(string arg) =>
        arg.Contains(' ') ? $"\"{arg}\"" : arg;

    private static void PrintUsage()
    {
        Console.WriteLine();
        Console.WriteLine("Babel Player — Dub TUI (interactive headless pipeline)");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  BabelPlayer.exe --tui [--media <path>] [--lang <code>]");
        Console.WriteLine();
        Console.WriteLine("Menu-driven setup collects inputs, then runs the same pipeline engine");
        Console.WriteLine("as --dub. Numbered menus read stdin lines, so sessions are scriptable:");
        Console.WriteLine("  \"1\",\"clip.mp4\",\"en\",\"2\",\"n\",\"y\",\"\",\"y\",\"0\" | BabelPlayer.exe --tui");
        Console.WriteLine();
    }
}
