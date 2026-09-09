using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Babel.Player.Services;

namespace BabelPlayer.Tests;

public sealed class DubRunTimingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"babel-timings-tests-{Guid.NewGuid():N}");

    public DubRunTimingsTests()
    {
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void Phases_AreDerivedAsDeltas()
    {
        var timings = new DubRunTimings();
        timings.Mark("load-media", TimeSpan.FromSeconds(2));
        timings.Mark("pipeline", TimeSpan.FromSeconds(7));

        var phases = timings.Phases();

        Assert.Equal(2, phases.Count);
        Assert.Equal(("load-media", 2), phases[0]);
        Assert.Equal(("pipeline", 5), phases[1]);
    }

    [Fact]
    public void Serialize_RecordsPhasesAndTotal()
    {
        var timings = new DubRunTimings();
        timings.Mark("load-media", TimeSpan.FromSeconds(1.5));
        timings.Mark("pipeline", TimeSpan.FromSeconds(4));

        using var document = JsonDocument.Parse(
            timings.Serialize("clip", DateTimeOffset.UtcNow));

        var phases = document.RootElement.GetProperty("Phases").EnumerateArray().ToList();
        Assert.Equal(2, phases.Count);
        Assert.Equal("pipeline", phases[1].GetProperty("Phase").GetString());
        Assert.Equal(4, document.RootElement.GetProperty("TotalSeconds").GetDouble(), precision: 3);
    }

    [Fact]
    public void Write_RoundTripsThroughFile()
    {
        var timings = new DubRunTimings();
        timings.Mark("load-media", TimeSpan.FromSeconds(1));

        string path = timings.Write(_dir, "clip", DateTimeOffset.UtcNow);

        Assert.Equal(DubRunTimings.BuildPath(_dir, "clip"), path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("clip", document.RootElement.GetProperty("MediaFile").GetString());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }
}
