using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Chatterbox;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services.SortFormer;

public sealed class SortFormerDiarizationProvider : IDiarizationProvider, IDisposable
{
    private readonly AppLog _log;
    private readonly string _modelDir;
    private readonly Lock _gate = new();
    private SortFormerDiarizationEngine? _engine;
    private int _disposed;

    public SortFormerDiarizationProvider(AppLog log, string modelDir)
    {
        _log = log;
        _modelDir = modelDir;
    }

    public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore)
    {
        if (!ModelDownloader.IsSortFormerModelDownloaded(settings.SortFormerModelDir))
        {
            return new ProviderReadiness(
                false,
                "SortFormer diarization model is not downloaded yet.",
                RequiresModelDownload: true,
                ModelDownloadDescription: "Download SortFormer 4-speaker diarization model");
        }

        var modelPath = Path.Combine(
            ModelDownloader.ResolveSortFormerModelDir(settings.SortFormerModelDir),
            SortFormerModelCatalog.RelativeModelPath);
        if (!SortFormerModelFiles.TryVerifySha256(modelPath, out _))
        {
            return new ProviderReadiness(
                false,
                "SortFormer model file failed SHA-256 verification. Re-download the model.",
                RequiresModelDownload: true,
                ModelDownloadDescription: "Re-download SortFormer 4-speaker diarization model");
        }

        return new ProviderReadiness(true, null);
    }

    public async Task<bool> EnsureReadyAsync(
        AppSettings settings,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (ModelDownloader.IsSortFormerModelDownloaded(settings.SortFormerModelDir))
        {
            var modelPath = Path.Combine(
                ModelDownloader.ResolveSortFormerModelDir(settings.SortFormerModelDir),
                SortFormerModelCatalog.RelativeModelPath);
            if (SortFormerModelFiles.TryVerifySha256(modelPath, out _))
                return true;
        }

        return await new ModelDownloader(_log)
            .DownloadSortFormerModelAsync(settings.SortFormerModelDir, progress, ct)
            .ConfigureAwait(false);
    }

    public async Task<DiarizationResult> DiarizeAsync(
        DiarizationRequest request,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (!File.Exists(request.SourceAudioPath))
            throw new FileNotFoundException($"Audio file not found: {request.SourceAudioPath}");

        _log.Debug($"[SortFormer] Diarizing: {request.SourceAudioPath}");

        try
        {
            var bytes = await File.ReadAllBytesAsync(request.SourceAudioPath, ct).ConfigureAwait(false);
            var (decoded, sourceRate) = ChatterboxAudio.DecodePcm16Mono(bytes);
            var samples = sourceRate == SortFormerDiarizationEngine.TargetSampleRate
                ? decoded
                : ChatterboxAudio.ResampleLinear(decoded, sourceRate, SortFormerDiarizationEngine.TargetSampleRate);

            // Prefer sample-derived duration so DecodeTurns timing stays aligned with PCM.
            double durationSeconds = samples.Length / (double)SortFormerDiarizationEngine.TargetSampleRate;
            if (durationSeconds <= 0)
                return new DiarizationResult(true, [], 0, null);

            if (request.MaxSpeakers is int maxSpeakers && maxSpeakers > SortFormerDiarizationEngine.MaxSupportedSpeakers)
            {
                _log.Warning(
                    $"SortFormer supports at most {SortFormerDiarizationEngine.MaxSupportedSpeakers} speakers; " +
                    $"requested maxSpeakers={maxSpeakers} will be capped by the model.");
            }

            var engine = GetOrCreateEngine();
            var turns = await Task.Run(() => engine.Diarize(samples, durationSeconds, ct), ct).ConfigureAwait(false);
            var segments = NormalizeSegments(turns);
            var speakerCount = segments
                .Select(segment => segment.SpeakerId)
                .Distinct(StringComparer.Ordinal)
                .Count();

            _log.Info($"[SortFormer] Complete: {speakerCount} speakers across {segments.Count} turns.");
            return new DiarizationResult(true, segments, speakerCount, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"SortFormer diarization failed: {ex.Message}", ex);
            return new DiarizationResult(false, [], 0, ex.Message);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        lock (_gate)
        {
            _engine?.Dispose();
            _engine = null;
        }
    }

    private SortFormerDiarizationEngine GetOrCreateEngine()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_engine is not null)
                return _engine;

            var files = SortFormerModelFiles.Resolve(_modelDir);
            _engine = new SortFormerDiarizationEngine(files.ModelPath);
            return _engine;
        }
    }

    private static IReadOnlyList<DiarizedSegment> NormalizeSegments(IReadOnlyList<SortFormerSpeakerTurn> turns)
    {
        var normalized = new List<DiarizedSegment>(turns.Count);
        var assignedLabels = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var turn in turns)
        {
            normalized.Add(new DiarizedSegment(
                turn.StartSeconds,
                turn.EndSeconds,
                NormalizeSpeakerId(turn.SpeakerKey, assignedLabels)));
        }

        return normalized;
    }

    /// <summary>
    /// Maps TrackDub-style <c>spk_0</c> labels to Babel <c>spk_00</c> zero-padded ids.
    /// </summary>
    internal static string NormalizeSpeakerId(string? rawSpeakerId, IDictionary<string, string>? assignedLabels = null)
    {
        assignedLabels ??= new Dictionary<string, string>(StringComparer.Ordinal);
        var key = string.IsNullOrWhiteSpace(rawSpeakerId)
            ? $"speaker_{assignedLabels.Count}"
            : rawSpeakerId.Trim();

        if (assignedLabels.TryGetValue(key, out var existing))
            return existing;

        // Prefer stable zero-padded ids when TrackDub emits spk_0 / spk_1.
        if (key.StartsWith("spk_", StringComparison.Ordinal) &&
            int.TryParse(key.AsSpan(4), out var index) &&
            index >= 0)
        {
            var padded = $"spk_{index:00}";
            assignedLabels[key] = padded;
            return padded;
        }

        var normalized = $"spk_{assignedLabels.Count:00}";
        assignedLabels[key] = normalized;
        return normalized;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed != 0, this);
}
