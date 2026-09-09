using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Babel.Player.Models;

namespace Babel.Player.Services;

public sealed record TranscriptChannelItem(
    string SegmentId,
    TranscriptSegmentArtifact Segment,
    string SourceLanguage,
    double LanguageProbability);

internal sealed record TranslationChannelItem(
    string SegmentId,
    TranslationSegmentArtifact Segment,
    string SourceLanguage,
    string TargetLanguage);

internal sealed record TtsChannelItem(
    string SegmentId,
    TranslationSegmentArtifact Segment,
    TtsResult Result);

public interface IStreamingTranscriptionProvider
{
    Task<TranscriptionResult> TranscribeStreamingAsync(
        TranscriptionRequest request,
        ChannelWriter<TranscriptChannelItem> writer,
        CancellationToken cancellationToken = default);
}

internal sealed class TranscriptChannelForwardingWriter(
    TranscriptArtifactStreamingWriter artifactWriter,
    ChannelWriter<TranscriptChannelItem> innerWriter) : ChannelWriter<TranscriptChannelItem>
{
    public override bool TryComplete(Exception? error = null) => innerWriter.TryComplete(error);

    public override bool TryWrite(TranscriptChannelItem item)
    {
        if (!innerWriter.TryWrite(item))
            return false;

        artifactWriter.TryAppend(item);
        return true;
    }

    public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) =>
        innerWriter.WaitToWriteAsync(cancellationToken);

    public override ValueTask WriteAsync(TranscriptChannelItem item, CancellationToken cancellationToken = default)
    {
        artifactWriter.TryAppend(item);
        return innerWriter.WriteAsync(item, cancellationToken);
    }
}

internal sealed record StreamingArtifactJournalPaths(
    string PartialPath,
    string PartialTempPath,
    string EventsPath,
    string CommitPath)
{
    public static StreamingArtifactJournalPaths FromFinalPath(string finalPath)
    {
        var dir = Path.GetDirectoryName(finalPath) ?? AppContext.BaseDirectory;
        var stem = Path.GetFileNameWithoutExtension(finalPath);
        var extension = Path.GetExtension(finalPath);
        var partialPath = Path.Combine(dir, $"{stem}.partial{extension}");
        return new StreamingArtifactJournalPaths(
            partialPath,
            $"{partialPath}.tmp",
            Path.Combine(dir, $"{stem}.events.jsonl"),
            Path.Combine(dir, $"{stem}.commit.json"));
    }

    public static StreamingArtifactJournalPaths FromPartialPath(string partialPath)
    {
        var fileName = Path.GetFileName(partialPath);
        const string partialJsonSuffix = ".partial.json";
        if (!fileName.EndsWith(partialJsonSuffix, StringComparison.OrdinalIgnoreCase))
            return FromFinalPath(partialPath);

        var dir = Path.GetDirectoryName(partialPath) ?? AppContext.BaseDirectory;
        var stem = fileName[..^partialJsonSuffix.Length];
        return new StreamingArtifactJournalPaths(
            partialPath,
            $"{partialPath}.tmp",
            Path.Combine(dir, $"{stem}.events.jsonl"),
            Path.Combine(dir, $"{stem}.commit.json"));
    }
}

internal sealed class TranscriptArtifactStreamingWriter
{
    private const int CompactEveryEventCount = 16;

    private static readonly JsonSerializerOptions JournalJsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly StreamingArtifactJournalPaths _paths;
    private readonly object _sync = new();
    private readonly Channel<Func<CancellationToken, Task>> _operations = Channel.CreateUnbounded<Func<CancellationToken, Task>>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    private TranscriptArtifact _artifact;
    private Task? _processorTask;
    private long _nextSequence;
    private int _eventsSinceCompaction;

    public TranscriptArtifactStreamingWriter(
        string partialPath,
        string sourceLanguage,
        double languageProbability)
    {
        _paths = StreamingArtifactJournalPaths.FromPartialPath(partialPath);
        _artifact = CreateEmptyArtifact(sourceLanguage, languageProbability);
    }

    public string PartialPath => _paths.PartialPath;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(_paths.PartialPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        RecoverOrResetState();
        await CompactAsync(cancellationToken).ConfigureAwait(false);
        _processorTask = Task.Run(ProcessOperationsAsync, CancellationToken.None);
    }

    public bool TryAppend(TranscriptChannelItem item)
    {
        TranscriptArtifact snapshot;
        string eventLine;
        bool shouldCompact;

        lock (_sync)
        {
            _artifact.Language = item.SourceLanguage;
            _artifact.LanguageProbability = item.LanguageProbability;
            _artifact.Segments ??= [];
            _artifact.Segments.Add(CloneTranscriptSegment(item.Segment));
            _nextSequence++;
            _eventsSinceCompaction++;
            eventLine = JsonSerializer.Serialize(new TranscriptJournalEvent
            {
                Sequence = _nextSequence,
                Type = "append_segment",
                SourceLanguage = item.SourceLanguage,
                LanguageProbability = item.LanguageProbability,
                Segment = CloneTranscriptSegment(item.Segment),
            }, JournalJsonOptions);
            snapshot = CloneArtifact(_artifact);
            shouldCompact = _eventsSinceCompaction >= CompactEveryEventCount;
        }

        EnqueueAppend(eventLine);
        if (shouldCompact)
            EnqueueCompact(snapshot, _nextSequence);

        return true;
    }

    public Task AppendAsync(TranscriptChannelItem item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TryAppend(item);
        return Task.CompletedTask;
    }

    public async Task CompleteAsync(
        TranscriptionResult result,
        string finalPath,
        CancellationToken cancellationToken)
    {
        TranscriptArtifact snapshot;
        lock (_sync)
        {
            _artifact.Language = result.Language;
            _artifact.LanguageProbability = result.LanguageProbability;
            _artifact.PeakRamMb = result.PeakRamMb;
            _artifact.PeakVramMb = result.PeakVramMb;
            snapshot = CloneArtifact(_artifact);
        }

        EnqueueCompact(snapshot, _nextSequence);
        _operations.Writer.TryComplete();
        if (_processorTask is not null)
            await _processorTask.ConfigureAwait(false);

        try
        {
            ArtifactRevisioning.BackupExisting(finalPath);
        }
        catch
        {
            // Backup failure should not prevent final artifact write
        }
        await ArtifactPersistence.AtomicWriteTextAsync(
                finalPath,
                ArtifactJson.SerializeTranscript(snapshot),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ReloadFromDiskAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RecoverOrResetState();
        await CompactAsync(cancellationToken).ConfigureAwait(false);
    }

    public static string GetPartialPath(string finalPath) =>
        StreamingArtifactJournalPaths.FromFinalPath(finalPath).PartialPath;

    private void RecoverOrResetState()
    {
        try
        {
            var lastCommittedSequence = ReadCommittedSequence(_paths.CommitPath);
            var recovered = lastCommittedSequence > 0 && File.Exists(_paths.PartialPath)
                ? ArtifactJson.DeserializeTranscript(File.ReadAllText(_paths.PartialPath), _paths.PartialPath)
                : CreateEmptyArtifact(_artifact.Language ?? "unknown", _artifact.LanguageProbability);
            var lastAppliedSequence = lastCommittedSequence;

            if (File.Exists(_paths.EventsPath))
            {
                if (lastCommittedSequence == 0)
                {
                    recovered = CreateEmptyArtifact(_artifact.Language ?? "unknown", _artifact.LanguageProbability);
                }

                foreach (var line in File.ReadLines(_paths.EventsPath))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    var evt = JsonSerializer.Deserialize<TranscriptJournalEvent>(line, JournalJsonOptions)
                        ?? throw new InvalidOperationException("Transcript journal line deserialized to null.");
                    if (evt.Sequence <= lastAppliedSequence)
                        continue;
                    if (!string.Equals(evt.Type, "append_segment", StringComparison.Ordinal))
                        throw new InvalidOperationException($"Unsupported transcript journal event '{evt.Type ?? "<null>"}'.");
                    if (evt.Segment is null)
                        throw new InvalidOperationException("Transcript journal event was missing a segment payload.");

                    recovered.Language = evt.SourceLanguage ?? recovered.Language;
                    recovered.LanguageProbability = evt.LanguageProbability ?? recovered.LanguageProbability;
                    recovered.Segments ??= [];
                    recovered.Segments.Add(CloneTranscriptSegment(evt.Segment));
                    lastAppliedSequence = evt.Sequence;
                }
            }

            lock (_sync)
            {
                _artifact = recovered;
                _nextSequence = lastAppliedSequence;
                _eventsSinceCompaction = 0;
            }
        }
        catch
        {
            QuarantineExistingJournalFiles();
            lock (_sync)
            {
                _artifact = CreateEmptyArtifact(_artifact.Language ?? "unknown", _artifact.LanguageProbability);
                _nextSequence = 0;
                _eventsSinceCompaction = 0;
            }
        }
    }

    private async Task ProcessOperationsAsync()
    {
        await foreach (var operation in _operations.Reader.ReadAllAsync().ConfigureAwait(false))
            await operation(CancellationToken.None).ConfigureAwait(false);
    }

    private void EnqueueAppend(string eventLine)
    {
        if (!_operations.Writer.TryWrite(static (ct) => Task.CompletedTask))
        {
            throw new InvalidOperationException("Transcript journal writer was not accepting new work.");
        }

        _operations.Reader.TryRead(out _);
        if (!_operations.Writer.TryWrite(ct => File.AppendAllTextAsync(_paths.EventsPath, eventLine + Environment.NewLine, ct)))
        {
            throw new InvalidOperationException("Transcript journal writer was not accepting append work.");
        }
    }

    private void EnqueueCompact(TranscriptArtifact snapshot, long committedSequence)
    {
        var json = ArtifactJson.SerializeTranscript(snapshot);
        if (!_operations.Writer.TryWrite(async ct =>
            {
                await File.WriteAllTextAsync(_paths.PartialTempPath, json, ct).ConfigureAwait(false);
                ArtifactPersistence.AtomicReplace(_paths.PartialTempPath, _paths.PartialPath);
                await ArtifactPersistence.AtomicWriteTextAsync(
                        _paths.CommitPath,
                        JsonSerializer.Serialize(new StreamingArtifactCommitState { CommittedSequence = committedSequence }, JournalJsonOptions),
                        ct)
                    .ConfigureAwait(false);
            }))
        {
            throw new InvalidOperationException("Transcript journal writer was not accepting compaction work.");
        }
    }

    private async Task CompactAsync(CancellationToken cancellationToken)
    {
        TranscriptArtifact snapshot;
        long committedSequence;
        lock (_sync)
        {
            snapshot = CloneArtifact(_artifact);
            committedSequence = _nextSequence;
            _eventsSinceCompaction = 0;
        }

        await File.WriteAllTextAsync(_paths.PartialTempPath, ArtifactJson.SerializeTranscript(snapshot), cancellationToken).ConfigureAwait(false);
        ArtifactPersistence.AtomicReplace(_paths.PartialTempPath, _paths.PartialPath);
        await ArtifactPersistence.AtomicWriteTextAsync(
                _paths.CommitPath,
                JsonSerializer.Serialize(new StreamingArtifactCommitState { CommittedSequence = committedSequence }, JournalJsonOptions),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static TranscriptArtifact CreateEmptyArtifact(string sourceLanguage, double languageProbability) =>
        new()
        {
            SchemaVersion = ArtifactJson.CurrentSchemaVersion,
            Language = sourceLanguage,
            LanguageProbability = languageProbability,
            Segments = [],
        };

    private static TranscriptArtifact CloneArtifact(TranscriptArtifact artifact) =>
        new()
        {
            SchemaVersion = artifact.SchemaVersion,
            Language = artifact.Language,
            LanguageProbability = artifact.LanguageProbability,
            PeakRamMb = artifact.PeakRamMb,
            PeakVramMb = artifact.PeakVramMb,
            Segments = artifact.Segments is null
                ? null
                : [.. artifact.Segments.Select(CloneTranscriptSegment)],
        };

    private static TranscriptSegmentArtifact CloneTranscriptSegment(TranscriptSegmentArtifact segment) =>
        new()
        {
            Start = segment.Start,
            End = segment.End,
            Text = segment.Text,
            SpeakerId = segment.SpeakerId,
            OriginalStart = segment.OriginalStart,
            Words = segment.Words is null ? null : [.. segment.Words],
        };

    private static long ReadCommittedSequence(string commitPath)
    {
        if (!File.Exists(commitPath))
            return 0;

        var state = JsonSerializer.Deserialize<StreamingArtifactCommitState>(File.ReadAllText(commitPath), JournalJsonOptions);
        return state?.CommittedSequence ?? 0;
    }

    private void QuarantineExistingJournalFiles()
    {
        var suffix = $".abandoned.{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
        MoveIfExists(_paths.PartialPath, suffix);
        MoveIfExists(_paths.PartialTempPath, suffix);
        MoveIfExists(_paths.EventsPath, suffix);
        MoveIfExists(_paths.CommitPath, suffix);
    }

    private static void MoveIfExists(string path, string suffix)
    {
        if (!File.Exists(path))
            return;

        var destination = path + suffix;
        ArtifactPersistence.TryDelete(destination);
        File.Move(path, destination);
    }

    private sealed class TranscriptJournalEvent
    {
        public long Sequence { get; set; }
        public string? Type { get; set; }
        public string? SourceLanguage { get; set; }
        public double? LanguageProbability { get; set; }
        public TranscriptSegmentArtifact? Segment { get; set; }
    }
}

internal sealed class TranslationArtifactStreamingWriter
{
    private const int CompactEveryEventCount = 16;

    private static readonly JsonSerializerOptions JournalJsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly StreamingArtifactJournalPaths _paths;
    private readonly object _sync = new();
    private readonly Channel<Func<CancellationToken, Task>> _operations = Channel.CreateUnbounded<Func<CancellationToken, Task>>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    private TranslationArtifact _artifact;
    private Task? _processorTask;
    private long _nextSequence;
    private int _eventsSinceCompaction;

    public TranslationArtifactStreamingWriter(
        string partialPath,
        string sourceLanguage,
        string targetLanguage)
    {
        _paths = StreamingArtifactJournalPaths.FromPartialPath(partialPath);
        _artifact = CreateEmptyArtifact(sourceLanguage, targetLanguage);
    }

    public string PartialPath => _paths.PartialPath;

    public IReadOnlyList<TranslationSegmentArtifact> OrderedSegments
    {
        get
        {
            lock (_sync)
            {
                return _artifact.Segments is null
                    ? []
                    : [.. _artifact.Segments.Select(StreamingArtifactCloneHelpers.CloneTranslationSegment)];
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(_paths.PartialPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        RecoverOrResetState();
        await CompactAsync(cancellationToken).ConfigureAwait(false);
        _processorTask = Task.Run(ProcessOperationsAsync, CancellationToken.None);
    }

public void ResetJournal()
{
    foreach (var path in new[] { _paths.PartialPath, _paths.PartialTempPath, _paths.EventsPath, _paths.CommitPath })
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}

    public Task AppendPendingSegmentAsync(
        TranscriptChannelItem item,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TranslationArtifact snapshot;
        string eventLine;
        bool shouldCompact;

        lock (_sync)
        {
            _artifact.SourceLanguage = item.SourceLanguage;
            _artifact.Segments ??= [];
            _artifact.Segments.Add(new TranslationSegmentArtifact
            {
                Id = item.SegmentId,
                Start = item.Segment.Start,
                End = item.Segment.End,
                Text = item.Segment.Text ?? string.Empty,
                TranslatedText = string.Empty,
                SpeakerId = item.Segment.SpeakerId,
            });
            _nextSequence++;
            _eventsSinceCompaction++;
            eventLine = JsonSerializer.Serialize(new TranslationJournalEvent
            {
                Sequence = _nextSequence,
                Type = "append_pending_segment",
                SourceLanguage = item.SourceLanguage,
                Segment = StreamingArtifactCloneHelpers.CloneTranslationSegment(_artifact.Segments[^1]),
            }, JournalJsonOptions);
            snapshot = CloneArtifact(_artifact);
            shouldCompact = _eventsSinceCompaction >= CompactEveryEventCount;
        }

        EnqueueAppend(eventLine);
        if (shouldCompact)
            EnqueueCompact(snapshot, _nextSequence);

        return Task.CompletedTask;
    }

    public int IndexOfSegment(string segmentId)
    {
        if (string.IsNullOrWhiteSpace(segmentId))
            return -1;

        lock (_sync)
        {
            var segments = _artifact.Segments;
            if (segments is null)
                return -1;

            for (var i = 0; i < segments.Count; i++)
            {
                if (string.Equals(segments[i].Id, segmentId, StringComparison.Ordinal))
                    return i;
            }

            return -1;
        }
    }

    public async Task<TranslationSegmentArtifact> ApplyTranslatedTextAsync(
        string segmentId,
        string translatedText,
        string? sourceLanguage,
        string? targetLanguage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TranslationArtifact snapshot;
        string eventLine;
        TranslationSegmentArtifact result;

        lock (_sync)
        {
            var segments = _artifact.Segments ?? throw new InvalidOperationException("No translation segments were queued for streaming translation.");
            var index = segments.FindIndex(segment => string.Equals(segment.Id, segmentId, StringComparison.Ordinal));
            if (index < 0)
                throw new InvalidOperationException($"Translated segment '{segmentId}' was not found in the streaming translation artifact.");

            _artifact.SourceLanguage = string.IsNullOrWhiteSpace(sourceLanguage) ? _artifact.SourceLanguage : sourceLanguage;
            _artifact.TargetLanguage = string.IsNullOrWhiteSpace(targetLanguage) ? _artifact.TargetLanguage : targetLanguage;
            segments[index].TranslatedText = translatedText;
            result = StreamingArtifactCloneHelpers.CloneTranslationSegment(segments[index]);
            _nextSequence++;
            _eventsSinceCompaction++;
            eventLine = JsonSerializer.Serialize(new TranslationJournalEvent
            {
                Sequence = _nextSequence,
                Type = "segment_translated",
                SourceLanguage = _artifact.SourceLanguage,
                TargetLanguage = _artifact.TargetLanguage,
                SegmentId = segmentId,
                TranslatedText = translatedText,
            }, JournalJsonOptions);
            snapshot = CloneArtifact(_artifact);
            _eventsSinceCompaction = 0;
        }

        EnqueueAppend(eventLine);
        EnqueueCompact(snapshot, _nextSequence);
        await EnqueueBarrier().WaitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task ReloadFromDiskAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RecoverOrResetState();
        await CompactAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteAsync(string finalPath, CancellationToken cancellationToken)
    {
        TranslationArtifact snapshot;
        lock (_sync)
        {
            snapshot = CloneArtifact(_artifact);
        }

        EnqueueCompact(snapshot, _nextSequence);
        _operations.Writer.TryComplete();
        if (_processorTask is not null)
            await _processorTask.ConfigureAwait(false);

        try
        {
            ArtifactRevisioning.BackupExisting(finalPath);
        }
        catch
        {
            // Backup failure should not prevent final artifact write
        }
        await ArtifactPersistence.AtomicWriteTextAsync(
                finalPath,
                ArtifactJson.SerializeTranslation(snapshot),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private void RecoverOrResetState()
    {
        try
        {
            var lastCommittedSequence = ReadCommittedSequence(_paths.CommitPath);
            var recovered = lastCommittedSequence > 0 && File.Exists(_paths.PartialPath)
                ? ArtifactJson.DeserializeTranslation(File.ReadAllText(_paths.PartialPath), _paths.PartialPath)
                : CreateEmptyArtifact(_artifact.SourceLanguage ?? "unknown", _artifact.TargetLanguage ?? string.Empty);
            var lastAppliedSequence = lastCommittedSequence;

            if (File.Exists(_paths.EventsPath))
            {
                if (lastCommittedSequence == 0)
                {
                    recovered = CreateEmptyArtifact(_artifact.SourceLanguage ?? "unknown", _artifact.TargetLanguage ?? string.Empty);
                }

                foreach (var line in File.ReadLines(_paths.EventsPath))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    var evt = JsonSerializer.Deserialize<TranslationJournalEvent>(line, JournalJsonOptions)
                        ?? throw new InvalidOperationException("Translation journal line deserialized to null.");
                    if (evt.Sequence <= lastAppliedSequence)
                        continue;

                    switch (evt.Type)
                    {
                        case "append_pending_segment":
                            if (evt.Segment is null)
                                throw new InvalidOperationException("Translation journal event was missing a segment payload.");
                            recovered.SourceLanguage = evt.SourceLanguage ?? recovered.SourceLanguage;
                            recovered.Segments ??= [];
                            recovered.Segments.Add(StreamingArtifactCloneHelpers.CloneTranslationSegment(evt.Segment));
                            break;
                        case "segment_translated":
                            if (recovered.Segments is null)
                                throw new InvalidOperationException("Translation journal applied a translated event before any segments existed.");
                            var matched = recovered.Segments.FirstOrDefault(segment => string.Equals(segment.Id, evt.SegmentId, StringComparison.Ordinal))
                                ?? throw new InvalidOperationException($"Translation journal segment '{evt.SegmentId ?? "<null>"}' was not found.");
                            recovered.SourceLanguage = evt.SourceLanguage ?? recovered.SourceLanguage;
                            recovered.TargetLanguage = evt.TargetLanguage ?? recovered.TargetLanguage;
                            matched.TranslatedText = evt.TranslatedText ?? string.Empty;
                            break;
                        default:
                            throw new InvalidOperationException($"Unsupported translation journal event '{evt.Type ?? "<null>"}'.");
                    }

                    lastAppliedSequence = evt.Sequence;
                }
            }

            lock (_sync)
            {
                _artifact = recovered;
                _nextSequence = lastAppliedSequence;
                _eventsSinceCompaction = 0;
            }
        }
        catch
        {
            QuarantineExistingJournalFiles();
            lock (_sync)
            {
                _artifact = CreateEmptyArtifact(_artifact.SourceLanguage ?? "unknown", _artifact.TargetLanguage ?? string.Empty);
                _nextSequence = 0;
                _eventsSinceCompaction = 0;
            }
        }
    }

    private async Task ProcessOperationsAsync()
    {
        await foreach (var operation in _operations.Reader.ReadAllAsync().ConfigureAwait(false))
            await operation(CancellationToken.None).ConfigureAwait(false);
    }

    private void EnqueueAppend(string eventLine)
    {
        if (!_operations.Writer.TryWrite(static (ct) => Task.CompletedTask))
        {
            throw new InvalidOperationException("Translation journal writer was not accepting new work.");
        }

        _operations.Reader.TryRead(out _);
        if (!_operations.Writer.TryWrite(ct => File.AppendAllTextAsync(_paths.EventsPath, eventLine + Environment.NewLine, ct)))
        {
            throw new InvalidOperationException("Translation journal writer was not accepting append work.");
        }
    }

    private void EnqueueCompact(TranslationArtifact snapshot, long committedSequence)
    {
        var json = ArtifactJson.SerializeTranslation(snapshot);
        if (!_operations.Writer.TryWrite(async ct =>
            {
                await File.WriteAllTextAsync(_paths.PartialTempPath, json, ct).ConfigureAwait(false);
                ArtifactPersistence.AtomicReplace(_paths.PartialTempPath, _paths.PartialPath);
                await ArtifactPersistence.AtomicWriteTextAsync(
                        _paths.CommitPath,
                        JsonSerializer.Serialize(new StreamingArtifactCommitState { CommittedSequence = committedSequence }, JournalJsonOptions),
                        ct)
                    .ConfigureAwait(false);
            }))
        {
            throw new InvalidOperationException("Translation journal writer was not accepting compaction work.");
        }
    }

    private Task EnqueueBarrier()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_operations.Writer.TryWrite(_ =>
            {
                tcs.TrySetResult();
                return Task.CompletedTask;
            }))
        {
            throw new InvalidOperationException("Translation journal writer was not accepting flush work.");
        }

        return tcs.Task;
    }

    private async Task CompactAsync(CancellationToken cancellationToken)
    {
        TranslationArtifact snapshot;
        long committedSequence;
        lock (_sync)
        {
            snapshot = CloneArtifact(_artifact);
            committedSequence = _nextSequence;
            _eventsSinceCompaction = 0;
        }

        await File.WriteAllTextAsync(_paths.PartialTempPath, ArtifactJson.SerializeTranslation(snapshot), cancellationToken).ConfigureAwait(false);
        ArtifactPersistence.AtomicReplace(_paths.PartialTempPath, _paths.PartialPath);
        await ArtifactPersistence.AtomicWriteTextAsync(
                _paths.CommitPath,
                JsonSerializer.Serialize(new StreamingArtifactCommitState { CommittedSequence = committedSequence }, JournalJsonOptions),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static TranslationArtifact CreateEmptyArtifact(string sourceLanguage, string targetLanguage) =>
        new()
        {
            SchemaVersion = ArtifactJson.CurrentSchemaVersion,
            SourceLanguage = sourceLanguage,
            TargetLanguage = targetLanguage,
            Segments = [],
        };

    private static TranslationArtifact CloneArtifact(TranslationArtifact artifact) =>
        new()
        {
            SchemaVersion = artifact.SchemaVersion,
            SourceLanguage = artifact.SourceLanguage,
            TargetLanguage = artifact.TargetLanguage,
            Segments = artifact.Segments is null
                ? null
                : [.. artifact.Segments.Select(StreamingArtifactCloneHelpers.CloneTranslationSegment)],
        };

    private static long ReadCommittedSequence(string commitPath)
    {
        if (!File.Exists(commitPath))
            return 0;

        var state = JsonSerializer.Deserialize<StreamingArtifactCommitState>(File.ReadAllText(commitPath), JournalJsonOptions);
        return state?.CommittedSequence ?? 0;
    }

    private void QuarantineExistingJournalFiles()
    {
        var suffix = $".abandoned.{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
        MoveIfExists(_paths.PartialPath, suffix);
        MoveIfExists(_paths.PartialTempPath, suffix);
        MoveIfExists(_paths.EventsPath, suffix);
        MoveIfExists(_paths.CommitPath, suffix);
    }

    private static void MoveIfExists(string path, string suffix)
    {
        if (!File.Exists(path))
            return;

        var destination = path + suffix;
        ArtifactPersistence.TryDelete(destination);
        File.Move(path, destination);
    }

    private sealed class TranslationJournalEvent
    {
        public long Sequence { get; set; }
        public string? Type { get; set; }
        public string? SourceLanguage { get; set; }
        public string? TargetLanguage { get; set; }
        public string? SegmentId { get; set; }
        public string? TranslatedText { get; set; }
        public TranslationSegmentArtifact? Segment { get; set; }
    }
}

internal sealed class StreamingArtifactCommitState
{
    public long CommittedSequence { get; set; }
}

internal static class StreamingArtifactCloneHelpers
{
    internal static TranslationSegmentArtifact CloneTranslationSegment(TranslationSegmentArtifact segment) =>
        new()
        {
            Id = segment.Id,
            Start = segment.Start,
            End = segment.End,
            Text = segment.Text,
            TranslatedText = segment.TranslatedText,
            SpeakerId = segment.SpeakerId,
        };
}
