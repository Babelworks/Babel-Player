using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Babel.Player.Services;

/// <summary>
/// Per-phase timings for a headless dub run, stolen from the Trackdub
/// archive's run-manifest stage outcomes. Checkpoints are elapsed-since-start
/// marks; phase durations are derived as deltas.
/// </summary>
internal sealed class DubRunTimings
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly List<(string Phase, TimeSpan Elapsed)> _marks = [];

    internal void Mark(string phase, TimeSpan elapsed) => _marks.Add((phase, elapsed));

    internal IReadOnlyList<(string Phase, double Seconds)> Phases()
    {
        var phases = new List<(string, double)>();
        TimeSpan previous = TimeSpan.Zero;
        foreach (var (phase, elapsed) in _marks)
        {
            phases.Add((phase, Math.Max(0, (elapsed - previous).TotalSeconds)));
            previous = elapsed;
        }
        return phases;
    }

    internal static string BuildPath(string outputDir, string mediaStem) =>
        Path.Combine(outputDir, $"{mediaStem}-run.json");

    internal string Serialize(string mediaStem, DateTimeOffset startedUtc) =>
        JsonSerializer.Serialize(
            new
            {
                MediaFile = mediaStem,
                StartedUtc = startedUtc,
                TotalSeconds = Math.Max(0, (_marks.Count > 0 ? _marks[^1].Elapsed : TimeSpan.Zero).TotalSeconds),
                Phases = Phases().Select(p => new { Phase = p.Phase, Seconds = Math.Round(p.Seconds, 2) }).ToArray(),
            },
            SerializerOptions);

    internal string Write(string outputDir, string mediaStem, DateTimeOffset startedUtc)
    {
        string path = BuildPath(outputDir, mediaStem);
        JsonStorePersistence.AtomicWriteText(path, Serialize(mediaStem, startedUtc));
        return path;
    }
}
