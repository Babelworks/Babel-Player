using System;
using System.IO;
using System.Threading.Tasks;
using Babel.Player.Services;
using Xunit;

namespace BabelPlayer.Tests;

public sealed class TtsReferenceExtractorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"babel-tts-ref-{Guid.NewGuid():N}");

    public TtsReferenceExtractorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort cleanup */ }
    }

    [Fact]
    public async Task DeleteAsync_LeavesUntrackedFilesAlone()
    {
        var stray = Path.Combine(_dir, "keep.wav");
        await File.WriteAllTextAsync(stray, "x");
        var log = new AppLog(Path.Combine(_dir, "test.log"));
        await using var extractor = new TtsReferenceExtractor(log);

        await extractor.DeleteAsync(stray);

        Assert.True(File.Exists(stray));
    }

    [Fact]
    public async Task DeleteAsync_RemovesOnlyTheRequestedTrackedFile()
    {
        var first = Path.Combine(_dir, "a.wav");
        var second = Path.Combine(_dir, "b.wav");
        await File.WriteAllTextAsync(first, "a");
        await File.WriteAllTextAsync(second, "b");
        var log = new AppLog(Path.Combine(_dir, "test.log"));
        await using var extractor = new TtsReferenceExtractor(log);
        Assert.True(extractor.TryTrackTempFile(first));
        Assert.True(extractor.TryTrackTempFile(second));

        await extractor.DeleteAsync(first);

        Assert.False(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public async Task Dispose_DeletesRemainingTrackedFiles()
    {
        var leftover = Path.Combine(_dir, "leftover.wav");
        await File.WriteAllTextAsync(leftover, "x");
        var log = new AppLog(Path.Combine(_dir, "test.log"));
        await using (var extractor = new TtsReferenceExtractor(log))
        {
            Assert.True(extractor.TryTrackTempFile(leftover));
        }

        Assert.False(File.Exists(leftover));
    }
}
