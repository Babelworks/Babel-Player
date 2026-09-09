using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.SortFormer;
using Xunit;

namespace BabelPlayer.Tests;

public sealed class SortFormerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"babel-sortformer-{Guid.NewGuid():N}");

    public SortFormerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort cleanup */ }
    }

    [Theory]
    [InlineData("clip.mp4", ProviderNames.WeSpeakerLocal, true)]
    [InlineData("clip.mkv", ProviderNames.SortFormerLocal, true)]
    [InlineData("talk.mp3", ProviderNames.SortFormerLocal, true)]
    [InlineData("talk.m4a", ProviderNames.SortFormerLocal, true)]
    [InlineData("talk.wav", ProviderNames.SortFormerLocal, false)]
    [InlineData("talk.mp3", ProviderNames.WeSpeakerLocal, false)]
    [InlineData("talk.wav", ProviderNames.WeSpeakerLocal, false)]
    public void RequiresPcmWavExtractForDiarization_MatchesProviderNeeds(
        string path,
        string provider,
        bool expected)
    {
        Assert.Equal(
            expected,
            SessionWorkflowCoordinator.RequiresPcmWavExtractForDiarization(path, provider));
    }

    [Fact]
    public void ModelCatalog_PinsExpectedShaAndRelativePath()
    {
        Assert.Equal("sortformer-4spk", SortFormerModelCatalog.ModelId);
        Assert.Equal(Path.Combine("onnx", "model.onnx"), SortFormerModelCatalog.RelativeModelPath);
        Assert.Equal(
            "82b9c735e1cfc6b36b4ff8a994d9a0573e922d0e80a58a8553b2c58f7aff0c00",
            SortFormerModelCatalog.Sha256);
        Assert.Contains(SortFormerModelCatalog.RelativeModelPath, SortFormerModelCatalog.RequiredFiles);
    }

    [Fact]
    public void ModelFiles_ResolveFindsOnnxPackage()
    {
        var root = Path.Combine(_dir, "sortformer-4spk");
        var onnxDir = Path.Combine(root, "onnx");
        Directory.CreateDirectory(onnxDir);
        File.WriteAllBytes(Path.Combine(onnxDir, "model.onnx"), [1, 2, 3, 4]);

        var files = SortFormerModelFiles.Resolve(root);

        Assert.Equal(Path.GetFullPath(root), files.RootDirectory);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(root, SortFormerModelCatalog.RelativeModelPath)),
            Path.GetFullPath(files.ModelPath));
    }

    [Fact]
    public void ModelFiles_ResolveThrowsWhenModelMissing()
    {
        var root = Path.Combine(_dir, "incomplete");
        Directory.CreateDirectory(root);

        Assert.Throws<FileNotFoundException>(() => SortFormerModelFiles.Resolve(root));
    }

    [Fact]
    public void ModelFiles_TryVerifySha256_RejectsWrongBytes()
    {
        var path = Path.Combine(_dir, "bad.onnx");
        File.WriteAllBytes(path, [9, 9, 9]);

        Assert.False(SortFormerModelFiles.TryVerifySha256(path, out var actual));
        Assert.False(string.IsNullOrWhiteSpace(actual));
        Assert.NotEqual(SortFormerModelCatalog.Sha256, actual, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ModelDownloader_IsSortFormerModelDownloaded_RequiresShaMatch()
    {
        var root = Path.Combine(_dir, "downloaded");
        var onnxDir = Path.Combine(root, "onnx");
        Directory.CreateDirectory(onnxDir);
        File.WriteAllBytes(Path.Combine(onnxDir, "model.onnx"), [1, 2, 3]);

        Assert.False(ModelDownloader.IsSortFormerModelDownloaded(root));
    }

    [Fact]
    public void FeatureExtractor_EmptyAudio_ReturnsZeroFrames()
    {
        var extractor = new SortFormerFeatureExtractor();
        using var features = extractor.Extract(ReadOnlySpan<float>.Empty);

        Assert.Equal(0, features.FrameCount);
        Assert.Equal(SortFormerFeatureExtractor.MelBins, features.FeatureCount);
        Assert.Equal(0, features.Data.Length);
    }

    [Fact]
    public void FeatureExtractor_OneSecondSilence_ProducesExpectedFrameCount()
    {
        var samples = new float[SortFormerFeatureExtractor.SampleRate];
        var extractor = new SortFormerFeatureExtractor();
        using var features = extractor.Extract(samples);

        // padAmount = FftSize/2; paddedLength = samples + FftSize; frames = 1 + (paddedLength - FftSize) / HopLength
        const int padAmount = SortFormerFeatureExtractor.FftSize / 2;
        int paddedLength = samples.Length + (padAmount * 2);
        int expectedFrames = 1 + ((paddedLength - SortFormerFeatureExtractor.FftSize) / SortFormerFeatureExtractor.HopLength);

        Assert.Equal(expectedFrames, features.FrameCount);
        Assert.Equal(expectedFrames * SortFormerFeatureExtractor.MelBins, features.Data.Length);
        Assert.All(features.Data.ToArray(), value => Assert.True(float.IsFinite(value)));
    }

    [Theory]
    [InlineData("spk_0", "spk_00")]
    [InlineData("spk_1", "spk_01")]
    [InlineData("spk_03", "spk_03")]
    public void NormalizeSpeakerId_PadsTrackDubLabels(string raw, string expected)
    {
        Assert.Equal(expected, SortFormerDiarizationProvider.NormalizeSpeakerId(raw));
    }

    [Fact]
    public void NormalizeSpeakerId_ReusesAssignmentForSameRawKey()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var first = SortFormerDiarizationProvider.NormalizeSpeakerId("spk_0", map);
        var second = SortFormerDiarizationProvider.NormalizeSpeakerId("spk_0", map);

        Assert.Equal("spk_00", first);
        Assert.Equal(first, second);
        Assert.Single(map);
    }

    [Fact]
    public void CatalogSha256_MatchesComputedDigestOfPinnedConstantBytes()
    {
        // Guard against accidental hex corruption of the pinned digest string itself.
        var digestBytes = Convert.FromHexString(SortFormerModelCatalog.Sha256);
        Assert.Equal(32, digestBytes.Length);
        Assert.Equal(
            SortFormerModelCatalog.Sha256,
            Convert.ToHexString(digestBytes).ToLowerInvariant());
    }
}
