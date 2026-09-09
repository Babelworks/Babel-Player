using System;
using System.IO;
using Babel.Player.Services;
using Xunit;

namespace BabelPlayer.Tests;

public sealed class ProjectFolderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"babel-project-folder-{Guid.NewGuid():N}");

    public ProjectFolderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort cleanup */ }
    }

    [Fact]
    public void TryGetDefaultDirectory_UsesStemBabelSibling()
    {
        var media = Path.Combine(_dir, "clip.mp4");
        var project = ProjectFolder.TryGetDefaultDirectory(media);

        Assert.Equal(Path.Combine(_dir, "clip.mp4.babel"), project);
    }

    [Fact]
    public void TryGetDefaultDirectory_KeepsSameStemDifferentExtensionsApart()
    {
        var mp4 = ProjectFolder.TryGetDefaultDirectory(Path.Combine(_dir, "clip.mp4"));
        var mov = ProjectFolder.TryGetDefaultDirectory(Path.Combine(_dir, "clip.mov"));

        Assert.Equal(Path.Combine(_dir, "clip.mp4.babel"), mp4);
        Assert.Equal(Path.Combine(_dir, "clip.mov.babel"), mov);
        Assert.NotEqual(mp4, mov);
    }

    [Fact]
    public void SanitizeDirectoryName_StripsInvalidFileNameCharacters()
    {
        var sanitized = ProjectFolder.SanitizeDirectoryName("clip:take?");
        Assert.Equal("cliptake", sanitized);
        Assert.Equal("cliptake", ProjectFolder.SanitizeDirectoryName("clip<>take|?*"));
        Assert.Equal("_CON.mp4", ProjectFolder.SanitizeDirectoryName("CON.mp4"));
        Assert.Equal("_nul", ProjectFolder.SanitizeDirectoryName("nul"));
        Assert.Equal("clip.mp4", ProjectFolder.SanitizeDirectoryName("clip.mp4"));
    }

    [Fact]
    public void ResolveSessionDirectory_UsesProjectSessionsWhenWritable()
    {
        var media = Path.Combine(_dir, "show.mkv");
        var appLocalRoot = Path.Combine(_dir, "app-sessions");
        var sessionId = Guid.NewGuid();

        var resolved = ProjectFolder.ResolveSessionDirectory(appLocalRoot, sessionId, media, useProjectFolders: true);

        Assert.Equal(Path.Combine(_dir, "show.mkv.babel", "sessions", sessionId.ToString()), resolved);
        Assert.True(Directory.Exists(Path.Combine(_dir, "show.mkv.babel", "sessions")));
    }

    [Fact]
    public void TryEnsureWritableSessionsRoot_FailsWhenSessionsPathIsAFile()
    {
        var projectDir = Path.Combine(_dir, "clip.mp4.babel");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, ProjectFolder.SessionsFolderName), "not a directory");

        Assert.False(ProjectFolder.TryEnsureWritableSessionsRoot(projectDir));
        Assert.True(ProjectFolder.TryEnsureWritable(projectDir));
    }

    [Fact]
    public void ResolveSessionDirectory_FallsBackToAppLocalWhenDisabled()
    {
        var media = Path.Combine(_dir, "show.mkv");
        var appLocalRoot = Path.Combine(_dir, "app-sessions");
        var sessionId = Guid.NewGuid();

        var resolved = ProjectFolder.ResolveSessionDirectory(appLocalRoot, sessionId, media, useProjectFolders: false);

        Assert.Equal(Path.Combine(appLocalRoot, sessionId.ToString()), resolved);
    }

    [Fact]
    public void ResolveSessionDirectory_HonorsProjectDirectoryOverrideOverMediaSibling()
    {
        var media = Path.Combine(_dir, "clip.mp4");
        var appLocalRoot = Path.Combine(_dir, "app-sessions");
        var portable = Path.Combine(_dir, "portable");
        Directory.CreateDirectory(portable);
        var sessionId = Guid.NewGuid();

        var resolved = ProjectFolder.ResolveSessionDirectory(
            appLocalRoot,
            sessionId,
            media,
            useProjectFolders: true,
            projectDirectoryOverride: portable);

        Assert.Equal(Path.Combine(portable, "sessions", sessionId.ToString()), resolved);
        Assert.True(Directory.Exists(Path.Combine(portable, "sessions")));
        Assert.False(Directory.Exists(Path.Combine(_dir, "clip.mp4.babel")));
    }

    [Fact]
    public void ResolveSessionDirectory_OverrideWinsWhenProjectFoldersAreDisabled()
    {
        var media = Path.Combine(_dir, "clip.mp4");
        var appLocalRoot = Path.Combine(_dir, "app-sessions");
        var portable = Path.Combine(_dir, "portable");
        Directory.CreateDirectory(portable);
        var sessionId = Guid.NewGuid();

        var resolved = ProjectFolder.ResolveSessionDirectory(
            appLocalRoot,
            sessionId,
            media,
            useProjectFolders: false,
            projectDirectoryOverride: portable);

        Assert.Equal(Path.Combine(portable, "sessions", sessionId.ToString()), resolved);
    }

    [Fact]
    public void NewAppSettings_EnablesProjectFoldersByDefault()
    {
        Assert.True(new Babel.Player.Services.Settings.AppSettings().StoreProjectsNextToMedia);
    }
}
