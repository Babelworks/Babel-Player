using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services.Chatterbox;

public sealed class ChatterboxTtsProvider : ITtsProvider, IDisposable, IAsyncDisposable
{
    private readonly AppLog _log;
    private readonly string _modelDir;
    private readonly bool _consentGranted;
    private readonly SegmentedTtsComposer _composer;
    private readonly TtsReferenceExtractor _extractor;
    private readonly bool _ownsExtractor;
    private readonly Lock _gate = new();
    private ChatterboxTtsEngine? _engine;
    private int _disposed;

    public ChatterboxTtsProvider(AppLog log, string modelDir, bool consentGranted, TtsReferenceExtractor? extractor = null)
    {
        _log = log;
        _modelDir = modelDir;
        _consentGranted = consentGranted;
        _composer = new SegmentedTtsComposer();
        _extractor = extractor ?? new TtsReferenceExtractor(log);
        _ownsExtractor = extractor is null;
    }

    public int MaxConcurrency => 1;

    public Task<TtsResult> GenerateTtsAsync(
        TtsRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        return _composer.GenerateAsync(
            request,
            Log,
            providerLabel: "Chatterbox",
            maxConcurrency: MaxConcurrency,
            requestFactory: (segment, segmentAudioPath) => new SingleSegmentTtsRequest(
                segment.TranslatedText!,
                segmentAudioPath,
                segment.SpeakerId ?? "clone",
                SpeakerId: segment.SpeakerId,
                ReferenceAudioPath: ResolveReferenceAudioPath(request, segment.SpeakerId),
                Language: request.Language,
                SourceVideoPath: request.SourceVideoPath,
                TargetDurationSeconds: Math.Max(0, segment.End - segment.Start),
                SourceStartSeconds: segment.Start,
                SourceEndSeconds: segment.End),
            generateSegmentAsync: GenerateSegmentTtsAsync,
            cancellationToken: cancellationToken);
    }

    public async Task<TtsResult> GenerateSegmentTtsAsync(
        SingleSegmentTtsRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputAudioPath);

        if (!_consentGranted)
        {
            throw new InvalidOperationException(
                "Chatterbox voice cloning requires explicit consent. Grant it in Settings (Chatterbox voice cloning) or pass --consent-clone for headless runs.");
        }

        if (string.IsNullOrWhiteSpace(request.ReferenceAudioPath) || !File.Exists(request.ReferenceAudioPath))
        {
            var resolved = await EnsureSourceLanguageReferenceAsync(request, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(resolved))
            {
                throw new InvalidOperationException(
                    "Chatterbox voice cloning requires source audio to clone from. Load media first, or assign a speaker clip in the Speaker Reference Wizard.");
            }

            request = request with { ReferenceAudioPath = resolved };
        }

        _log.Info($"Starting Chatterbox segment TTS ({request.SpeakerId ?? "clone"}): {request.Text[..Math.Min(30, request.Text.Length)]}... -> {request.OutputAudioPath}");

        var engine = GetOrCreateEngine();
        var wavBytes = await engine.SynthesizeAsync(
            request.Text,
            request.Language ?? "en",
            request.ReferenceAudioPath,
            targetDurationSeconds: request.TargetDurationSeconds,
            cancellationToken).ConfigureAwait(false);

        var outputDir = Path.GetDirectoryName(request.OutputAudioPath);
        if (!string.IsNullOrWhiteSpace(outputDir))
            Directory.CreateDirectory(outputDir);
        await File.WriteAllBytesAsync(request.OutputAudioPath, wavBytes, cancellationToken).ConfigureAwait(false);

        double durationSeconds = wavBytes.Length > 44
            ? (wavBytes.Length - 44) / 2.0 / ChatterboxTtsEngine.SampleRate
            : 0;
        _log.Debug($"Chatterbox segment TTS completed: {request.OutputAudioPath}");
        return new TtsResult(true, request.OutputAudioPath, request.VoiceName, wavBytes.Length, null, durationSeconds);
    }

    public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null)
    {
        if (!ModelDownloader.IsChatterboxModelDownloaded(settings.ChatterboxModelDir))
        {
            return new ProviderReadiness(
                false,
                "Chatterbox voice cloning model is not downloaded yet.",
                RequiresModelDownload: true,
                ModelDownloadDescription: "Download Chatterbox multilingual voice cloning model");
        }

        if (!_consentGranted && !settings.ChatterboxVoiceCloneConsent)
        {
            return new ProviderReadiness(
                false,
                "Voice cloning consent has not been granted for Chatterbox.");
        }

        return ProviderReadiness.Ready;
    }

    public async Task<bool> EnsureReadyAsync(AppSettings settings, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (!ModelDownloader.IsChatterboxModelDownloaded(settings.ChatterboxModelDir))
        {
            Log.Debug("Chatterbox model requires download. Starting download...");
            return await new ModelDownloader(Log).DownloadChatterboxModelAsync(settings.ChatterboxModelDir, progress, ct);
        }

        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        lock (_gate)
        {
            _engine?.Dispose();
            _engine = null;
        }

        if (_ownsExtractor)
        {
            Task.Run(() => _extractor.DisposeAsync().AsTask()).GetAwaiter().GetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        lock (_gate)
        {
            _engine?.Dispose();
            _engine = null;
        }

        if (_ownsExtractor)
            await _extractor.DisposeAsync().ConfigureAwait(false);
    }

    private ChatterboxTtsEngine GetOrCreateEngine()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _engine ??= new ChatterboxTtsEngine(Log, ModelDownloader.ResolveChatterboxModelDir(_modelDir));
            return _engine;
        }
    }

    private async Task<string?> EnsureSourceLanguageReferenceAsync(
        SingleSegmentTtsRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.SourceVideoPath) || !File.Exists(request.SourceVideoPath))
            return null;

        double start = Math.Max(0d, request.SourceStartSeconds ?? 0d);
        double duration = request.SourceEndSeconds is double end && end > start
            ? Math.Min(TtsReferenceExtractor.MaxDurationSeconds, Math.Max(end - start, 3d))
            : TtsReferenceExtractor.DefaultDurationSeconds;

        _log.Info(
            $"Chatterbox cloning from source-language audio at {start:0.##}s ({duration:0.##}s window).");
        return await _extractor.ExtractReferenceAsync(
            request.SourceVideoPath,
            start,
            duration,
            cancellationToken).ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(ChatterboxTtsProvider));
    }

    private static string? ResolveReferenceAudioPath(TtsRequest request, string? speakerId)
    {
        if (speakerId is not null &&
            request.SpeakerReferenceAudioPaths is not null &&
            request.SpeakerReferenceAudioPaths.TryGetValue(speakerId, out var path) &&
            File.Exists(path))
        {
            return path;
        }

        if (!string.IsNullOrWhiteSpace(request.DefaultVoiceFallback) && File.Exists(request.DefaultVoiceFallback))
            return request.DefaultVoiceFallback;

        return null;
    }

    private AppLog Log => _log;
}
