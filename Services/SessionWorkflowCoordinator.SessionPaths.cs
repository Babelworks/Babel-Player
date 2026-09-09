using System;
using System.IO;
using Babel.Player.Models;

namespace Babel.Player.Services;

// Session and TTS path helpers live here so SessionWorkflowCoordinator.cs stays
// under the architectural line-count threshold in scripts/check-architecture.py.
public sealed partial class SessionWorkflowCoordinator
{
    /// <summary>
    /// Base filename (no extension) for transcript JSON under <c>transcripts/</c>.
    /// Prefers the ingested media file name so vocal-separation stem paths (for example <c>vocals.wav</c>)
    /// do not replace the user-visible artifact name derived from the original loaded file.
    /// </summary>
    internal static string ResolveTranscriptArtifactStem(string? ingestedMediaPath, string transcriptionSourcePath)
    {
        if (!string.IsNullOrWhiteSpace(ingestedMediaPath))
            return Path.GetFileNameWithoutExtension(ingestedMediaPath);
        return Path.GetFileNameWithoutExtension(transcriptionSourcePath);
    }

    /// <summary>
    /// Builds TTS output file paths for a translation artifact and ensures their parent directories exist.
    /// Sanitizes the voice identifier so that reserved path characters don't produce invalid file names.
    /// </summary>
    /// <param name="translationPath">Full path to the translation artifact JSON file.</param>
    /// <param name="voice">Voice identifier used to name the combined MP3 output file.</param>
    /// <returns>
    /// A tuple of <c>TtsPath</c> (full path to the per-translation MP3) and <c>SegmentsDir</c>
    /// (directory for per-segment audio files); both directories are created if they do not exist.
    /// </returns>
    internal static (string TtsPath, string SegmentsDir) BuildTtsOutputPaths(string translationPath, string voice)
    {
        var sessionDir = Path.GetDirectoryName(Path.GetDirectoryName(translationPath)!)!;
        var ttsDir = Path.Combine(sessionDir, "tts");
        Directory.CreateDirectory(ttsDir);
        var fileName = Path.GetFileNameWithoutExtension(translationPath);
        var sanitizedVoice = ProjectFolder.SanitizeDirectoryName(voice);
        if (sanitizedVoice.Length == 0) sanitizedVoice = "default";
        var ttsPath = Path.Combine(ttsDir, $"{fileName}_{sanitizedVoice}.mp3");
        var segmentsDir = Path.Combine(ttsDir, "segments", Path.GetFileNameWithoutExtension(translationPath));
        Directory.CreateDirectory(segmentsDir);
        return (ttsPath, segmentsDir);
    }

    private string GetSessionDirectory() => SessionDirectoryFor(CurrentSession.SessionId);

    private string SessionDirectoryFor(Guid sessionId) =>
        ResolveSessionDirectory(sessionId, CurrentSession.SourceMediaPath);

    private string ResolveSessionDirectory(Guid sessionId, string? sourceMediaPath)
    {
        var resolved = ProjectFolder.ResolveSessionDirectory(
            _perSessionStore.SessionsRoot,
            sessionId,
            sourceMediaPath,
            useProjectFolders: CurrentSettings.StoreProjectsNextToMedia,
            projectDirectoryOverride: _projectDirectoryOverride);

        if (_projectDirectoryOverride is null &&
            CurrentSettings.StoreProjectsNextToMedia &&
            ProjectFolder.TryGetDefaultDirectory(sourceMediaPath) is { } projectDir)
        {
            var intended = Path.Combine(projectDir, ProjectFolder.SessionsFolderName, sessionId.ToString());
            if (!string.Equals(resolved, intended, StringComparison.OrdinalIgnoreCase))
            {
                _log.Warning(
                    $"Project folder is not writable ({projectDir}). Using app-local session storage.");
            }
        }

        return resolved;
    }

    private WorkflowSessionSnapshot? TryLoadProjectFolderSession(string sourceMediaPath)
    {
        if (!CurrentSettings.StoreProjectsNextToMedia && _projectDirectoryOverride is null)
            return null;

        var projectDir = !string.IsNullOrWhiteSpace(_projectDirectoryOverride)
            ? _projectDirectoryOverride
            : ProjectFolder.TryGetDefaultDirectory(sourceMediaPath);

        if (projectDir is null)
            return null;

        var sessionsRoot = Path.Combine(projectDir, ProjectFolder.SessionsFolderName);
        if (!Directory.Exists(sessionsRoot))
            return null;

        try
        {
            if (string.Equals(
                    Path.GetFullPath(sessionsRoot),
                    Path.GetFullPath(_perSessionStore.SessionsRoot),
                    StringComparison.OrdinalIgnoreCase))
                return null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        using var store = new PerSessionSnapshotStore(sessionsRoot, _log);
        return store.TryLoadLatestForSourceMedia(sourceMediaPath);
    }

    private static string? NormalizeProjectDirectory(string? projectDirectory)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory))
            return null;

        try
        {
            return Path.GetFullPath(projectDirectory.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
