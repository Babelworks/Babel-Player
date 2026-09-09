using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Models.LanguageSupport;
using Babel.Player.Services;

namespace BabelPlayer.Tests;

/// <summary>
/// Small deterministic seam tests for <see cref="DubTui"/>: argument validation
/// via faked stdin/stdout plus the pure helpers behind the review fixes
/// (language catalog single-sourcing, audio-only MP4 default, argv building,
/// clone-consent resolution). No pipeline execution, no sleeps.
/// </summary>
[Collection("Environment")]
public sealed class DubTuiTests
{
    private static async Task<(int ExitCode, string Out, string Err)> RunWithStdIoAsync(
        string[] args, string stdin, CancellationToken cancellationToken = default)
    {
        var prevIn = Console.In;
        var prevOut = Console.Out;
        var prevErr = Console.Error;
        try
        {
            using var reader = new StringReader(stdin);
            using var outWriter = new StringWriter();
            using var errWriter = new StringWriter();
            Console.SetIn(reader);
            Console.SetOut(outWriter);
            Console.SetError(errWriter);
            int code = await DubTui.RunAsync(args, cancellationToken);
            return (code, outWriter.ToString(), errWriter.ToString());
        }
        finally
        {
            Console.SetIn(prevIn);
            Console.SetOut(prevOut);
            Console.SetError(prevErr);
        }
    }

    [Fact]
    public async Task Help_ReturnsZeroAndPrintsUsage()
    {
        var (code, output, _) = await RunWithStdIoAsync(["--tui", "--help"], string.Empty);
        Assert.Equal(0, code);
        Assert.Contains("--tui", output);
    }

    [Fact]
    public async Task UnknownFlag_ReturnsArgumentError()
    {
        var (code, _, _) = await RunWithStdIoAsync(["--tui", "--bogus-flag"], string.Empty);
        Assert.Equal(1, code);
    }

    [Fact]
    public async Task MissingPresetMedia_ReturnsArgumentError()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"babel-no-such-{Guid.NewGuid():N}.mp4");
        var (code, _, _) = await RunWithStdIoAsync(["--tui", "--media", missing], string.Empty);
        Assert.Equal(1, code);
    }

    [Fact]
    public async Task QuitChoice_ReturnsZero()
    {
        var (code, _, _) = await RunWithStdIoAsync(["--tui"], "0\n");
        Assert.Equal(0, code);
    }

    [Fact]
    public void TargetLanguages_MatchNllbCatalogSingleSource()
    {
        Assert.Equal(
            NllbLanguageCatalog.IsoCodes.OrderBy(c => c, StringComparer.Ordinal).ToList(),
            DubTui.TargetLanguages.OrderBy(c => c, StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void IsSupportedTargetLanguage_AcceptsCatalogCodesOnly()
    {
        Assert.True(DubTui.IsSupportedTargetLanguage("en"));
        Assert.True(DubTui.IsSupportedTargetLanguage("FR"));
        Assert.False(DubTui.IsSupportedTargetLanguage("xx"));
        Assert.False(DubTui.IsSupportedTargetLanguage(string.Empty));
    }

    [Fact]
    public void IsAudioOnlyMedia_SplitsAudioFromVideoExtensions()
    {
        Assert.True(DubTui.IsAudioOnlyMedia("clip.mp3"));
        Assert.True(DubTui.IsAudioOnlyMedia("clip.WAV"));
        Assert.False(DubTui.IsAudioOnlyMedia("clip.mp4"));
        Assert.False(DubTui.IsAudioOnlyMedia("clip.mkv"));
    }

    [Fact]
    public void BuildDubArgv_MirrorsDubCliFlags()
    {
        var argv = DubTui.BuildDubArgv("clip.mp4", "es", ProviderNames.Piper, "my-voice", true, true, null, false);
        Assert.Equal(["--dub", "--media", "clip.mp4", "--lang", "es", "--tts", ProviderNames.Piper, "--voice", "my-voice"], argv);

        var audioArgv = DubTui.BuildDubArgv("clip.mp3", "es", string.Empty, "  ", false, false, "C:\\out", true);
        Assert.DoesNotContain("--tts", audioArgv);
        Assert.DoesNotContain("--voice", audioArgv);
        Assert.Contains("--no-diarization", audioArgv);
        Assert.Contains("--no-mp4", audioArgv);
        Assert.Contains("--consent-clone", audioArgv);
        Assert.Contains("C:\\out", audioArgv);
    }

    [Fact]
    public async Task InvalidPresetLang_ReturnsArgumentError()
    {
        var (code, _, _) = await RunWithStdIoAsync(["--tui", "--lang", "xx"], string.Empty);
        Assert.Equal(1, code);
    }

    [Fact]
    public async Task CancelledToken_ReturnsCancelledExitCode()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var (code, _, _) = await RunWithStdIoAsync(["--tui"], "0\n", cts.Token);
        Assert.Equal(130, code);
    }

    [Fact]
    public void ReadSettingsValue_FallsBackForUnknownKey()
    {
        Assert.Equal("fb", DubTui.ReadSettingsValue("NoSuchSettingKey", "fb"));
    }

    [Fact]
    public async Task Wizard_BuildsArgvAndPropagatesEngineExitCode()
    {
        var media = Path.Combine(Path.GetTempPath(), $"babel-tui-{Guid.NewGuid():N}.mp4");
        File.WriteAllText(media, string.Empty);
        string[]? captured = null;
        var previous = DubTui.RunPipelineEngine;
        try
        {
            DubTui.RunPipelineEngine = (argv, _) =>
            {
                captured = argv;
                return Task.FromResult(0);
            };
            var (code, output, _) = await RunWithStdIoAsync(
                ["--tui", "--media", media, "--lang", "es"],
                "1\n2\n\nn\ny\n\n");
            Assert.Equal(0, code);
            Assert.NotNull(captured);
            Assert.Contains("--dub", captured);
            Assert.Contains(media, captured);
            Assert.Contains("es", captured);
            Assert.Contains(ProviderNames.Piper, captured);
            Assert.Contains("--no-diarization", captured);
            Assert.DoesNotContain("--no-mp4", captured);
            Assert.Contains("transcription", output);
        }
        finally
        {
            DubTui.RunPipelineEngine = previous;
            File.Delete(media);
        }
    }

    [Fact]
    public async Task Wizard_PropagatesEngineFailure()
    {
        var media = Path.Combine(Path.GetTempPath(), $"babel-tui-{Guid.NewGuid():N}.mp4");
        File.WriteAllText(media, string.Empty);
        var previous = DubTui.RunPipelineEngine;
        try
        {
            DubTui.RunPipelineEngine = (_, _) => Task.FromResult(2);
            var (code, _, _) = await RunWithStdIoAsync(
                ["--tui", "--media", media, "--lang", "es"],
                "1\n2\n\nn\ny\n\n");
            Assert.Equal(2, code);
        }
        finally
        {
            DubTui.RunPipelineEngine = previous;
            File.Delete(media);
        }
    }

    [Fact]
    public void RequiresCloneConsent_FollowsEffectiveProvider()
    {
        Assert.True(DubTui.RequiresCloneConsent(ProviderNames.Chatterbox));
        Assert.True(DubTui.RequiresCloneConsent("Chatterbox"));
        Assert.False(DubTui.RequiresCloneConsent(ProviderNames.Piper));
        Assert.False(DubTui.RequiresCloneConsent(ProviderNames.EdgeTts));
    }

    [Fact]
    public void ResolveEffectiveTtsProvider_NormalizesExplicitChoice()
    {
        Assert.Equal(ProviderNames.Piper, DubTui.ResolveEffectiveTtsProvider("PIPER"));
        Assert.Equal(ProviderNames.Chatterbox, DubTui.ResolveEffectiveTtsProvider(" chatterbox "));
    }
}
