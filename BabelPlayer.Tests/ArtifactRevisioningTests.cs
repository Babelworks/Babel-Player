using System;
using System.IO;
using Babel.Player.Services;

namespace BabelPlayer.Tests;

public sealed class ArtifactRevisioningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"babel-rev-tests-{Guid.NewGuid():N}");

    public ArtifactRevisioningTests()
    {
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void BackupExisting_MovesToFirstFreeSlot()
    {
        string finalPath = Path.Combine(_dir, "clip.json");
        File.WriteAllText(finalPath, "v1");

        string? backup = ArtifactRevisioning.BackupExisting(finalPath);

        Assert.Equal(Path.Combine(_dir, "clip-rev0001.json"), backup);
        Assert.False(File.Exists(finalPath));
        Assert.Equal("v1", File.ReadAllText(backup!));
    }

    [Fact]
    public void BackupExisting_SkipsOccupiedSlots()
    {
        string finalPath = Path.Combine(_dir, "clip.json");
        File.WriteAllText(finalPath, "v2");
        File.WriteAllText(Path.Combine(_dir, "clip-rev0001.json"), "v1");

        string? backup = ArtifactRevisioning.BackupExisting(finalPath);

        Assert.Equal(Path.Combine(_dir, "clip-rev0002.json"), backup);
        Assert.Equal("v2", File.ReadAllText(backup!));
        Assert.Equal("v1", File.ReadAllText(Path.Combine(_dir, "clip-rev0001.json")));
    }

    [Fact]
    public void BackupExisting_ReturnsNullWhenNothingToBackUp()
    {
        Assert.Null(ArtifactRevisioning.BackupExisting(Path.Combine(_dir, "missing.json")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }
}
