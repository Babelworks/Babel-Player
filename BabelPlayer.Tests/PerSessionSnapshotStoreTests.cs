using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Babel.Player.Models;
using Babel.Player.Services;

namespace BabelPlayer.Tests;

/// <summary>
/// Tests for PerSessionSnapshotStore — per-session save, load, LoadAll, and graceful degradation.
/// </summary>
public sealed class PerSessionSnapshotStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _sessionsRoot;
    private readonly AppLog _log;
    private readonly PerSessionSnapshotStore _store;

    public PerSessionSnapshotStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"babel-per-session-tests-{Guid.NewGuid():N}");
        _sessionsRoot = Path.Combine(_dir, "sessions");
        _log = new AppLog(Path.Combine(_dir, "test.log"));
        _store = new PerSessionSnapshotStore(_sessionsRoot, _log);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private static string[] FindCorruptBackups(string sessionDir) =>
        Directory.GetFiles(sessionDir, "snapshot.json.corrupt.*", SearchOption.TopDirectoryOnly);

    // ── Constructor ────────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_CreatesSessionsRootDirectory()
    {
        Assert.True(Directory.Exists(_sessionsRoot));
    }

    // ── Save / Load ────────────────────────────────────────────────────────────

    [Fact]
    public void Save_CreatesSnapshotFileUnderSessionDirectory()
    {
        var snapshot = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow);
        _store.Save(snapshot);

        var expectedPath = Path.Combine(_sessionsRoot, snapshot.SessionId.ToString(), "snapshot.json");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void Load_MissingSession_ReturnsNull()
    {
        var result = _store.Load(Guid.NewGuid());
        Assert.Null(result);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips_SessionId()
    {
        var snapshot = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow);
        _store.Save(snapshot);
        var loaded = _store.Load(snapshot.SessionId);
        Assert.NotNull(loaded);
        Assert.Equal(snapshot.SessionId, loaded.SessionId);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips_Stage()
    {
        var snapshot = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow)
            with
            {
                Stage = SessionWorkflowStage.Diarized,
                DiarizationProvider = ProviderNames.NemoLocal,
                SpeakersDetectedAtUtc = DateTimeOffset.UtcNow,
            };
        _store.Save(snapshot);
        var loaded = _store.Load(snapshot.SessionId)!;
        Assert.Equal(SessionWorkflowStage.Diarized, loaded.Stage);
    }

    [Fact]
    public void Save_WritesStageAsStringName()
    {
        var snapshot = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow)
            with
            {
                Stage = SessionWorkflowStage.Diarized,
                DiarizationProvider = ProviderNames.NemoLocal,
                SpeakersDetectedAtUtc = DateTimeOffset.UtcNow,
            };

        _store.Save(snapshot);
        var path = Path.Combine(_sessionsRoot, snapshot.SessionId.ToString(), "snapshot.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));

        Assert.Equal("Diarized", doc.RootElement.GetProperty("Stage").GetString());
    }

    [Fact]
    public void Load_LegacyNumericStageValue_IsAccepted()
    {
        var sessionId = Guid.NewGuid();
        var sessionDir = Path.Combine(_sessionsRoot, sessionId.ToString());
        Directory.CreateDirectory(sessionDir);
        var now = DateTimeOffset.Parse("2025-01-15T12:00:00Z");
        File.WriteAllText(
            Path.Combine(sessionDir, "snapshot.json"),
            $$"""
              {
                "SessionId": "{{sessionId}}",
                "Stage": 3,
                "CreatedAtUtc": "{{now:O}}",
                "LastUpdatedAtUtc": "{{now:O}}",
                "StatusMessage": "legacy"
              }
              """);

        var loaded = _store.Load(sessionId);

        Assert.NotNull(loaded);
        Assert.Equal(SessionWorkflowStage.Translated, loaded!.Stage);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips_NullableFields()
    {
        var snapshot = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            SourceMediaPath = "/video/input.mp4",
            TranscriptPath = "/artifacts/transcript.json",
            SourceLanguage = "es",
            TargetLanguage = "en",
            TtsVoice = "en-US-AriaNeural",
            SpeakerVoiceAssignments = new() { ["spk_01"] = "en-US-AriaNeural" },
            SpeakerReferenceAudioPaths = new() { ["spk_01"] = "/artifacts/speakers/spk_01.wav" },
            MultiSpeakerEnabled = true,
            DefaultTtsVoiceFallback = "en-US-AriaNeural",
            DiarizationProvider = "speaker-diarization-local",
        };
        _store.Save(snapshot);
        var loaded = _store.Load(snapshot.SessionId)!;

        Assert.Equal(snapshot.SourceMediaPath, loaded.SourceMediaPath);
        Assert.Equal(snapshot.TranscriptPath, loaded.TranscriptPath);
        Assert.Equal(snapshot.SourceLanguage, loaded.SourceLanguage);
        Assert.Equal(snapshot.TargetLanguage, loaded.TargetLanguage);
        Assert.Equal(snapshot.TtsVoice, loaded.TtsVoice);
        Assert.Equal(snapshot.SpeakerVoiceAssignments, loaded.SpeakerVoiceAssignments);
        Assert.Equal(snapshot.SpeakerReferenceAudioPaths, loaded.SpeakerReferenceAudioPaths);
        Assert.Equal(snapshot.MultiSpeakerEnabled, loaded.MultiSpeakerEnabled);
        Assert.Equal(snapshot.DefaultTtsVoiceFallback, loaded.DefaultTtsVoiceFallback);
        Assert.Equal(snapshot.DiarizationProvider, loaded.DiarizationProvider);
    }

    [Fact]
    public void Load_CorruptSnapshotFile_ReturnsNull()
    {
        var id = Guid.NewGuid();
        var sessionDir = Path.Combine(_sessionsRoot, id.ToString());
        Directory.CreateDirectory(sessionDir);
        File.WriteAllText(Path.Combine(sessionDir, "snapshot.json"), "{ not valid json {{{{");

        var result = _store.Load(id);
        Assert.Null(result);
        Assert.False(File.Exists(Path.Combine(sessionDir, "snapshot.json")));
        Assert.Single(FindCorruptBackups(sessionDir));
    }

    [Fact]
    public void SaveTwice_SameSession_OverwritesPreviousSnapshot()
    {
        var snapshot = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow);
        _store.Save(snapshot);

        var updated = snapshot with { Stage = SessionWorkflowStage.TtsGenerated };
        _store.Save(updated);

        var loaded = _store.Load(snapshot.SessionId)!;
        Assert.Equal(SessionWorkflowStage.TtsGenerated, loaded.Stage);
    }

    // ── LoadAll ────────────────────────────────────────────────────────────────

    [Fact]
    public void LoadAll_WhenRootDoesNotExist_ReturnsEmpty()
    {
        var nonExistentRoot = Path.Combine(_dir, "does-not-exist");
        var store = new PerSessionSnapshotStore(nonExistentRoot, _log);
        // Remove it to simulate non-existent root after construction
        Directory.Delete(nonExistentRoot);

        var result = store.LoadAll();
        Assert.Empty(result);
    }

    [Fact]
    public void LoadAll_NoSnapshots_ReturnsEmpty()
    {
        var result = _store.LoadAll();
        Assert.Empty(result);
    }

    [Fact]
    public void LoadAll_ReturnsAllSavedSnapshots()
    {
        var a = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow);
        var b = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with { Stage = SessionWorkflowStage.Transcribed };
        _store.Save(a);
        _store.Save(b);

        var all = _store.LoadAll();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, s => s.SessionId == a.SessionId);
        Assert.Contains(all, s => s.SessionId == b.SessionId);
    }

    [Fact]
    public void LoadAll_SkipsDirectoriesWithNoSnapshotFile()
    {
        // Create a directory that has no snapshot.json
        var emptyDir = Path.Combine(_sessionsRoot, Guid.NewGuid().ToString());
        Directory.CreateDirectory(emptyDir);

        var snapshot = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow);
        _store.Save(snapshot);

        var all = _store.LoadAll();
        Assert.Single(all);
    }

    [Fact]
    public void LoadAll_SkipsCorruptSnapshotFiles()
    {
        // Write a corrupt snapshot
        var corruptId = Guid.NewGuid();
        var corruptDir = Path.Combine(_sessionsRoot, corruptId.ToString());
        Directory.CreateDirectory(corruptDir);
        File.WriteAllText(Path.Combine(corruptDir, "snapshot.json"), "bad json ;;");

        // Write a good snapshot
        var good = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow);
        _store.Save(good);

        var all = _store.LoadAll();
        // Only the good snapshot is returned
        Assert.Single(all);
        Assert.Equal(good.SessionId, all[0].SessionId);
        Assert.False(File.Exists(Path.Combine(corruptDir, "snapshot.json")));
        Assert.Single(FindCorruptBackups(corruptDir));
    }

    [Fact]
    public void Save_WithExtraSessionDirectory_WritesBothCopies()
    {
        var snapshot = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow);
        var extraDir = Path.Combine(_dir, "project", "sessions", snapshot.SessionId.ToString());

        _store.Save(snapshot, extraDir);

        Assert.True(File.Exists(Path.Combine(_sessionsRoot, snapshot.SessionId.ToString(), "snapshot.json")));
        Assert.True(File.Exists(Path.Combine(extraDir, "snapshot.json")));
    }

    [Fact]
    public void Save_WithSameExtraSessionDirectory_DoesNotFail()
    {
        var snapshot = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow);
        var sameDir = Path.Combine(_sessionsRoot, snapshot.SessionId.ToString());

        _store.Save(snapshot, sameDir);

        Assert.True(File.Exists(Path.Combine(sameDir, "snapshot.json")));
    }

    [Fact]
    public void TryLoadLatestForSourceMedia_ReturnsNewestMatchingSnapshot()
    {
        var media = Path.Combine(_dir, "clip.mp4");
        var older = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow.AddHours(-1)) with
        {
            SourceMediaPath = media,
            LastUpdatedAtUtc = DateTimeOffset.UtcNow.AddHours(-1),
        };
        var newer = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            SourceMediaPath = media,
            LastUpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        var other = WorkflowSessionSnapshot.CreateNew(DateTimeOffset.UtcNow) with
        {
            SourceMediaPath = Path.Combine(_dir, "other.mp4"),
            LastUpdatedAtUtc = DateTimeOffset.UtcNow.AddHours(1),
        };

        _store.Save(older);
        _store.Save(newer);
        _store.Save(other);

        var loaded = _store.TryLoadLatestForSourceMedia(media);

        Assert.NotNull(loaded);
        Assert.Equal(newer.SessionId, loaded.SessionId);
    }

    [Fact]
    public void TryLoadLatestForSourceMedia_MissingMedia_ReturnsNull()
    {
        Assert.Null(_store.TryLoadLatestForSourceMedia(Path.Combine(_dir, "missing.mp4")));
    }
}
