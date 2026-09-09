using System;
using System.IO;
using System.Text.Json;

namespace Babel.Player.Services;

/// <summary>
/// Sidecar record written next to every dub delivery set, stolen from the
/// Trackdub archive's export-manifest: which providers, voice, and language
/// produced these artifacts, when, and with what result.
/// </summary>
internal sealed record DubExportManifest(
    string MediaFile,
    string TargetLanguage,
    string TranscriptionProvider,
    string TranslationProvider,
    string TtsProvider,
    string TtsVoice,
    bool DiarizationEnabled,
    bool Mp4Exported,
    string SrtPath,
    string Mp3Path,
    string? Mp4Path,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    int SegmentCount,
    int ExitCode);

internal static class DubManifest
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    internal static string BuildPath(string outputDir, string mediaStem) =>
        Path.Combine(outputDir, $"{mediaStem}-dub.manifest.json");

    internal static string Serialize(DubExportManifest manifest) =>
        JsonSerializer.Serialize(manifest, SerializerOptions);

    internal static string Write(string outputDir, DubExportManifest manifest)
    {
        string path = BuildPath(outputDir, Path.GetFileName(Path.GetFileNameWithoutExtension(manifest.MediaFile)));
        JsonStorePersistence.AtomicWriteText(path, Serialize(manifest));
        return path;
    }
}
