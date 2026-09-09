using System;
using System.IO;

namespace Babel.Player.Services;

/// <summary>
/// Revision backups for transcript and translation artifacts, stolen from the
/// Trackdub archive's revisioned transcripts: before a completed artifact
/// overwrites its final path, the previous file moves to the next free
/// <c>{stem}-rev{NNNN}{ext}</c> slot so a bad re-run cannot destroy good output.
/// </summary>
internal static class ArtifactRevisioning
{
    internal static string BuildRevisionPath(string finalPath, int revision)
    {
        var dir = Path.GetDirectoryName(finalPath) ?? AppContext.BaseDirectory;
        var stem = Path.GetFileNameWithoutExtension(finalPath);
        var extension = Path.GetExtension(finalPath);
        return Path.Combine(dir, $"{stem}-rev{revision:0000}{extension}");
    }

    /// <summary>
    /// Moves the existing file at <paramref name="finalPath"/> to the next free
    /// revision slot. Returns null when there is nothing to back up.
    /// </summary>
    internal static string? BackupExisting(string finalPath)
    {
        if (!File.Exists(finalPath))
            return null;

        for (int revision = 1; revision <= 9999; revision++)
        {
            string candidate = BuildRevisionPath(finalPath, revision);
            if (File.Exists(candidate))
                continue;

            File.Move(finalPath, candidate);
            return candidate;
        }
    }
}
