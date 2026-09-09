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

        Assert.Equal(Path.Combine(_dir, "clip.babel"), project);
    }

    [Fact]
    public void SanitizeDirectoryName_StripsInvalidFileNameCharacters()
    {
        var sanitized = ProjectFolder.SanitizeDirectoryName("clip:take?");
        Assert.Equal("cliptake", sanitized);
    }

    [Fact]
    public void ResolveSessionDirectory_UsesProjectSessionsWhenWritable()
    {
        var media = Path.Combine(_dir, "show.mkv");
        var appLocalRoot = Path.Combine(_dir, "app-sessions");
        var sessionId = Guid.NewGuid();

        var resolved = ProjectFolder.ResolveSessionDirectory(appLocalRoot, sessionId, media, useProjectFolders: true);

        Assert.Equal(Path.Combine(_dir, "show.babel", "sessions", sessionId.ToString()), resolved);
        Assert.True(Directory.Exists(Path.Combine(_dir, "show.babel", "sessions")));
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
    public void NewAppSettings_EnablesProjectFoldersByDefault()
    {
        Assert.True(new Babel.Player.Services.Settings.AppSettings().StoreProjectsNextToMedia);
    }
}
