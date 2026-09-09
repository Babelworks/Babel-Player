using System;
using System.IO;
using System.Linq;

namespace Babel.Player.Services;

/// <summary>
/// Resolves the default on-disk project folder for a media file.
/// When enabled, each source file gets a sibling <c>{stem}.babel</c> directory
/// for session artifacts and (for headless runs) delivery files.
/// </summary>
internal static class ProjectFolder
{
    public const string DirectorySuffix = ".babel";

    public static string? TryGetDefaultDirectory(string? sourceMediaPath)
    {
        if (string.IsNullOrWhiteSpace(sourceMediaPath))
            return null;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(sourceMediaPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        var parent = Path.GetDirectoryName(fullPath);
        var stem = SanitizeDirectoryName(Path.GetFileNameWithoutExtension(fullPath));
        if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(stem))
            return null;

        return Path.Combine(parent, stem + DirectorySuffix);
    }

    public static string ResolveSessionDirectory(
        string appLocalSessionsRoot,
        Guid sessionId,
        string? sourceMediaPath,
        bool useProjectFolders)
    {
        var appLocalDir = Path.Combine(appLocalSessionsRoot, sessionId.ToString());
        if (!useProjectFolders)
            return appLocalDir;

        var projectDir = TryGetDefaultDirectory(sourceMediaPath);
        if (projectDir is null)
            return appLocalDir;

        var projectSessionsRoot = Path.Combine(projectDir, "sessions");
        if (!TryEnsureWritable(projectSessionsRoot))
            return appLocalDir;

        return Path.Combine(projectSessionsRoot, sessionId.ToString());
    }

    public static bool TryEnsureWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".babel-write-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static string SanitizeDirectoryName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var invalid = Path.GetInvalidFileNameChars();
        var trimmed = string.Concat(name.Where(ch => !invalid.Contains(ch))).Trim();
        return trimmed.TrimEnd('.', ' ');
    }
}
