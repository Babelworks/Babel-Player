using System;
using System.IO;
using System.Text.Json;
using Babel.Player.Services;

namespace BabelPlayer.Tests;

public sealed class DubManifestTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"babel-manifest-tests-{Guid.NewGuid():N}");

    public DubManifestTests()
    {
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void BuildPath_UsesDubManifestSuffix()
    {
        Assert.Equal(
            Path.Combine(_dir, "clip-dub.manifest.json"),
            DubManifest.BuildPath(_dir, "clip"));
    }

    [Fact]
    public void Serialize_RecordsProvenanceFields()
    {
        var manifest = new DubExportManifest(
            Path.Combine(_dir, "clip.mp4"), "es", "faster-whisper", "nllb-200",
            "piper", "en_US-lessac-medium", true, true,
            Path.Combine(_dir, "clip-captions.srt"), Path.Combine(_dir, "clip-dub.mp3"),
            Path.Combine(_dir, "clip-dub.mp4"),
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, 12, 0);

        using var document = JsonDocument.Parse(DubManifest.Serialize(manifest));
        Assert.Equal("es", document.RootElement.GetProperty("TargetLanguage").GetString());
        Assert.Equal("piper", document.RootElement.GetProperty("TtsProvider").GetString());
        Assert.Equal(12, document.RootElement.GetProperty("SegmentCount").GetInt32());
        Assert.Equal(0, document.RootElement.GetProperty("ExitCode").GetInt32());
    }

    [Fact]
    public void Write_RoundTripsThroughFile()
    {
        var manifest = new DubExportManifest(
            Path.Combine(_dir, "clip.mp4"), "fr", "faster-whisper", "nllb-200",
            "chatterbox", "default", false, false,
            Path.Combine(_dir, "clip-captions.srt"), Path.Combine(_dir, "clip-dub.mp3"),
            null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, 2);

        string path = DubManifest.Write(_dir, manifest);

        Assert.Equal(DubManifest.BuildPath(_dir, "clip"), path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("chatterbox", document.RootElement.GetProperty("TtsProvider").GetString());
        Assert.False(document.RootElement.GetProperty("Mp4Exported").GetBoolean());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }
}
