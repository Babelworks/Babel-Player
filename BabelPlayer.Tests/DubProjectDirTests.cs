using System;
using System.IO;
using Babel.Player.Services;

namespace BabelPlayer.Tests;

public sealed class DubProjectDirTests
{
    [Fact]
    public void ResolveSessionsRoot_DefaultsToAppLocalSessions()
    {
        string root = DubCli.ResolveSessionsRoot(
            Path.Combine("x", "BabelPlayer"), null);

        Assert.Equal(Path.Combine("x", "BabelPlayer", "sessions"), root);
    }

    [Fact]
    public void ResolveSessionsRoot_PortsSessionsUnderProjectDir()
    {
        string projectDir = Path.Combine(Path.GetTempPath(), "proj");
        string root = DubCli.ResolveSessionsRoot("appdata", projectDir);

        Assert.Equal(Path.Combine(Path.GetFullPath(projectDir), "sessions"), root);
    }

    [Fact]
    public void IsValidProjectDir_AcceptsNormalPathsRejectsNul()
    {
        Assert.True(DubCli.IsValidProjectDir("some-dir"));
        Assert.False(DubCli.IsValidProjectDir("a\0b"));
    }
}
