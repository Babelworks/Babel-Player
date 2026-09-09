using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Babel.Player.Services;

/// <summary>
/// Resolves the default on-disk project folder for a media file.
/// When enabled, each source file gets a sibling <c>{filename}.babel</c> directory
/// (for example <c>clip.mp4.babel</c>) for session artifacts and headless delivery files.
/// </summary>
internal static class ProjectFolder
{
    public const string DirectorySuffix = ".babel";
    public const string SessionsFolderName = "sessions";

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
        var fileName = SanitizeDirectoryName(Path.GetFileName(fullPath));
        if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(fileName))
            return null;

        return Path.Combine(parent, fileName + DirectorySuffix);
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

        if (!TryEnsureWritableSessionsRoot(projectDir))
            return appLocalDir;

        var projectSessionsRoot = Path.Combine(projectDir, SessionsFolderName);

        return Path.Combine(projectSessionsRoot, sessionId.ToString());
    }

    public static bool TryEnsureWritableSessionsRoot(string projectDir) =>
        !string.IsNullOrWhiteSpace(projectDir) &&
        TryEnsureWritable(Path.Combine(projectDir, SessionsFolderName));

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

        var trimmed = string.Concat(name.Where(ch => !InvalidFileNameCharacters.Contains(ch))).Trim();
        return trimmed.TrimEnd('.', ' ');
    }

    // Path.GetInvalidFileNameChars() is OS-specific. Linux omits ':' and '?', which
    // still break project folders copied to Windows or shared across machines.
    private static readonly HashSet<char> InvalidFileNameCharacters = CreateInvalidFileNameCharacters();

    private static HashSet<char> CreateInvalidFileNameCharacters()
    {
        var chars = new HashSet<char>(Path.GetInvalidFileNameChars());
        foreach (var ch in "<>:\"/\\|?*")
            chars.Add(ch);
        for (int i = 0; i < 32; i++)
            chars.Add((char)i);
        return chars;
    }
}
