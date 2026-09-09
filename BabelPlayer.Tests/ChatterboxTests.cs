using System;
using System.IO;
using System.Linq;
using System.Text;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Chatterbox;
using Xunit;

namespace BabelPlayer.Tests;

public sealed class ChatterboxTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"babel-chatterbox-{Guid.NewGuid():N}");

    public ChatterboxTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort cleanup */ }
    }

    [Fact]
    public void ModelFiles_ResolveFindsGraphsAndDetectsMultilingual()
    {
        var root = Path.Combine(_dir, "chatterbox-multilingual-ONNX");
        var onnxDir = Path.Combine(root, "onnx");
        Directory.CreateDirectory(onnxDir);
        foreach (var name in new[] { "speech_encoder.onnx", "embed_tokens.onnx", "language_model.onnx", "conditional_decoder.onnx" })
            File.WriteAllText(Path.Combine(onnxDir, name), "fake");
        File.WriteAllText(Path.Combine(root, "tokenizer.json"), "{}");

        var files = ChatterboxModelFiles.Resolve(root);

        Assert.Equal(Path.Combine(onnxDir, "language_model.onnx"), files.LanguageModelPath);
        Assert.False(files.IsTurbo);
        Assert.True(files.IsMultilingual);
    }

    [Fact]
    public void ModelFiles_ResolveThrowsWhenGraphMissing()
    {
        var root = Path.Combine(_dir, "incomplete");
        Directory.CreateDirectory(Path.Combine(root, "onnx"));
        File.WriteAllText(Path.Combine(root, "tokenizer.json"), "{}");

        Assert.Throws<FileNotFoundException>(() => ChatterboxModelFiles.Resolve(root));
    }

    [Fact]
    public void ApplyMultilingualLanguagePrefix_PassesThroughNonMultilingual()
    {
        Assert.Equal("Bonjour", ChatterboxTtsEngine.ApplyMultilingualLanguagePrefix("Bonjour", "fr", false));
    }

    [Fact]
    public void ApplyMultilingualLanguagePrefix_PrependsLanguageToken()
    {
        Assert.Equal("[fr]Bonjour", ChatterboxTtsEngine.ApplyMultilingualLanguagePrefix("Bonjour", "fr", true));
        Assert.Equal("[fr]Bonjour", ChatterboxTtsEngine.ApplyMultilingualLanguagePrefix("Bonjour", "FR", true));
    }

    [Fact]
    public void ApplyMultilingualLanguagePrefix_RejectsUnsupportedLanguage()
    {
        Assert.Throws<NotSupportedException>(() =>
            ChatterboxTtsEngine.ApplyMultilingualLanguagePrefix("Hello", "zh", true));
    }

    [Fact]
    public void BuildTextInputIds_FramesTurboAndBase()
    {
        var turbo = ChatterboxTtsEngine.BuildTextInputIds(new long[] { 10, 20 }, true);
        Assert.Equal(new long[] { 10, 20, 50256, 50256 }, turbo);

        var @base = ChatterboxTtsEngine.BuildTextInputIds(new long[] { 10, 20 }, false);
        Assert.Equal(new long[] { 6563, 255, 10, 20, 0, 6561, 6561 }, @base);
    }

    [Fact]
    public void ResolveMaxNewTokens_DefaultsAndBudgets()
    {
        Assert.Equal(1000, ChatterboxTtsEngine.ResolveMaxNewTokens(null));
        Assert.Equal(1000, ChatterboxTtsEngine.ResolveMaxNewTokens(0));
        var budgeted = ChatterboxTtsEngine.ResolveMaxNewTokens(4.0);
        Assert.InRange(budgeted, 128, 1000);
    }

    [Fact]
    public void ResolveOnnxIntraOpNumThreads_UsesAtLeastOneCore()
    {
        Assert.InRange(ChatterboxTtsEngine.ResolveOnnxIntraOpNumThreads(), 1, int.MaxValue);
    }

    [Fact]
    public void MapPresentOutputToPastInputName_HandlesExportVariants()
    {
        Assert.Equal(
            "past_key_values.0.key",
            ChatterboxTtsEngine.MapLanguageModelPresentOutputToPastInputName("present_key_values.0.key"));
        Assert.Equal(
            "past_key_values.0.key",
            ChatterboxTtsEngine.MapLanguageModelPresentOutputToPastInputName("present.0.key"));
    }

    [Fact]
    public void AudioEncodeDecode_RoundTripsMonoPcm16()
    {
        var samples = new float[] { 0f, 0.5f, -0.5f, 1f, -1f };
        var bytes = ChatterboxAudio.EncodeMonoPcm16(samples, 24000);

        var (decoded, rate) = ChatterboxAudio.DecodePcm16Mono(bytes);

        Assert.Equal(24000, rate);
        Assert.Equal(samples.Length, decoded.Length);
        for (int index = 0; index < samples.Length; index++)
            Assert.Equal(samples[index], decoded[index], precision: 3);
    }

    [Fact]
    public void AudioResampleLinear_UpsamplesAndPreservesEndpoints()
    {
        var samples = new float[] { 0f, 1f };
        var upsampled = ChatterboxAudio.ResampleLinear(samples, 1, 3);

        Assert.Equal(6, upsampled.Length);
        Assert.Equal(0f, upsampled[0], precision: 5);
        Assert.Equal(1f, upsampled[5], precision: 5);
    }

    [Fact]
    public void ModelCatalog_CoversExpectedLanguages()
    {
        Assert.NotEmpty(ChatterboxModelCatalog.RequiredFiles);
        Assert.Contains("onnx/language_model.onnx", ChatterboxModelCatalog.RequiredFiles);
        Assert.Contains("fr", ChatterboxModelCatalog.SupportedLanguages);
        Assert.DoesNotContain("zh", ChatterboxModelCatalog.SupportedLanguages);
    }

    [Fact]
    public void ModelCatalog_DownloadsPinnedHuggingFaceRevision()
    {
        var url = ChatterboxModelCatalog.ModelDownloadUrl("tokenizer.json");

        Assert.Equal("chatterbox-multilingual", ChatterboxModelCatalog.ModelId);
        Assert.Equal("onnx-community/chatterbox-multilingual-ONNX", ChatterboxModelCatalog.RepositoryId);
        Assert.Equal(
            $"https://huggingface.co/{ChatterboxModelCatalog.RepositoryId}/resolve/{ChatterboxModelCatalog.Revision}/tokenizer.json",
            url);
    }

    [Fact]
    public void IsChatterboxModelDownloaded_RejectsZeroByteFiles()
    {
        var modelDir = Path.Combine(_dir, "chatterbox-partial");
        foreach (var relativePath in ChatterboxModelCatalog.RequiredFiles)
        {
            var fullPath = Path.Combine(modelDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, "x");
        }

        Assert.True(ModelDownloader.IsChatterboxModelDownloaded(modelDir));

        var first = Path.Combine(modelDir, ChatterboxModelCatalog.RequiredFiles[0]);
        File.WriteAllBytes(first, []);
        Assert.False(ModelDownloader.IsChatterboxModelDownloaded(modelDir));
    }

    [Fact]
    public void DecodePcm16Mono_RejectsZeroChannelHeader()
    {
        var bytes = BuildWavBytes(channels: 0, frames: 4);
        Assert.Throws<InvalidDataException>(() => ChatterboxAudio.DecodePcm16Mono(bytes));
    }

    [Fact]
    public void DecodePcm16Mono_SkipsMetadataChunks()
    {
        var bytes = BuildWavBytes(channels: 1, frames: 4, extraChunk: true);
        var (samples, rate) = ChatterboxAudio.DecodePcm16Mono(bytes);

        Assert.Equal(24000, rate);
        Assert.Equal(4, samples.Length);
        Assert.Equal(1000 / (float)short.MaxValue, samples[1], precision: 5);
    }

    [Fact]
    public void NormalizeTextForLanguage_DecomposesKoreanJamo()
    {
        Assert.Equal(string.Empty, ChatterboxTtsEngine.NormalizeTextForLanguage("　", "ko"));
        Assert.Equal("\u1100\u1161", ChatterboxTtsEngine.NormalizeTextForLanguage("가", "ko"));
        Assert.Equal("\u1100\u1161", ChatterboxTtsEngine.NormalizeTextForLanguage("가", "KO"));
        Assert.Equal("hello", ChatterboxTtsEngine.NormalizeTextForLanguage("hello", "ko"));
        Assert.Equal("Bonjour", ChatterboxTtsEngine.NormalizeTextForLanguage("Bonjour", "fr"));
    }

    [Fact]
    public void NormalizePromptText_CleansPunctuationAndAddsStop()
    {
        Assert.Equal("You need to add some text for me to talk.", ChatterboxSampling.NormalizePromptText("  "));
        Assert.Equal("Hello world.", ChatterboxSampling.NormalizePromptText("hello world"));
        Assert.Equal("Hello, world.", ChatterboxSampling.NormalizePromptText("Hello: world"));
        Assert.Equal("Already done.", ChatterboxSampling.NormalizePromptText("Already done."));
    }

    [Fact]
    public void ApplyCfg_BlendsConditionalAndUnconditionalLogits()
    {
        float[] destination = new float[2];
        ChatterboxSampling.ApplyCfg([1f, 2f], [0f, 1f], 0.5f, destination);
        Assert.Equal(1.5f, destination[0], precision: 5);
        Assert.Equal(2.5f, destination[1], precision: 5);
    }

    [Fact]
    public void SampleNextToken_StopsAfterRepeatedPhonemes()
    {
        var generated = Enumerable.Repeat(42L, ChatterboxSampling.ConsecutiveRepeatLimit).ToArray();
        long token = ChatterboxSampling.SampleNextToken(
            [0.1f, 0.2f, 0.3f],
            [],
            generated,
            stopSpeechToken: 6562,
            randomValue: 0.1d);
        Assert.Equal(6562, token);
    }

    [Fact]
    public void SampleNextToken_ArgMaxWhenTemperatureIsZero()
    {
        long token = ChatterboxSampling.SampleNextToken(
            [0.1f, 5f, 0.2f],
            [0.1f, 0.1f, 0.1f],
            [6561L],
            stopSpeechToken: 6562,
            randomValue: 0.0d,
            temperature: 0f);
        Assert.Equal(1, token);
    }

    [Fact]
    public void CopyLastLogits_ReadsLastStepPerBatch()
    {
        // [batch=2, seq=2, vocab=3]
        float[] values =
        [
            1f, 2f, 3f,
            4f, 5f, 6f,
            7f, 8f, 9f,
            10f, 11f, 12f,
        ];
        float[] cond = new float[3];
        float[] uncond = new float[3];
        ChatterboxSampling.CopyLastLogits(values, [2, 2, 3], 0, cond);
        ChatterboxSampling.CopyLastLogits(values, [2, 2, 3], 1, uncond);
        Assert.Equal(new float[] { 4f, 5f, 6f }, cond);
        Assert.Equal(new float[] { 10f, 11f, 12f }, uncond);
    }

    [Fact]
    public void TruncateReferenceAudio_KeepsAtMostTenSeconds()
    {
        var samples = new float[24000 * 15];
        samples[0] = 0.5f;
        var truncated = ChatterboxAudio.TruncateReferenceAudio(samples, 24000);
        Assert.Equal(24000 * 10, truncated.Length);
        Assert.Equal(0.5f, truncated[0]);
    }

    [Fact]
    public void CountTrailingRepeats_CountsRunAtEnd()
    {
        Assert.Equal(3, ChatterboxSampling.CountTrailingRepeats([1, 2, 9, 9, 9]));
        Assert.Equal(1, ChatterboxSampling.CountTrailingRepeats([7]));
        Assert.Equal(0, ChatterboxSampling.CountTrailingRepeats([]));
    }

    [Fact]
    public void ModelFiles_DetectsMultilingualFromTokenizerContent()
    {
        var multiRoot = Path.Combine(_dir, "neutral-model-name");
        WriteFakeModel(multiRoot, withLanguageToken: true);
        Assert.True(ChatterboxModelFiles.Resolve(multiRoot).IsMultilingual);

        var singleRoot = Path.Combine(_dir, "other-neutral-name");
        WriteFakeModel(singleRoot, withLanguageToken: false);
        Assert.False(ChatterboxModelFiles.Resolve(singleRoot).IsMultilingual);
    }

    [Fact]
    public async Task Encode_UsesGraphemeSpaceAndLanguageTokens()
    {
        var root = Path.Combine(_dir, "grapheme-tokenizer");
        WriteGraphemeTokenizer(root);
        var tokenizer = await ChatterboxTokenizer.LoadAsync(Path.Combine(root, "tokenizer.json"));

        var ids = tokenizer.Encode("[en]a b");

        Assert.Equal(new long[] { 6, 3, 2, 4 }, ids);
    }

    [Fact]
    public async Task Encode_MatchesOfficialMultilingualIdsWhenModelIsInstalled()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BabelPlayer",
            "models",
            "chatterbox-multilingual",
            "tokenizer.json");
        if (!File.Exists(path))
            return;

        var tokenizer = await ChatterboxTokenizer.LoadAsync(path);
        var ids = tokenizer.Encode("[en]Today we're going to do an audio understanding exercise.");
        Assert.Equal(
            new long[]
            {
                708, 296, 207, 88, 2, 100, 4, 46, 2, 119, 52, 2, 51, 2, 134, 2, 43, 2,
                14, 34, 17, 22, 28, 2, 97, 234, 63, 53, 52, 2, 169, 44, 16, 54, 18, 9,
            },
            ids);
    }

    private static void WriteFakeModel(string root, bool withLanguageToken)
    {
        var onnxDir = Path.Combine(root, "onnx");
        Directory.CreateDirectory(onnxDir);
        foreach (var name in new[] { "speech_encoder.onnx", "embed_tokens.onnx", "language_model.onnx", "conditional_decoder.onnx" })
            File.WriteAllText(Path.Combine(onnxDir, name), "fake");
        var vocabEntry = withLanguageToken ? "\"[fr]\": 100, " : string.Empty;
        File.WriteAllText(
            Path.Combine(root, "tokenizer.json"),
            "{\"model\":{\"vocab\":{" + vocabEntry + "\"hello\": 200},\"merges\":[]}}");
    }

    private static void WriteGraphemeTokenizer(string root)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "tokenizer.json"),
            """
            {
              "added_tokens": [
                {"id": 0, "content": "[STOP]", "special": true},
                {"id": 1, "content": "[UNK]", "special": true},
                {"id": 2, "content": "[SPACE]", "special": true},
                {"id": 6, "content": "[en]", "special": false}
              ],
              "model": {
                "vocab": {
                  "[STOP]": 0,
                  "[UNK]": 1,
                  "[SPACE]": 2,
                  "[en]": 6,
                  "a": 3,
                  "b": 4
                },
                "merges": []
              }
            }
            """);
    }

    private static byte[] BuildWavBytes(int channels, int frames, bool extraChunk = false)
    {
        int safeChannels = Math.Max(1, channels);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(0);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(24000);
        writer.Write(24000 * safeChannels * 2);
        writer.Write((short)(safeChannels * 2));
        writer.Write((short)16);
        if (extraChunk)
        {
            writer.Write(Encoding.ASCII.GetBytes("LIST"));
            writer.Write(4);
            writer.Write(new byte[] { (byte)'I', (byte)'N', (byte)'F', (byte)'O' });
        }
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(frames * safeChannels * 2);
        for (int i = 0; i < frames * safeChannels; i++)
            writer.Write((short)(i * 1000));
        writer.Flush();
        return stream.ToArray();
    }
}
