using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;

namespace Babel.Player.Services;

/// <summary>
/// Persists each session's <see cref="WorkflowSessionSnapshot"/> to a per-session directory
/// under <c>sessions/[SessionId]/snapshot.json</c>.
/// </summary>
public sealed class PerSessionSnapshotStore : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _sessionsRoot;
    private readonly AppLog _log;
    // Single store-wide save gate. A per-session gate would allow cross-session
    // parallelism, but saves are short (small JSON write) and rare, and a per-session
    // dictionary grows unbounded as new sessions are created. One gate keeps memory
    // flat and still serializes same-session Save()/SaveAsync() correctly.
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public PerSessionSnapshotStore(string sessionsRoot, AppLog log)
    {
        _sessionsRoot = sessionsRoot;
        _log = log;
        Directory.CreateDirectory(sessionsRoot);
    }

    /// <summary>Root directory containing all per-session folders.</summary>
    public string SessionsRoot => _sessionsRoot;

    /// <summary>Returns the directory path for a specific session ID.</summary>
    public string GetSessionDirectory(Guid sessionId) => SessionDir(sessionId);

    /// <summary>Writes <c>sessions/[SessionId]/snapshot.json</c>. Non-fatal on failure.</summary>
    public void Save(WorkflowSessionSnapshot snapshot, string? extraSessionDirectory = null)
    {
        // Serialize inside the gate so gate-acquisition order matches file-write order.
        _saveGate.Wait();
        try
        {
            WriteSnapshotUnsafe(snapshot, extraSessionDirectory);
        }
        catch (Exception ex)
        {
            _log.Error($"PerSessionSnapshotStore: failed to save session {snapshot.SessionId}.", ex);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    /// <summary>
    /// Asynchronous counterpart to <see cref="Save"/>. Use from async pipeline code to avoid
    /// blocking the caller on disk I/O. Non-fatal on failure (errors are logged and swallowed).
    /// </summary>
    public async Task SaveAsync(WorkflowSessionSnapshot snapshot, string? extraSessionDirectory = null)
    {
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await WriteSnapshotUnsafeAsync(snapshot, extraSessionDirectory).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error($"PerSessionSnapshotStore: failed to save session {snapshot.SessionId}.", ex);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    /// <summary>
    /// Loads a single session by ID. Returns null if the file is absent or unreadable.
    /// </summary>
    public WorkflowSessionSnapshot? Load(Guid sessionId)
    {
        var path = SnapshotPath(sessionId);
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            var snapshot = SessionSnapshotJsonCompat.Deserialize(json, SerializerOptions);
            if (snapshot is null)
            {
                RecoverUnreadableSnapshot(path, $"PerSessionSnapshotStore: session {sessionId} snapshot file was empty or unreadable JSON.");
                return null;
            }

            return snapshot;
        }
        catch (JsonException ex)
        {
            RecoverUnreadableSnapshot(path, $"PerSessionSnapshotStore: failed to load session {sessionId}.", ex);
            return null;
        }
        catch (Exception ex)
        {
            _log.Warning($"PerSessionSnapshotStore: failed to load session {sessionId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Loads all sessions found in subdirectories of the sessions root.
    /// Directories that cannot be read are skipped and logged.
    /// </summary>
    public IReadOnlyList<WorkflowSessionSnapshot> LoadAll()
    {
        var results = new List<WorkflowSessionSnapshot>();

        if (!Directory.Exists(_sessionsRoot)) return results;

        foreach (var dir in Directory.EnumerateDirectories(_sessionsRoot))
        {
            var path = Path.Combine(dir, "snapshot.json");
            if (!File.Exists(path)) continue;

            try
            {
                var json = File.ReadAllText(path);
                var snapshot = SessionSnapshotJsonCompat.Deserialize(json, SerializerOptions);
                if (snapshot is null)
                {
                    RecoverUnreadableSnapshot(path, $"PerSessionSnapshotStore: snapshot at {path} was empty or unreadable JSON.");
                    continue;
                }

                results.Add(snapshot);
            }
            catch (JsonException ex)
            {
                RecoverUnreadableSnapshot(path, $"PerSessionSnapshotStore: skipped unreadable snapshot at {path}.", ex);
            }
            catch (Exception ex)
            {
                _log.Warning($"PerSessionSnapshotStore: skipped unreadable snapshot at {path}: {ex.Message}");
            }
        }

        return results;
    }

    /// <summary>
    /// Returns the newest snapshot whose <see cref="WorkflowSessionSnapshot.SourceMediaPath"/>
    /// matches <paramref name="sourceMediaPath"/>, or null if none exists.
    /// </summary>
    public WorkflowSessionSnapshot? TryLoadLatestForSourceMedia(string sourceMediaPath)
    {
        if (string.IsNullOrWhiteSpace(sourceMediaPath))
            return null;

        WorkflowSessionSnapshot? latest = null;
        foreach (var snapshot in LoadAll())
        {
            if (!SameMediaPath(snapshot.SourceMediaPath, sourceMediaPath))
                continue;
            if (latest is null || snapshot.LastUpdatedAtUtc > latest.LastUpdatedAtUtc)
                latest = snapshot;
        }

        return latest;
    }

    private void WriteSnapshotUnsafe(WorkflowSessionSnapshot snapshot, string? extraSessionDirectory)
    {
        var json = JsonSerializer.Serialize(snapshot, SerializerOptions);
        WriteSnapshotJson(SessionDir(snapshot.SessionId), json);
        if (TryGetDistinctExtraDirectory(snapshot.SessionId, extraSessionDirectory) is { } extraDir)
            WriteSnapshotJson(extraDir, json);
    }

    private async Task WriteSnapshotUnsafeAsync(WorkflowSessionSnapshot snapshot, string? extraSessionDirectory)
    {
        var json = JsonSerializer.Serialize(snapshot, SerializerOptions);
        await WriteSnapshotJsonAsync(SessionDir(snapshot.SessionId), json).ConfigureAwait(false);
        if (TryGetDistinctExtraDirectory(snapshot.SessionId, extraSessionDirectory) is { } extraDir)
            await WriteSnapshotJsonAsync(extraDir, json).ConfigureAwait(false);
    }

    private string? TryGetDistinctExtraDirectory(Guid sessionId, string? extraSessionDirectory)
    {
        if (string.IsNullOrWhiteSpace(extraSessionDirectory))
            return null;

        try
        {
            var extra = Path.GetFullPath(extraSessionDirectory);
            var primary = Path.GetFullPath(SessionDir(sessionId));
            return string.Equals(extra, primary, StringComparison.OrdinalIgnoreCase) ? null : extra;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static void WriteSnapshotJson(string sessionDirectory, string json)
    {
        Directory.CreateDirectory(sessionDirectory);
        JsonStorePersistence.AtomicWriteText(Path.Combine(sessionDirectory, "snapshot.json"), json);
    }

    private static Task WriteSnapshotJsonAsync(string sessionDirectory, string json)
    {
        Directory.CreateDirectory(sessionDirectory);
        return JsonStorePersistence.AtomicWriteTextAsync(Path.Combine(sessionDirectory, "snapshot.json"), json);
    }

    private static bool SameMediaPath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private string SessionDir(Guid sessionId) =>
        Path.Combine(_sessionsRoot, sessionId.ToString());

    private string SnapshotPath(Guid sessionId) =>
        Path.Combine(SessionDir(sessionId), "snapshot.json");

    private void RecoverUnreadableSnapshot(string path, string statusMessage, Exception? ex = null)
    {
        if (ex is not null)
        {
            _log.Warning($"{statusMessage} {ex.Message}");
        }
        else
        {
            _log.Warning(statusMessage);
        }

        try
        {
            var backupPath = JsonStorePersistence.MoveUnreadableFileToBackup(path);
            _log.Warning($"PerSessionSnapshotStore: unreadable snapshot was moved to {backupPath}.");
        }
        catch (Exception moveEx)
        {
            _log.Error($"PerSessionSnapshotStore: failed to quarantine unreadable snapshot '{path}'.", moveEx);
        }
    }

    public void Dispose()
    {
        _saveGate.Dispose();
    }
}
