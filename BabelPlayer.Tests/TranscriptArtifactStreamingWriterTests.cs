using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services;
using Xunit;

namespace BabelPlayer.Tests;

public sealed class TranscriptArtifactStreamingWriterTests : IDisposable
{
    private readonly string _dir;

    public TranscriptArtifactStreamingWriterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"babel-transcript-streaming-writer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort cleanup */ }
    }

    [Fact]
    public void ResetJournal_RemovesPartialEventsCommitAndTempFiles()
    {
        var partialPath = Path.Combine(_dir, "clip.partial.json");
        var paths = StreamingArtifactJournalPaths.FromPartialPath(partialPath);
        foreach (var path in new[] { paths.PartialPath, paths.PartialTempPath, paths.EventsPath, paths.CommitPath })
            File.WriteAllText(path, "stale");

        var writer = new TranscriptArtifactStreamingWriter(partialPath, "es", 0.99);
        writer.ResetJournal();

        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task InitializeWithoutReset_ReplaysPreviousRunIntoCommittedArtifact()
    {
        var partialPath = Path.Combine(_dir, "clip.partial.json");
        var finalPath = Path.Combine(_dir, "clip.json");
        var ct = CancellationToken.None;

        var first = new TranscriptArtifactStreamingWriter(partialPath, "es", 0.99);
        await first.InitializeAsync(ct);
        first.TryAppend(CreateItem(0.0, 8.2, "Hoy vamos"));
        first.TryAppend(CreateItem(8.6, 15.5, "Hola, soy Brenda"));
        await first.CompleteAsync(CreateResult(), finalPath, ct);

        var second = new TranscriptArtifactStreamingWriter(partialPath, "es", 0.99);
        await second.InitializeAsync(ct);
        second.TryAppend(CreateItem(0.0, 8.2, "Hoy vamos"));
        second.TryAppend(CreateItem(8.6, 15.5, "Hola, soy Brenda"));
        await second.CompleteAsync(CreateResult(), finalPath, ct);

        var artifact = await ArtifactJson.LoadTranscriptAsync(finalPath, ct);
        Assert.Equal(4, artifact.Segments!.Count);
    }

    [Fact]
    public async Task ResetJournal_BeforeInitialize_DoesNotCarryOverPreviousSegments()
    {
        var partialPath = Path.Combine(_dir, "clip.partial.json");
        var finalPath = Path.Combine(_dir, "clip.json");
        var ct = CancellationToken.None;

        var first = new TranscriptArtifactStreamingWriter(partialPath, "es", 0.99);
        await first.InitializeAsync(ct);
        first.TryAppend(CreateItem(0.0, 8.2, "Hoy vamos"));
        first.TryAppend(CreateItem(8.6, 15.5, "Hola, soy Brenda"));
        await first.CompleteAsync(CreateResult(), finalPath, ct);

        var second = new TranscriptArtifactStreamingWriter(partialPath, "es", 0.99);
        second.ResetJournal();
        await second.InitializeAsync(ct);
        second.TryAppend(CreateItem(0.0, 8.2, "Hoy vamos"));
        second.TryAppend(CreateItem(8.6, 15.5, "Hola, soy Brenda"));
        await second.CompleteAsync(CreateResult(), finalPath, ct);

        var artifact = await ArtifactJson.LoadTranscriptAsync(finalPath, ct);
        Assert.Equal(2, artifact.Segments!.Count);
        Assert.Equal("Hoy vamos", artifact.Segments[0].Text);
        Assert.Equal("Hola, soy Brenda", artifact.Segments[1].Text);
    }

    [Fact]
    public async Task ForwardingWriter_TryComplete_UnblocksTranscriptReader()
    {
        var partialPath = Path.Combine(_dir, "clip.partial.json");
        var artifactWriter = new TranscriptArtifactStreamingWriter(partialPath, "es", 0.99);
        await artifactWriter.InitializeAsync(CancellationToken.None);

        var channel = Channel.CreateUnbounded<TranscriptChannelItem>();
        var forwarding = new TranscriptChannelForwardingWriter(artifactWriter, channel.Writer);

        var readTask = Task.Run(async () =>
        {
            var count = 0;
            await foreach (var _ in channel.Reader.ReadAllAsync())
                count++;
            return count;
        });

        await forwarding.WriteAsync(CreateItem(0.0, 8.2, "Hoy vamos"));
        await forwarding.WriteAsync(CreateItem(8.6, 15.5, "Hola, soy Brenda"));
        Assert.True(forwarding.TryComplete());

        var count = await readTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, count);

        await artifactWriter.CompleteAsync(CreateResult(), Path.Combine(_dir, "clip.json"), CancellationToken.None);
    }

    private static TranscriptChannelItem CreateItem(double start, double end, string text) =>
        new(
            SessionWorkflowCoordinator.SegmentId(start),
            new TranscriptSegmentArtifact { Start = start, End = end, Text = text },
            "es",
            0.99);

    private static TranscriptionResult CreateResult() =>
        new(
            true,
            [],
            "es",
            0.99,
            null);
}
