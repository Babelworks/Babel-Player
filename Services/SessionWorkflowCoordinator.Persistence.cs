using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Babel.Player.Models;

namespace Babel.Player.Services;

// Save/flush/persist plumbing lives here so the main coordinator file can stay
// focused on state transitions and pipeline orchestration. Splitting these out
// keeps SessionWorkflowCoordinator.cs under the architectural line-count
// threshold enforced by scripts/check-architecture.py.
public sealed partial class SessionWorkflowCoordinator
{
    /// <summary>
    /// Updates the snapshot's <c>LastUpdatedAtUtc</c>, commits it to <see cref="CurrentSession"/>
    /// under the session lock, and persists the snapshot synchronously.
    /// </summary>
    public void SaveCurrentSession()
    {
        WorkflowSessionSnapshot snapshot;
        lock (_sessionLock)
        {
            snapshot = CurrentSession with { LastUpdatedAtUtc = DateTimeOffset.UtcNow };
            CurrentSession = snapshot;
        }
        PersistSnapshot(snapshot, updateStatus: true);
    }

    /// <summary>
    /// Asynchronous counterpart to <see cref="SaveCurrentSession"/>. Pipeline code that is already
    /// inside an <c>async</c> context should prefer this so the disk write does not block the
    /// calling thread.
    /// </summary>
    public async Task SaveCurrentSessionAsync()
    {
        WorkflowSessionSnapshot snapshot;
        lock (_sessionLock)
        {
            snapshot = CurrentSession with { LastUpdatedAtUtc = DateTimeOffset.UtcNow };
            CurrentSession = snapshot;
        }
        await PersistSnapshotAsync(snapshot, updateStatus: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Immediately persists the current session snapshot and updates its last-updated timestamp.
    /// Used by callers that need a synchronous flush during shutdown or media-switch handoffs.
    /// </summary>
    public void FlushPendingSave()
    {
        WorkflowSessionSnapshot snapshot;
        lock (_sessionLock)
        {
            snapshot = CurrentSession with { LastUpdatedAtUtc = DateTimeOffset.UtcNow };
            CurrentSession = snapshot;
        }
        PersistSnapshot(snapshot, updateStatus: true);
    }

    private void PersistSnapshot(WorkflowSessionSnapshot snapshot, bool updateStatus)
    {
        var stopwatch = Stopwatch.StartNew();
        var projectSessionDir = ResolveSessionDirectory(snapshot.SessionId, snapshot.SourceMediaPath);
        _store.Save(snapshot);
        _perSessionStore.Save(snapshot, projectSessionDir);
        stopwatch.Stop();
        var message = $"Saved current session snapshot to {StateFilePath}.";
        if (updateStatus)
            PersistenceStatus = message;
        _log.Debug($"{message} Mirrored per-session snapshot. elapsedMs={stopwatch.ElapsedMilliseconds}");
    }

    private async Task PersistSnapshotAsync(WorkflowSessionSnapshot snapshot, bool updateStatus)
    {
        var stopwatch = Stopwatch.StartNew();
        var projectSessionDir = ResolveSessionDirectory(snapshot.SessionId, snapshot.SourceMediaPath);
        await _store.SaveAsync(snapshot).ConfigureAwait(false);
        await _perSessionStore.SaveAsync(snapshot, projectSessionDir).ConfigureAwait(false);
        stopwatch.Stop();
        var message = $"Saved current session snapshot to {StateFilePath}.";
        if (updateStatus)
            PersistenceStatus = message;
        _log.Debug($"{message} Mirrored per-session snapshot (async). elapsedMs={stopwatch.ElapsedMilliseconds}");
    }
}
