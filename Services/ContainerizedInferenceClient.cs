using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Babel.Player.Models;

namespace Babel.Player.Services;

public sealed class ContainerizedInferenceClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly AppLog _log;
    private readonly string _inferenceServiceUrl;
    private readonly ContainerizedRequestLeaseTracker? _requestLeaseTracker;
    private readonly bool _disposeHttpClient;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = false };
    private static readonly string DebugLogPath = ResolveDebugLogPath();

    /// <summary>
    /// Initializes a new ContainerizedInferenceClient for the specified inference service URL and logger, using a default HttpClient and no request lease tracker.
    /// </summary>
    /// <param name="inferenceServiceUrl">The base URL of the containerized inference service; if null or empty, "http://localhost:8000" is used.</param>
    public ContainerizedInferenceClient(string inferenceServiceUrl, AppLog log)
        : this(inferenceServiceUrl, log, null, null)
    {
    }

    /// <summary>
    /// Initializes a ContainerizedInferenceClient using the provided inference service URL and logging, optionally reusing an HttpClient.
    /// </summary>
    /// <param name="inferenceServiceUrl">Base URL of the containerized inference service; the value will be normalized (trimmed and trailing '/' removed) and defaults to "http://localhost:8000" when null or empty.</param>
    /// <param name="log">Logging instance used by the client.</param>
    /// <param name="httpClient">Optional HttpClient to reuse for requests; when null the client will create a new HttpClient with a 10-minute timeout.</param>
    public ContainerizedInferenceClient(string inferenceServiceUrl, AppLog log, HttpClient? httpClient)
        : this(inferenceServiceUrl, log, httpClient, null)
    {
    }

    /// <summary>
    /// Initializes a ContainerizedInferenceClient with the specified service URL, logger, optional HTTP client, and optional request-lease tracker.
    /// </summary>
    /// <param name="inferenceServiceUrl">Base URL of the containerized inference service; the value is normalized (trimmed and trailing slash removed). If null or empty, a default of "http://localhost:8000" is used.</param>
    /// <param name="log">Application logger used by the client.</param>
    /// <param name="httpClient">Optional HttpClient to use. If null, a new HttpClient with a 10-minute timeout is created.</param>
    /// <param name="requestLeaseTracker">Optional tracker for acquiring per-request leases to coordinate concurrency; when null, no leasing is performed.</param>
    public ContainerizedInferenceClient(
        string inferenceServiceUrl,
        AppLog log,
        HttpClient? httpClient,
        ContainerizedRequestLeaseTracker? requestLeaseTracker)
    {
        _inferenceServiceUrl = NormalizeBaseUrl(inferenceServiceUrl);
        _log = log;
        _disposeHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _requestLeaseTracker = requestLeaseTracker;
    }

    /// <summary>
    /// Checks the health of the configured containerized inference service.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the health probe.</param>
    /// <returns>A ContainerHealthStatus describing service availability, CUDA status/version, capabilities snapshot (if available), active request counts, busy state and reason, and any error or capabilities error messages.</returns>
    public Task<ContainerHealthStatus> CheckHealthAsync(
        CancellationToken cancellationToken = default) =>
        ProbeHealthAsync(_httpClient, _inferenceServiceUrl, cancellationToken);

    private static readonly HttpClient SharedProbeClient = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static async Task<ContainerHealthStatus> CheckHealthAsync(
        string serviceUrl,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            return await ProbeHealthAsync(SharedProbeClient, NormalizeBaseUrl(serviceUrl), cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var normalizedUrl = NormalizeBaseUrl(serviceUrl);
            return ContainerHealthStatus.Unavailable(normalizedUrl, ex.Message);
        }
    }

    /// <summary>
    /// Blocking health check intended for background-thread startup probes.
    /// Uses both /health/live and /capabilities so provider readiness is stage-aware.
    /// </summary>
    public static ContainerHealthStatus CheckHealth(string serviceUrl, int timeoutSeconds = 5)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            return ProbeHealthAsync(SharedProbeClient, NormalizeBaseUrl(serviceUrl), cts.Token)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            var normalizedUrl = NormalizeBaseUrl(serviceUrl);
            return ContainerHealthStatus.Unavailable(normalizedUrl, ex.Message);
        }
    }

    /// <summary>
    /// Transcribes an audio file using the containerized inference service.
    /// </summary>
    /// <param name="audioFilePath">Path to the audio file to transcribe; must exist.</param>
    /// <param name="modelName">Model identifier to use for transcription.</param>
    /// <param name="language">Optional hint for the transcription language.</param>
    /// <param name="cpuComputeType">CPU compute precision requested by the container (defaults to "int8" when blank).</param>
    /// <param name="cpuThreads">Number of CPU threads to request; included only when greater than 0.</param>
    /// <param name="numWorkers">Number of worker processes to request; values less than 1 are treated as 1.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A TranscriptionResult with Success=true and populated segments, Language, and LanguageProbability on success; otherwise Success=false and ErrorMessage populated.</returns>
    public async Task<TranscriptionResult> TranscribeAsync(
        string audioFilePath,
        string modelName = "base",
        string? language = null,
        string cpuComputeType = "int8",
        int cpuThreads = 0,
        int numWorkers = 1,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var lease = AcquireLease(ContainerizedRequestKind.Transcription);

            if (!File.Exists(audioFilePath))
                throw new FileNotFoundException($"Audio file not found: {audioFilePath}");

            _log.Debug($"Transcribing with containerized service: {audioFilePath}");

            using var content = new MultipartFormDataContent();
            await using var fileStream = new FileStream(audioFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, FileOptions.Asynchronous);
            content.Add(new StreamContent(fileStream), "file", Path.GetFileName(audioFilePath));
            content.Add(new StringContent(modelName), "model");
            if (language != null)
                content.Add(new StringContent(language), "language");
            content.Add(new StringContent(string.IsNullOrWhiteSpace(cpuComputeType) ? "int8" : cpuComputeType), "cpu_compute_type");
            if (cpuThreads > 0)
                content.Add(new StringContent(cpuThreads.ToString()), "cpu_threads");
            content.Add(new StringContent((numWorkers < 1 ? 1 : numWorkers).ToString()), "num_workers");

            using var response = await _httpClient.PostAsync(
                $"{_inferenceServiceUrl}/transcribe",
                content,
                cancellationToken);

            var result = await DeserializeResponseAsync<TranscriptionApiResponseDto>(response, cancellationToken);
            if (!result.Success)
                throw new InvalidOperationException($"Transcription error: {result.ErrorMessage}");

            var segments = new List<TranscriptSegment>();
            foreach (var seg in result.Segments ?? [])
            {
                if (!string.IsNullOrWhiteSpace(seg.Text))
                    segments.Add(new TranscriptSegment(seg.Start, seg.End, seg.Text));
            }

            _log.Debug($"Transcription complete: {segments.Count} segments");

            return new TranscriptionResult(
                true,
                segments,
                result.Language ?? "unknown",
                result.LanguageProbability,
                null);
        }
        catch (Exception ex)
        {
            _log.Error($"Transcription failed: {ex.Message}", ex);
            return new TranscriptionResult(false, [], "unknown", 0.0, ex.Message);
        }
    }

    internal async Task<TranscriptionResult> TranscribeStreamingAsync(
        string audioFilePath,
        ChannelWriter<TranscriptChannelItem> writer,
        string modelName = "base",
        string? language = null,
        string cpuComputeType = "int8",
        int cpuThreads = 0,
        int numWorkers = 1,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);

        try
        {
            using var lease = AcquireLease(ContainerizedRequestKind.Transcription);

            if (!File.Exists(audioFilePath))
                throw new FileNotFoundException($"Audio file not found: {audioFilePath}");

            _log.Debug($"Streaming transcription with containerized service: {audioFilePath}");

            using var content = new MultipartFormDataContent();
            await using var fileStream = new FileStream(audioFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, FileOptions.Asynchronous);
            content.Add(new StreamContent(fileStream), "file", Path.GetFileName(audioFilePath));
            content.Add(new StringContent(modelName), "model");
            if (language is not null)
                content.Add(new StringContent(language), "language");
            content.Add(new StringContent(string.IsNullOrWhiteSpace(cpuComputeType) ? "int8" : cpuComputeType), "cpu_compute_type");
            if (cpuThreads > 0)
                content.Add(new StringContent(cpuThreads.ToString()), "cpu_threads");
            content.Add(new StringContent((numWorkers < 1 ? 1 : numWorkers).ToString()), "num_workers");

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_inferenceServiceUrl}/transcribe/stream")
            {
                Content = content,
            };
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(payload)
                    ? $"HTTP {(int)response.StatusCode}"
                    : payload);
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            var sourceLanguage = language ?? "unknown";
            var languageProbability = 0d;
            var peakRamMb = -1d;
            var peakVramMb = -1d;
            var segments = new List<TranscriptSegment>();
            var stopwatch = Stopwatch.StartNew();

            while (true)
            {
                var line = await reader.ReadLineAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                    break;
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var evt = JsonSerializer.Deserialize<StreamingTranscriptionEventDto>(line, JsonOptions)
                    ?? throw new InvalidOperationException("Failed to deserialize streaming transcription event.");

                if (string.Equals(evt.Type, "metadata", StringComparison.Ordinal))
                {
                    sourceLanguage = string.IsNullOrWhiteSpace(evt.Language) ? sourceLanguage : evt.Language;
                    languageProbability = evt.LanguageProbability;
                    continue;
                }

                if (string.Equals(evt.Type, "complete", StringComparison.Ordinal))
                {
                    peakRamMb = evt.PeakRamMb;
                    peakVramMb = evt.PeakVramMb;
                    continue;
                }

                if (!string.Equals(evt.Type, "segment", StringComparison.Ordinal) || evt.Segment is null)
                    continue;

                var text = evt.Segment.Text?.Trim();
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                var artifactSegment = new TranscriptSegmentArtifact
                {
                    Start = evt.Segment.Start,
                    End = evt.Segment.End,
                    Text = text,
                    Words = evt.Segment.Words is null
                        ? null
                        : [.. evt.Segment.Words
                            .Where(word => !string.IsNullOrWhiteSpace(word.Text))
                            .Select(word => new WordTimestamp(word.Text!, word.Start, word.End))],
                };
                var segmentId = SessionWorkflowCoordinator.SegmentId(artifactSegment.Start);
                segments.Add(new TranscriptSegment(artifactSegment.Start, artifactSegment.End, text));
                await writer.WriteAsync(
                    new TranscriptChannelItem(segmentId, artifactSegment, sourceLanguage, languageProbability),
                    cancellationToken).ConfigureAwait(false);
            }

            stopwatch.Stop();
            _log.Debug($"Streaming transcription complete: {segments.Count} segments");

            return new TranscriptionResult(
                true,
                segments,
                sourceLanguage,
                languageProbability,
                null,
                ElapsedMs: stopwatch.ElapsedMilliseconds,
                PeakVramMb: peakVramMb,
                PeakRamMb: peakRamMb);
        }
        catch (Exception ex)
        {
            _log.Error($"Streaming transcription failed: {ex.Message}", ex);
            return new TranscriptionResult(false, [], language ?? "unknown", 0.0, ex.Message);
        }
        finally
        {
            writer.TryComplete();
        }
    }

    /// <summary>
    /// Transcribes an audio file using the Parakeet TDT model via the containerized inference service (<c>/transcribe/parakeet</c>).
    /// </summary>
    /// <param name="audioFilePath">Path to the audio file to transcribe; must exist.</param>
    /// <param name="language">Optional language hint (informational; Parakeet is English-only).</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A <see cref="TranscriptionResult"/> with Success=true and populated segments on success; otherwise Success=false and ErrorMessage populated.</returns>
    public async Task<TranscriptionResult> TranscribeParakeetAsync(
        string audioFilePath,
        string? language = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var lease = AcquireLease(ContainerizedRequestKind.Transcription);

            if (!File.Exists(audioFilePath))
                throw new FileNotFoundException($"Audio file not found: {audioFilePath}");

            _log.Debug($"Transcribing with Parakeet: {audioFilePath}");

            using var content = new MultipartFormDataContent();
            await using var fileStream = new FileStream(
                audioFilePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 4096, FileOptions.Asynchronous);
            content.Add(new StreamContent(fileStream), "file", Path.GetFileName(audioFilePath));
            if (language != null)
                content.Add(new StringContent(language), "language");

            using var response = await _httpClient.PostAsync(
                $"{_inferenceServiceUrl}/transcribe/parakeet",
                content,
                cancellationToken);

            var result = await DeserializeResponseAsync<TranscriptionApiResponseDto>(response, cancellationToken);
            if (!result.Success)
                throw new InvalidOperationException($"Parakeet transcription error: {result.ErrorMessage}");

            var segments = new List<TranscriptSegment>();
            foreach (var seg in result.Segments ?? [])
            {
                if (!string.IsNullOrWhiteSpace(seg.Text))
                    segments.Add(new TranscriptSegment(seg.Start, seg.End, seg.Text));
            }

            _log.Debug($"Parakeet transcription complete: {segments.Count} segments");

            return new TranscriptionResult(
                true,
                segments,
                result.Language ?? "en",
                result.LanguageProbability,
                null);
        }
        catch (Exception ex)
        {
            _log.Error($"Parakeet transcription failed: {ex.Message}", ex);
            return new TranscriptionResult(false, [], "en", 0.0, ex.Message);
        }
    }

    /// <summary>
    /// Translates a serialized transcript from a source language into a target language using the specified model.
    /// </summary>
    /// <param name="transcriptJson">JSON-serialized transcript to translate (expected format produced by the transcription endpoint).</param>
    /// <param name="sourceLanguage">Language code of the input transcript.</param>
    /// <param name="targetLanguage">Language code to translate the transcript into.</param>
    /// <param name="model">Identifier of the translation model to use.</param>
    /// <param name="cancellationToken">Token to cancel the request.</param>
    /// <returns>A <see cref="TranslationResult"/> containing success state, translated segments, resolved source and target language codes, and an error message when unsuccessful.</returns>
    public async Task<TranslationResult> TranslateAsync(
        string transcriptJson,
        string sourceLanguage,
        string targetLanguage,
        string model,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var lease = AcquireLease(ContainerizedRequestKind.Translation);

            _log.Debug($"Translating {sourceLanguage} -> {targetLanguage}");

            using var content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("transcript_json", transcriptJson),
                new KeyValuePair<string, string>("source_language", sourceLanguage),
                new KeyValuePair<string, string>("target_language", targetLanguage),
                new KeyValuePair<string, string>("model", model),
            ]);

            using var response = await _httpClient.PostAsync(
                $"{_inferenceServiceUrl}/translate",
                content,
                cancellationToken);

            var result = await DeserializeResponseAsync<TranslationApiResponseDto>(response, cancellationToken);
            if (!result.Success)
                throw new InvalidOperationException($"Translation error: {result.ErrorMessage}");

            var translatedSegments = new List<TranslatedSegment>();
            foreach (var seg in result.Segments ?? [])
            {
                translatedSegments.Add(new TranslatedSegment(
                    seg.Start,
                    seg.End,
                    seg.Text ?? string.Empty,
                    seg.TranslatedText ?? string.Empty,
                    SpeakerId: string.IsNullOrWhiteSpace(seg.SpeakerId) ? null : seg.SpeakerId));
            }

            _log.Debug($"Translation complete: {translatedSegments.Count} segments");

            return new TranslationResult(
                true,
                translatedSegments,
                result.SourceLanguage ?? sourceLanguage,
                result.TargetLanguage ?? targetLanguage,
                null);
        }
        catch (Exception ex)
        {
            _log.Error($"Translation failed: {ex.Message}", ex);
            return new TranslationResult(false, [], sourceLanguage, targetLanguage, ex.Message);
        }
    }

    /// <summary>
    /// Generate speech audio from the provided text using the containerized TTS service.
    /// </summary>
    /// <param name="text">The text to synthesize into speech.</param>
    /// <param name="voice">The voice identifier to use for synthesis (defaults to "en-US-AriaNeural").</param>
    /// <param name="cancellationToken">Cancellation token to cancel the request.</param>
    /// <returns>A TtsResult where Success is true on success and contains the remote audio path, resolved voice, and file size in bytes; on failure Success is false and Error contains the failure message.</returns>
    public async Task<TtsResult> TextToSpeechAsync(
        string text,
        string voice = "en-US-AriaNeural",
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var lease = AcquireLease(ContainerizedRequestKind.Tts);

            _log.Debug($"Generating TTS with voice: {voice}");

            using var content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("text", text),
                new KeyValuePair<string, string>("voice", voice)
            ]);

            using var response = await _httpClient.PostAsync(
                $"{_inferenceServiceUrl}/tts",
                content,
                cancellationToken);

            var result = await DeserializeResponseAsync<TtsApiResponseDto>(response, cancellationToken);
            if (!result.Success)
                throw new InvalidOperationException($"TTS error: {result.ErrorMessage}");

            _log.Debug($"TTS generation complete: {result.FileSizeBytes} bytes");

            return new TtsResult(true, result.AudioPath ?? "", result.Voice ?? "", result.FileSizeBytes, null);
        }
        catch (Exception ex)
        {
            _log.Error($"TTS failed: {ex.Message}", ex);
            return new TtsResult(false, "", "", 0, ex.Message);
        }
    }

    /// <summary>
    /// Performs speaker diarization on a local audio file using the containerized inference service.
    /// </summary>
    /// <param name="audioFilePath">Path to an existing audio file to diarize.</param>
    /// <param name="engine">Identifier of the diarization provider to request.</param>
    /// <param name="minSpeakers">Optional minimum number of speakers to detect.</param>
    /// <param name="maxSpeakers">Optional maximum number of speakers to detect.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <returns>A <see cref="DiarizationResult"/> containing a success flag, the list of normalized diarization segments, the determined speaker count, and an error message when <see cref="DiarizationResult.Success"/> is <c>false</c>.</returns>
    public async Task<DiarizationResult> DiarizeAsync(
        string audioFilePath,
        string engine,
        int? minSpeakers = null,
        int? maxSpeakers = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var lease = AcquireLease(ContainerizedRequestKind.Diarization);

            if (!File.Exists(audioFilePath))
                throw new FileNotFoundException($"Audio file not found: {audioFilePath}");

            var normalizedEngine = NormalizeDiarizationEngine(engine);
            if (!string.Equals(normalizedEngine, ProviderNames.NemoLocal, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Containerized diarization only supports {ProviderNames.NemoLocal}; '{normalizedEngine}' now runs via the managed CPU runtime.");
            }

            const string endpoint = "/diarize";
            _log.Debug($"Diarizing with containerized service: {audioFilePath} (engine={normalizedEngine})");

            using var content = new MultipartFormDataContent();
            await using var fileStream = new FileStream(audioFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, FileOptions.Asynchronous);
            content.Add(new StreamContent(fileStream), "audio", Path.GetFileName(audioFilePath));
            if (minSpeakers.HasValue)
                content.Add(new StringContent(minSpeakers.Value.ToString()), "min_speakers");
            if (maxSpeakers.HasValue)
                content.Add(new StringContent(maxSpeakers.Value.ToString()), "max_speakers");

            using var response = await _httpClient.PostAsync(
                $"{_inferenceServiceUrl}{endpoint}",
                content,
                cancellationToken);

            var result = await DeserializeResponseAsync<DiarizationApiResponseDto>(response, cancellationToken);
            if (!result.Success)
                throw new InvalidOperationException($"Diarization error: {result.ErrorMessage}");

            var normalizedSegments = NormalizeDiarizationSegments(result.Segments ?? []);
            var speakerCount = result.SpeakerCount > 0
                ? result.SpeakerCount
                : CountDistinctSpeakers(normalizedSegments);

            return new DiarizationResult(true, normalizedSegments, speakerCount, null);
        }
        catch (Exception ex)
        {
            _log.Error($"Diarization failed: {ex.Message}", ex);
            return new DiarizationResult(false, [], 0, ex.Message);
        }
    }

    public async Task<VocalSeparationResult> SeparateVocalsAsync(
        string audioPath,
        string? outputDirectoryPath = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(audioPath))
                throw new FileNotFoundException($"Audio file not found: {audioPath}");

            _log.Debug($"Separating vocals with containerized service: {audioPath}");

            SeparateVocalsApiResponseDto result;
            using (var lease = AcquireLease(ContainerizedRequestKind.Other))
            {
                using var content = new MultipartFormDataContent();
                await using var fileStream = new FileStream(audioPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, FileOptions.Asynchronous);
                content.Add(new StreamContent(fileStream), "file", Path.GetFileName(audioPath));

                using var response = await _httpClient.PostAsync(
                    $"{_inferenceServiceUrl}/separate/vocals",
                    content,
                    cancellationToken).ConfigureAwait(false);

                result = await DeserializeResponseAsync<SeparateVocalsApiResponseDto>(response, cancellationToken).ConfigureAwait(false);
            }

            if (!result.Success)
                throw new InvalidOperationException(
                    $"Vocal separation error: {result.ErrorMessage ?? "unknown"}");

            if (string.IsNullOrWhiteSpace(result.VocalsFilename) || string.IsNullOrWhiteSpace(result.InstrumentalFilename))
                throw new InvalidOperationException("Vocal separation response did not include stem filenames.");

            var stemsDir = outputDirectoryPath;
            if (string.IsNullOrWhiteSpace(stemsDir))
            {
                var audioDir = Path.GetDirectoryName(audioPath);
                if (string.IsNullOrWhiteSpace(audioDir))
                    throw new InvalidOperationException($"Could not resolve source audio directory from '{audioPath}'.");

                var sessionDir = Directory.GetParent(audioDir)?.FullName;
                if (string.IsNullOrWhiteSpace(sessionDir))
                    sessionDir = audioDir;
                stemsDir = Path.Combine(sessionDir, "stems");
            }
            Directory.CreateDirectory(stemsDir);

            var vocalsPath = Path.Combine(stemsDir, Path.GetFileName(result.VocalsFilename));
            var ambiancePath = Path.Combine(stemsDir, Path.GetFileName(result.InstrumentalFilename));

            // Downloads are separate requests — each acquires its own lease (no outer lease held).
            await DownloadTtsAudioAsync(
                result.VocalsFilename,
                vocalsPath,
                cancellationToken,
                requestKind: ContainerizedRequestKind.Other).ConfigureAwait(false);
            await DownloadTtsAudioAsync(
                result.InstrumentalFilename,
                ambiancePath,
                cancellationToken,
                requestKind: ContainerizedRequestKind.Other).ConfigureAwait(false);

            return new VocalSeparationResult(
                Success: true,
                VocalsAudioPath: vocalsPath,
                AmbianceAudioPath: ambiancePath,
                ErrorMessage: null,
                VocalsFileSizeBytes: result.VocalsFileSizeBytes,
                AmbianceFileSizeBytes: result.InstrumentalFileSizeBytes,
                VocalsModel: result.VocalsModel,
                AmbianceModel: result.InstrumentalModel);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"Vocal separation failed: {ex.Message}", ex);
            return new VocalSeparationResult(false, string.Empty, string.Empty, ex.Message);
        }
    }

    /// <summary>
    /// Registers a Qwen voice reference audio for a speaker and returns the created reference ID.
    /// </summary>
    /// <param name="speakerId">Identifier of the speaker to associate with the reference.</param>
    /// <param name="referenceAudioPath">Path to the local reference audio file to upload.</param>
    /// <returns>The reference ID assigned by the service.</returns>
    /// <exception cref="FileNotFoundException">Thrown if <paramref name="referenceAudioPath"/> does not exist.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the service reports a failure or returns no reference ID.</exception>
    public async Task<string> RegisterQwenReferenceAsync(
        string speakerId,
        string referenceAudioPath,
        CancellationToken cancellationToken = default)
    {
        using var lease = AcquireLease(ContainerizedRequestKind.Qwen);

        if (!File.Exists(referenceAudioPath))
            throw new FileNotFoundException($"Reference audio file not found: {referenceAudioPath}");

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(speakerId), "speaker_id");

        await using var fs = new FileStream(referenceAudioPath, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read, BufferSize = 4096, Options = FileOptions.Asynchronous | FileOptions.SequentialScan });
        content.Add(new StreamContent(fs), "file", Path.GetFileName(referenceAudioPath));

        using var response = await _httpClient.PostAsync(
            $"{_inferenceServiceUrl}/tts/qwen/references",
            content,
            cancellationToken);

        var result = await DeserializeResponseAsync<QwenReferenceResponseDto>(response, cancellationToken);
        if (!result.Success || string.IsNullOrWhiteSpace(result.ReferenceId))
            throw new InvalidOperationException($"Qwen reference registration failed: {result.ErrorMessage}");

        return result.ReferenceId;
    }



    /// <summary>
    /// Generate a Qwen-segmented text-to-speech audio asset for the given text using an optional reference audio or reference ID.
    /// </summary>
    /// <param name="text">The input text to synthesize.</param>
    /// <param name="model">The TTS model id to use; when null or whitespace the default "Qwen/Qwen3-TTS-12Hz-1.7B-Base" is used.</param>
    /// <param name="language">Optional language hint for synthesis.</param>
    /// <param name="referenceAudioPath">Optional path to a reference audio file; used only when <paramref name="referenceId"/> is not provided and must exist.</param>
    /// <param name="referenceText">Optional reference text associated with the reference audio.</param>
    /// <param name="referenceId">Optional pre-registered reference identifier; when provided, the reference audio file is not uploaded.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <returns>
    /// A <see cref="TtsResult"/> containing the synthesized audio path, resolved voice/model, file size, and an error message when <see cref="TtsResult.Success"/> is false.
    /// </returns>
    public async Task<TtsResult> QwenSegmentAsync(
        string text,
        string model,
        string? language = null,
        string? referenceAudioPath = null,
        string? referenceText = null,
        string? referenceId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var lease = AcquireLease(ContainerizedRequestKind.Qwen);

            using var content = new MultipartFormDataContent();
            content.Add(new StringContent(text), "text");
            content.Add(new StringContent(string.IsNullOrWhiteSpace(model) ? "Qwen/Qwen3-TTS-12Hz-1.7B-Base" : model), "model");
            if (!string.IsNullOrWhiteSpace(language))
                content.Add(new StringContent(language), "language");
            if (!string.IsNullOrWhiteSpace(referenceText))
                content.Add(new StringContent(referenceText), "reference_text");
            if (!string.IsNullOrWhiteSpace(referenceId))
                content.Add(new StringContent(referenceId), "reference_id");

            FileStream? fs = null;
            try
            {
                if (string.IsNullOrWhiteSpace(referenceId) && !string.IsNullOrWhiteSpace(referenceAudioPath))
                {
                    if (!File.Exists(referenceAudioPath))
                        throw new FileNotFoundException($"Reference audio file not found: {referenceAudioPath}");
                    fs = new FileStream(referenceAudioPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, FileOptions.Asynchronous);
                    content.Add(new StreamContent(fs), "reference_file", Path.GetFileName(referenceAudioPath));
                }

                using var response = await _httpClient.PostAsync(
                    $"{_inferenceServiceUrl}/tts/qwen/segment",
                    content,
                    cancellationToken);

                var result = await DeserializeResponseAsync<TtsApiResponseDto>(response, cancellationToken);
                if (!result.Success)
                    throw new InvalidOperationException($"Qwen TTS segment error: {result.ErrorMessage}");

                return new TtsResult(true, result.AudioPath ?? "", result.Voice ?? model, result.FileSizeBytes, null);
            }
            finally
            {
                if (fs is not null)
                    await fs.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Qwen TTS segment synthesis failed: {ex.Message}", ex);
            return new TtsResult(false, "", string.IsNullOrWhiteSpace(model) ? "Qwen/Qwen3-TTS-12Hz-1.7B-Base" : model, 0, ex.Message);
        }
    }

    public async Task<IReadOnlyList<QwenBatchSegmentResult>> QwenBatchAsync(
        string model,
        IReadOnlyList<QwenBatchSegmentPayload> segments,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var lease = AcquireLease(ContainerizedRequestKind.Qwen);

            var payload = new QwenBatchRequestPayloadDto(
                string.IsNullOrWhiteSpace(model) ? "Qwen/Qwen3-TTS-12Hz-1.7B-Base" : model,
                segments.Select(segment => new QwenBatchSegmentPayloadDto(
                    segment.SegmentId,
                    segment.Text,
                    segment.Language,
                    segment.ReferenceId)).ToList());

            using var content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOptions),
                Encoding.UTF8,
                "application/json");

            using var response = await _httpClient.PostAsync(
                $"{_inferenceServiceUrl}/tts/qwen/batch",
                content,
                cancellationToken);

            var result = await DeserializeResponseAsync<QwenBatchResponseDto>(response, cancellationToken);
            if (!result.Success)
                throw new InvalidOperationException($"Qwen TTS batch error: {result.ErrorMessage}");

            return result.Segments?
                .Where(segment => !string.IsNullOrWhiteSpace(segment.SegmentId))
                .Select(segment => new QwenBatchSegmentResult(
                    segment.SegmentId!,
                    new TtsResult(
                        true,
                        segment.AudioPath ?? string.Empty,
                        segment.Voice ?? model,
                        segment.FileSizeBytes,
                        null)))
                .ToList()
                ?? [];
        }
        catch (Exception ex)
        {
            _log.Error($"Qwen TTS batch synthesis failed: {ex.Message}", ex);
            return [];
        }
    }

    /// <summary>
    /// Downloads a TTS audio file from the containerized inference service and saves it to the specified local path.
    /// </summary>
    /// <param name="filename">The remote audio filename to request from the service.</param>
    /// <param name="localOutputPath">The local filesystem path where the downloaded audio will be saved (created or overwritten).</param>
    /// <param name="cancellationToken">Token to cancel the download operation.</param>
    /// <param name="requestKind">Lease tracker request kind associated with this download.</param>
    /// <exception cref="InvalidOperationException">Thrown when the service responds with a non-success status; the exception message contains the response body.</exception>
    public async Task DownloadTtsAudioAsync(
        string filename,
        string localOutputPath,
        CancellationToken cancellationToken = default,
        ContainerizedRequestKind requestKind = ContainerizedRequestKind.Tts)
    {
        using var lease = AcquireLease(requestKind);
        var normalizedFilename = ExtractServerFilename(filename);

        using var response = await _httpClient.GetAsync(
            $"{_inferenceServiceUrl}/tts/audio/{Uri.EscapeDataString(normalizedFilename)}",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Failed to download TTS audio '{normalizedFilename}': {error}");
        }

        using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var fileStream = new FileStream(
            localOutputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);
        await contentStream.CopyToAsync(fileStream, cancellationToken);
    }

    private static string ExtractServerFilename(string pathOrFilename)
    {
        if (string.IsNullOrWhiteSpace(pathOrFilename))
            throw new InvalidOperationException("Cannot download TTS audio without a server filename.");

        var trimmed = pathOrFilename.Trim();
        var lastSlash = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
        var filename = lastSlash >= 0 ? trimmed[(lastSlash + 1)..] : trimmed;

        if (string.IsNullOrWhiteSpace(filename))
            throw new InvalidOperationException($"Cannot extract filename from server audio path: '{pathOrFilename}'");

        return filename;
    }

    /// <summary>
    /// Probes the containerized inference service at the specified base URL for liveness and reported capabilities.
    /// </summary>
    /// <param name="httpClient">The HTTP client used to perform the probe requests.</param>
    /// <param name="serviceUrl">Base URL of the inference service to probe.</param>
    /// <param name="cancellationToken">Cancellation token to abort the probe requests.</param>
    /// <returns>A <see cref="ContainerHealthStatus"/> describing availability, CUDA status, active request counts, busy state, and either a capabilities snapshot or a capabilities error when capability probing failed; returns an unavailable status if the probe fails.</returns>
    private static async Task<ContainerHealthStatus> ProbeHealthAsync(
        HttpClient httpClient,
        string serviceUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            using var liveResponse = await httpClient.GetAsync(
                $"{serviceUrl}/health/live",
                cancellationToken).ConfigureAwait(false);
            var live = await DeserializeResponseAsync<LiveHealthResponseDto>(liveResponse, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(live.Status, "healthy", StringComparison.OrdinalIgnoreCase))
                return ContainerHealthStatus.Unavailable(serviceUrl, $"Unexpected live status '{live.Status ?? "unknown"}'.");

            ContainerCapabilitiesSnapshot? capabilities = null;
            string? capabilitiesError = null;
            try
            {
                using var capabilitiesResponse = await httpClient.GetAsync(
                    $"{serviceUrl}/capabilities",
                    cancellationToken).ConfigureAwait(false);
                var capabilitiesDto = await DeserializeResponseAsync<CapabilitiesResponseDto>(capabilitiesResponse, cancellationToken).ConfigureAwait(false);
                capabilities = new ContainerCapabilitiesSnapshot(
                    capabilitiesDto.Transcription?.Ready ?? false,
                    capabilitiesDto.Transcription?.Detail,
                    capabilitiesDto.Translation?.Ready ?? false,
                    capabilitiesDto.Translation?.Detail,
                    capabilitiesDto.Tts?.Ready ?? false,
                    capabilitiesDto.Tts?.Detail,
                    capabilitiesDto.Tts?.Providers,
                    capabilitiesDto.Tts?.ProviderDetails,
                    capabilitiesDto.Diarization?.Ready ?? false,
                    capabilitiesDto.Diarization?.Detail,
                    NormalizeDiarizationProviderReadiness(capabilitiesDto.Diarization?.Providers),
                    NormalizeDiarizationProviderDetails(capabilitiesDto.Diarization?.ProviderDetails),
                    NormalizeDiarizationDefaultProvider(capabilitiesDto.Diarization?.DefaultProvider),
                    TtsProviderHealth: NormalizeProviderHealthMap(capabilitiesDto.Tts?.ProviderHealth),
                    DiarizationProviderHealth: NormalizeDiarizationProviderHealth(capabilitiesDto.Diarization?.ProviderHealth),
                    VocalSeparationReady: capabilitiesDto.VocalSeparation?.Ready ?? false,
                    VocalSeparationDetail: capabilitiesDto.VocalSeparation?.Detail);
            }
            catch (Exception ex)
            {
                capabilitiesError = ex.Message;
            }

            return new ContainerHealthStatus(
                IsAvailable: true,
                CudaAvailable: live.CudaAvailable,
                CudaVersion: live.CudaVersion,
                ServiceUrl: serviceUrl,
                ErrorMessage: null,
                Capabilities: capabilities,
                CapabilitiesError: capabilitiesError,
                ActiveRequests: live.ActiveRequests,
                ActiveQwenRequests: live.ActiveQwenRequests,
                ActiveDiarizationRequests: live.ActiveDiarizationRequests,
                Busy: live.Busy,
                BusyReason: live.BusyReason,
                ProviderHealth: NormalizeProviderHealthMap(live.ProviderHealth),
                QwenMaxConcurrency: live.QwenMaxConcurrency,
                QwenQueueDepth: live.QwenQueueDepth,
                QwenLastQueueWaitMs: live.QwenLastQueueWaitMs,
                QwenLastGenerationMs: live.QwenLastGenerationMs,
                QwenLastReferencePrepMs: live.QwenLastReferencePrepMs,
                QwenLastWarmupMs: live.QwenLastWarmupMs);
        }
        catch (Exception ex)
        {
            return ContainerHealthStatus.Unavailable(serviceUrl, ex.Message);
        }
    }

    /// <summary>
    /// Deserialize an HTTP response JSON payload into an instance of <typeparamref name="T"/> and ensure the response indicates success.
    /// </summary>
    /// <param name="response">The HTTP response whose JSON body will be read and deserialized.</param>
    /// <param name="cancellationToken">Token to cancel reading the response body.</param>
    /// <returns>The deserialized object of type <typeparamref name="T"/>.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the HTTP response status is not successful (message contains the response body or status code) or if JSON deserialization fails.</exception>
    private static async Task<T> DeserializeResponseAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(payload)
                ? $"HTTP {(int)response.StatusCode}"
                : payload);

        return JsonSerializer.Deserialize<T>(payload, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize {typeof(T).Name} from service response.");
    }

    public static string NormalizeBaseUrl(string? serviceUrl)
    {
        if (string.IsNullOrWhiteSpace(serviceUrl))
            return "http://localhost:8000";

        return serviceUrl.Trim().TrimEnd('/');
    }

    private sealed class LiveHealthResponseDto
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("cuda_available")]
        public bool CudaAvailable { get; set; }

        [JsonPropertyName("cuda_version")]
        public string? CudaVersion { get; set; }

        [JsonPropertyName("active_requests")]
        public int ActiveRequests { get; set; }

        [JsonPropertyName("active_qwen_requests")]
        public int ActiveQwenRequests { get; set; }

        [JsonPropertyName("active_diarization_requests")]
        public int ActiveDiarizationRequests { get; set; }

        [JsonPropertyName("busy")]
        public bool Busy { get; set; }

        [JsonPropertyName("busy_reason")]
        public string? BusyReason { get; set; }

        [JsonPropertyName("provider_health")]
        public Dictionary<string, ProviderHealthSnapshotDto>? ProviderHealth { get; set; }

        [JsonPropertyName("qwen_max_concurrency")]
        public int QwenMaxConcurrency { get; set; }

        [JsonPropertyName("qwen_queue_depth")]
        public int QwenQueueDepth { get; set; }

        [JsonPropertyName("qwen_last_queue_wait_ms")]
        public double? QwenLastQueueWaitMs { get; set; }

        [JsonPropertyName("qwen_last_generation_ms")]
        public double? QwenLastGenerationMs { get; set; }

        [JsonPropertyName("qwen_last_reference_prep_ms")]
        public double? QwenLastReferencePrepMs { get; set; }

        [JsonPropertyName("qwen_last_warmup_ms")]
        public double? QwenLastWarmupMs { get; set; }
    }

    private sealed class CapabilitiesResponseDto
    {
        [JsonPropertyName("transcription")]
        public StageCapabilityDto? Transcription { get; set; }

        [JsonPropertyName("translation")]
        public StageCapabilityDto? Translation { get; set; }

        [JsonPropertyName("tts")]
        public StageCapabilityDto? Tts { get; set; }

        [JsonPropertyName("diarization")]
        public StageCapabilityDto? Diarization { get; set; }

        [JsonPropertyName("vocal_separation")]
        public StageCapabilityDto? VocalSeparation { get; set; }
    }

    private sealed class StageCapabilityDto
    {
        [JsonPropertyName("ready")]
        public bool Ready { get; set; }

        [JsonPropertyName("detail")]
        public string? Detail { get; set; }

        [JsonPropertyName("providers")]
        public Dictionary<string, bool>? Providers { get; set; }

        [JsonPropertyName("provider_details")]
        public Dictionary<string, string>? ProviderDetails { get; set; }

        [JsonPropertyName("default_provider")]
        public string? DefaultProvider { get; set; }

        [JsonPropertyName("provider_health")]
        public Dictionary<string, ProviderHealthSnapshotDto>? ProviderHealth { get; set; }
    }

    private sealed class ProviderHealthSnapshotDto
    {
        [JsonPropertyName("ready")]
        public bool Ready { get; set; }

        [JsonPropertyName("state")]
        public string? State { get; set; }

        [JsonPropertyName("detail")]
        public string? Detail { get; set; }

        [JsonPropertyName("checked_at")]
        public string? CheckedAt { get; set; }

        [JsonPropertyName("is_stale")]
        public bool IsStale { get; set; }

        [JsonPropertyName("failure_category")]
        public string? FailureCategory { get; set; }

        [JsonPropertyName("metrics")]
        public Dictionary<string, JsonElement>? Metrics { get; set; }

        [JsonPropertyName("history")]
        public List<ProviderHealthHistoryEntryDto>? History { get; set; }
    }

    private sealed class ProviderHealthHistoryEntryDto
    {
        [JsonPropertyName("timestamp")]
        public string? Timestamp { get; set; }

        [JsonPropertyName("state")]
        public string? State { get; set; }

        [JsonPropertyName("ready")]
        public bool Ready { get; set; }

        [JsonPropertyName("detail")]
        public string? Detail { get; set; }

        [JsonPropertyName("failure_category")]
        public string? FailureCategory { get; set; }
    }

    private sealed class TranscriptionApiResponseDto
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("language")]
        public string? Language { get; set; }

        [JsonPropertyName("language_probability")]
        public double LanguageProbability { get; set; }

        [JsonPropertyName("segments")]
        public List<TranscriptSegmentDto>? Segments { get; set; }

        [JsonPropertyName("error_message")]
        public string? ErrorMessage { get; set; }
    }

    private sealed class TranscriptSegmentDto
    {
        [JsonPropertyName("start")]
        public double Start { get; set; }

        [JsonPropertyName("end")]
        public double End { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }

    private sealed class StreamingTranscriptionEventDto
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("language")]
        public string? Language { get; set; }

        [JsonPropertyName("language_probability")]
        public double LanguageProbability { get; set; }

        [JsonPropertyName("segment")]
        public StreamingTranscriptSegmentDto? Segment { get; set; }

        [JsonPropertyName("peak_ram_mb")]
        public double PeakRamMb { get; set; } = -1;

        [JsonPropertyName("peak_vram_mb")]
        public double PeakVramMb { get; set; } = -1;
    }

    private sealed class StreamingTranscriptSegmentDto
    {
        [JsonPropertyName("start")]
        public double Start { get; set; }

        [JsonPropertyName("end")]
        public double End { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("words")]
        public List<StreamingWordTimestampDto>? Words { get; set; }
    }

    private sealed class StreamingWordTimestampDto
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("start")]
        public double Start { get; set; }

        [JsonPropertyName("end")]
        public double End { get; set; }
    }

    private sealed class TranslationApiResponseDto
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("source_language")]
        public string? SourceLanguage { get; set; }

        [JsonPropertyName("target_language")]
        public string? TargetLanguage { get; set; }

        [JsonPropertyName("segments")]
        public List<TranslatedSegmentDto>? Segments { get; set; }

        [JsonPropertyName("error_message")]
        public string? ErrorMessage { get; set; }
    }

    private sealed class TranslatedSegmentDto
    {
        [JsonPropertyName("start")]
        public double Start { get; set; }

        [JsonPropertyName("end")]
        public double End { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("translated_text")]
        public string? TranslatedText { get; set; }

        [JsonPropertyName("speaker_id")]
        public string? SpeakerId { get; set; }
    }

    private sealed class TtsApiResponseDto
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("voice")]
        public string? Voice { get; set; }

        [JsonPropertyName("audio_path")]
        public string? AudioPath { get; set; }

        [JsonPropertyName("file_size_bytes")]
        public long FileSizeBytes { get; set; }

        [JsonPropertyName("error_message")]
        public string? ErrorMessage { get; set; }
    }

    private sealed class SeparateVocalsApiResponseDto
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("vocals_filename")]
        public string? VocalsFilename { get; set; }

        [JsonPropertyName("instrumental_filename")]
        public string? InstrumentalFilename { get; set; }

        [JsonPropertyName("vocals_file_size_bytes")]
        public long VocalsFileSizeBytes { get; set; }

        [JsonPropertyName("instrumental_file_size_bytes")]
        public long InstrumentalFileSizeBytes { get; set; }

        [JsonPropertyName("vocals_model")]
        public string? VocalsModel { get; set; }

        [JsonPropertyName("instrumental_model")]
        public string? InstrumentalModel { get; set; }

        [JsonPropertyName("error_message")]
        public string? ErrorMessage { get; set; }
    }

    private sealed class QwenReferenceResponseDto
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("reference_id")]
        public string? ReferenceId { get; set; }

        [JsonPropertyName("error_message")]
        public string? ErrorMessage { get; set; }
    }

    private sealed record QwenBatchRequestPayloadDto(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("segments")] List<QwenBatchSegmentPayloadDto> Segments);

    private sealed record QwenBatchSegmentPayloadDto(
        [property: JsonPropertyName("segment_id")] string SegmentId,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("language")] string? Language,
        [property: JsonPropertyName("reference_id")] string ReferenceId);

    private sealed class QwenBatchResponseDto
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("segments")]
        public List<QwenBatchSegmentResponseDto>? Segments { get; set; }

        [JsonPropertyName("error_message")]
        public string? ErrorMessage { get; set; }
    }

    private sealed class QwenBatchSegmentResponseDto
    {
        [JsonPropertyName("segment_id")]
        public string? SegmentId { get; set; }

        [JsonPropertyName("voice")]
        public string? Voice { get; set; }

        [JsonPropertyName("audio_path")]
        public string? AudioPath { get; set; }

        [JsonPropertyName("file_size_bytes")]
        public long FileSizeBytes { get; set; }
    }

    private sealed class DiarizationApiResponseDto
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("segments")]
        public List<DiarizationSegmentDto>? Segments { get; set; }

        [JsonPropertyName("speaker_count")]
        public int SpeakerCount { get; set; }

        [JsonPropertyName("error_message")]
        public string? ErrorMessage { get; set; }
    }

    private sealed class DiarizationSegmentDto
    {
        [JsonPropertyName("start")]
        public double Start { get; set; }

        [JsonPropertyName("end")]
        public double End { get; set; }

        [JsonPropertyName("speaker_id")]
        public string? SpeakerId { get; set; }
    }

    /// <summary>
    /// Normalize a diarization engine identifier to a supported canonical provider id.
    /// </summary>
    /// <param name="engine">The input engine identifier; accepted values include legacy aliases and canonical provider IDs.</param>
    /// <returns><see cref="ProviderNames.WeSpeakerLocal"/> when the input resolves to WeSpeaker; otherwise <see cref="ProviderNames.NemoLocal"/>.</returns>
    private static string NormalizeDiarizationEngine(string engine) =>
        InferenceRuntimeCatalog.NormalizeDiarizationCapabilityProviderId(engine) switch
    {
        ProviderNames.WeSpeakerLocal => ProviderNames.WeSpeakerLocal,
        _ => ProviderNames.NemoLocal,
    };

    /// <summary>
    /// Normalize the keys of a diarization provider readiness map using the runtime catalog's provider ID normalization.
    /// </summary>
    /// <param name="providers">Mapping of provider identifiers to their readiness state; keys will be normalized.</param>
    /// <returns>
    /// A dictionary with normalized provider IDs mapped to the same readiness values, or the original <c>null</c> / empty input if none was provided.
    /// </returns>
    private static IReadOnlyDictionary<string, bool>? NormalizeDiarizationProviderReadiness(
        IReadOnlyDictionary<string, bool>? providers)
    {
        if (providers is null || providers.Count == 0)
            return providers;

        var normalized = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var pair in providers)
            normalized[InferenceRuntimeCatalog.NormalizeDiarizationCapabilityProviderId(pair.Key)] = pair.Value;
        return normalized;
    }

    /// <summary>
    /// Produces a copy of the provider-details dictionary with each key normalized to a canonical diarization provider ID.
    /// </summary>
    /// <param name="providerDetails">A mapping of provider identifier to detail string; may be null or empty.</param>
    /// <returns>
    /// A new dictionary whose keys are the normalized provider IDs and whose values are the original detail strings,
    /// or the original <paramref name="providerDetails"/> if it is null or empty.
    /// </returns>
    private static IReadOnlyDictionary<string, string>? NormalizeDiarizationProviderDetails(
        IReadOnlyDictionary<string, string>? providerDetails)
    {
        if (providerDetails is null || providerDetails.Count == 0)
            return providerDetails;

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in providerDetails)
            normalized[InferenceRuntimeCatalog.NormalizeDiarizationCapabilityProviderId(pair.Key)] = pair.Value;
        return normalized;
    }

    private static IReadOnlyDictionary<string, ContainerProviderHealthSnapshot>? NormalizeProviderHealthMap(
        IReadOnlyDictionary<string, ProviderHealthSnapshotDto>? providerHealth)
    {
        if (providerHealth is null || providerHealth.Count == 0)
            return null;

        return providerHealth.ToDictionary(
            pair => pair.Key,
            pair => ToProviderHealthSnapshot(pair.Value),
            StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, ContainerProviderHealthSnapshot>? NormalizeDiarizationProviderHealth(
        IReadOnlyDictionary<string, ProviderHealthSnapshotDto>? providerHealth)
    {
        if (providerHealth is null || providerHealth.Count == 0)
            return null;

        var normalized = new Dictionary<string, ContainerProviderHealthSnapshot>(StringComparer.Ordinal);
        foreach (var pair in providerHealth)
            normalized[InferenceRuntimeCatalog.NormalizeDiarizationCapabilityProviderId(pair.Key ?? string.Empty)] = ToProviderHealthSnapshot(pair.Value);
        return normalized;
    }

    private static ContainerProviderHealthSnapshot ToProviderHealthSnapshot(ProviderHealthSnapshotDto dto)
    {
        var metrics = dto.Metrics is null || dto.Metrics.Count == 0
            ? null
            : dto.Metrics.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToString(),
                StringComparer.Ordinal);

        var history = dto.History is null || dto.History.Count == 0
            ? Array.Empty<ContainerProviderHealthHistoryEntry>()
            : dto.History
                .Select(entry => new ContainerProviderHealthHistoryEntry(
                    entry.Timestamp,
                    entry.State,
                    entry.Ready,
                    entry.Detail,
                    entry.FailureCategory))
                .ToArray();

        return new ContainerProviderHealthSnapshot(
            dto.Ready,
            dto.State,
            dto.Detail,
            dto.CheckedAt,
            dto.IsStale,
            dto.FailureCategory,
            metrics,
            history);
    }

    /// <summary>
    /// Normalizes a diarization default provider identifier for use with the runtime catalog.
    /// </summary>
    /// <param name="defaultProvider">The provider identifier to normalize; may be null or whitespace.</param>
    /// <returns>The normalized provider identifier, or the original <c>defaultProvider</c> value if it is null or whitespace.</returns>
    private static string? NormalizeDiarizationDefaultProvider(string? defaultProvider)
    {
        if (string.IsNullOrWhiteSpace(defaultProvider))
            return defaultProvider;

        return InferenceRuntimeCatalog.NormalizeDiarizationCapabilityProviderId(defaultProvider);
    }

    /// <summary>
    /// Normalize speaker identifiers for a collection of diarization segments.
    /// </summary>
    /// <param name="segments">The diarization segments whose speaker identifiers should be normalized; timings are preserved.</param>
    /// <returns>A list of DiarizedSegment objects with normalized speaker IDs and the original start/end times.</returns>
    private static IReadOnlyList<DiarizedSegment> NormalizeDiarizationSegments(
        IReadOnlyList<DiarizationSegmentDto> segments)
    {
        var normalized = new List<DiarizedSegment>(segments.Count);
        var assignedLabels = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var segment in segments)
        {
            normalized.Add(new DiarizedSegment(
                segment.Start,
                segment.End,
                NormalizeSpeakerId(segment.SpeakerId, assignedLabels)));
        }

        return normalized;
    }

    /// <summary>
    /// Normalize a raw speaker identifier into a consistent "spk_{NN}" label and record the mapping.
    /// </summary>
    /// <param name="rawSpeakerId">The raw speaker identifier which may be null, blank, or already normalized.</param>
    /// <param name="assignedLabels">A mapping of original keys to normalized speaker labels; this dictionary is updated with a new entry when a normalization is created.</param>
    /// <returns>The normalized speaker id in the form "spk_{NN}" where NN is a two-digit zero-based index.</returns>
    private static string NormalizeSpeakerId(string? rawSpeakerId, IDictionary<string, string> assignedLabels)
    {
        var key = string.IsNullOrWhiteSpace(rawSpeakerId)
            ? $"speaker_{assignedLabels.Count}"
            : rawSpeakerId.Trim();

        if (assignedLabels.TryGetValue(key, out var existing))
            return existing;

        var usedLabels = new HashSet<string>(assignedLabels.Values, StringComparer.Ordinal);
        var speakerIndex = assignedLabels.Count;
        string speakerId;
        do
        {
            speakerId = $"spk_{speakerIndex:D2}";
            speakerIndex++;
        }
        while (usedLabels.Contains(speakerId));

        assignedLabels[key] = speakerId;
        return speakerId;
    }

    /// <summary>
    /// Counts the unique, non-blank speaker identifiers present in the given diarization segments.
    /// </summary>
    /// <param name="segments">The diarization segments to inspect for speaker identifiers.</param>
    /// <returns>The count of unique, non-blank speaker IDs in <paramref name="segments"/>.</returns>
    private static int CountDistinctSpeakers(IReadOnlyList<DiarizedSegment> segments)
    {
        var speakers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in segments)
        {
            if (!string.IsNullOrWhiteSpace(segment.SpeakerId))
                speakers.Add(segment.SpeakerId);
        }

        return speakers.Count;
    }

    /// <summary>
    /// Acquires a per-request lease for the specified request kind when a lease tracker is configured.
    /// </summary>
    /// <param name="kind">The kind of containerized request to acquire a lease for.</param>
    /// <returns>An <see cref="IDisposable"/> representing the acquired lease that must be disposed to release it, or <c>null</c> if no lease tracker is configured.</returns>
    private IDisposable? AcquireLease(ContainerizedRequestKind kind) =>
        _requestLeaseTracker?.Acquire(kind);

    public void Dispose()
    {
        if (_disposeHttpClient)
            _httpClient.Dispose();
    }

    private static void WriteDebugLog(string runId, string hypothesisId, string location, string message, object data)
    {
        var payload = new
        {
            sessionId = "f76224",
            runId,
            hypothesisId,
            location,
            message,
            data,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        try
        {
            var line = JsonSerializer.Serialize(payload);
            File.AppendAllText(DebugLogPath, line + Environment.NewLine);
        }
        catch
        {
            // Swallow debug log failures.
        }
    }

    private static string ResolveDebugLogPath()
    {
        var envPath = Environment.GetEnvironmentVariable("BABEL_DEBUG_LOG_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
            return envPath;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Babel-Player.sln")))
                return Path.Combine(dir.FullName, "debug-f76224.log");
            dir = dir.Parent;
        }

        return Path.Combine(Environment.CurrentDirectory, "debug-f76224.log");
    }
}

public sealed record QwenBatchSegmentPayload(
    string SegmentId,
    string Text,
    string? Language,
    string ReferenceId);

public sealed record QwenBatchSegmentResult(
    string SegmentId,
    TtsResult Result);

public enum ContainerCapabilityStage
{
    Transcription,
    Translation,
    Tts,
    Diarization,
    VocalSeparation,
}

public sealed record ContainerCapabilitiesSnapshot(
    bool TranscriptionReady,
    string? TranscriptionDetail,
    bool TranslationReady,
    string? TranslationDetail,
    bool TtsReady,
    string? TtsDetail,
    IReadOnlyDictionary<string, bool>? TtsProviders = null,
    IReadOnlyDictionary<string, string>? TtsProviderDetails = null,
    bool DiarizationReady = false,
    string? DiarizationDetail = null,
    IReadOnlyDictionary<string, bool>? DiarizationProviders = null,
    IReadOnlyDictionary<string, string>? DiarizationProviderDetails = null,
    string? DiarizationDefaultProvider = null,
    IReadOnlyDictionary<string, ContainerProviderHealthSnapshot>? TtsProviderHealth = null,
    IReadOnlyDictionary<string, ContainerProviderHealthSnapshot>? DiarizationProviderHealth = null,
    bool VocalSeparationReady = false,
    string? VocalSeparationDetail = null)
{
    /// <summary>
    /// Indicates whether the given capability stage is ready.
    /// </summary>
    /// <param name="stage">The capability stage to query.</param>
    /// <returns>`true` if the specified stage is ready, `false` otherwise.</returns>
    public bool IsReady(ContainerCapabilityStage stage) => stage switch
    {
        ContainerCapabilityStage.Transcription => TranscriptionReady,
        ContainerCapabilityStage.Translation => TranslationReady,
        ContainerCapabilityStage.Tts => TtsReady,
        ContainerCapabilityStage.Diarization => DiarizationReady,
        ContainerCapabilityStage.VocalSeparation => VocalSeparationReady,
        _ => false,
    };

    /// <summary>
    /// Gets the detail message associated with the specified capability stage.
    /// </summary>
    /// <param name="stage">The capability stage to query.</param>
    /// <returns>The detail string for the stage, or `null` if no detail is available or the stage is unrecognized.</returns>
    public string? Detail(ContainerCapabilityStage stage) => stage switch
    {
        ContainerCapabilityStage.Transcription => TranscriptionDetail,
        ContainerCapabilityStage.Translation => TranslationDetail,
        ContainerCapabilityStage.Tts => TtsDetail,
        ContainerCapabilityStage.Diarization => DiarizationDetail,
        ContainerCapabilityStage.VocalSeparation => VocalSeparationDetail,
        _ => null,
    };

    /// <summary>
    /// Checks whether readiness status or detail information exists for a given TTS provider identifier.
    /// </summary>
    /// <param name="providerId">The TTS provider identifier to look up; returns false if null or whitespace.</param>
    /// <param name="ready">Set to the provider's readiness value when present, otherwise false.</param>
    /// <param name="detail">Set to the provider's detail string when present, otherwise null.</param>
    /// <returns>`true` if either a readiness entry or a detail string was found for the given providerId, `false` otherwise.</returns>
    public bool TryGetTtsProviderReadiness(string providerId, out bool ready, out string? detail)
    {
        ready = false;
        detail = null;
        if (string.IsNullOrWhiteSpace(providerId))
            return false;

        var found = false;
        if (TtsProviders is not null && TtsProviders.TryGetValue(providerId, out var providerReady))
        {
            ready = providerReady;
            found = true;
        }

        if (TtsProviderDetails is not null && TtsProviderDetails.TryGetValue(providerId, out var providerDetail))
            detail = providerDetail;

        return found || detail is not null;
    }

    public bool TryGetTtsProviderHealth(string providerId, out ContainerProviderHealthSnapshot? providerHealth)
    {
        providerHealth = null;
        return !string.IsNullOrWhiteSpace(providerId)
            && TtsProviderHealth is not null
            && TtsProviderHealth.TryGetValue(providerId, out providerHealth);
    }

    /// <summary>
    /// Attempts to retrieve the readiness and detail information for a diarization provider identified by <paramref name="providerId"/>.
    /// </summary>
    /// <param name="providerId">Normalized diarization provider identifier to look up.</param>
    /// <param name="ready">Set to the provider's readiness state if available; otherwise <c>false</c>.</param>
    /// <param name="detail">Set to the provider's detail string if available; otherwise <c>null</c>.</param>
    /// <returns><c>true</c> if either readiness or detail information was found for the given provider; <c>false</c> if <paramref name="providerId"/> is null/blank or no information was found.</returns>
    public bool TryGetDiarizationProviderReadiness(string providerId, out bool ready, out string? detail)
    {
        ready = false;
        detail = null;
        if (string.IsNullOrWhiteSpace(providerId))
            return false;

        var found = false;
        if (DiarizationProviders is not null && DiarizationProviders.TryGetValue(providerId, out var providerReady))
        {
            ready = providerReady;
            found = true;
        }

        if (DiarizationProviderDetails is not null && DiarizationProviderDetails.TryGetValue(providerId, out var providerDetail))
            detail = providerDetail;

        return found || detail is not null;
    }

    public bool TryGetDiarizationProviderHealth(string providerId, out ContainerProviderHealthSnapshot? providerHealth)
    {
        providerHealth = null;
        return !string.IsNullOrWhiteSpace(providerId)
            && DiarizationProviderHealth is not null
            && DiarizationProviderHealth.TryGetValue(providerId, out providerHealth);
    }
}

public sealed record ContainerProviderHealthHistoryEntry(
    string? Timestamp,
    string? State,
    bool Ready,
    string? Detail,
    string? FailureCategory);

public sealed record ContainerProviderHealthSnapshot(
    bool Ready,
    string? State,
    string? Detail,
    string? CheckedAt,
    bool IsStale,
    string? FailureCategory,
    IReadOnlyDictionary<string, string>? Metrics,
    IReadOnlyList<ContainerProviderHealthHistoryEntry> History);

public sealed record ContainerHealthStatus(
    bool IsAvailable,
    bool CudaAvailable,
    string? CudaVersion,
    string ServiceUrl,
    string? ErrorMessage,
    ContainerCapabilitiesSnapshot? Capabilities = null,
    string? CapabilitiesError = null,
    int ActiveRequests = 0,
    int ActiveQwenRequests = 0,
    int ActiveDiarizationRequests = 0,
    bool Busy = false,
    string? BusyReason = null,
    IReadOnlyDictionary<string, ContainerProviderHealthSnapshot>? ProviderHealth = null,
    int QwenMaxConcurrency = 0,
    int QwenQueueDepth = 0,
    double? QwenLastQueueWaitMs = null,
    double? QwenLastGenerationMs = null,
    double? QwenLastReferencePrepMs = null,
    double? QwenLastWarmupMs = null)
{
    /// <summary>
        /// Create a ContainerHealthStatus that marks the container at the given URL as unavailable.
        /// </summary>
        /// <param name="url">The service URL being reported as unavailable.</param>
        /// <param name="reason">Optional human-readable reason or error message explaining the unavailability.</param>
        /// <returns>A ContainerHealthStatus marked unavailable for the specified URL with default capability and activity values; the provided reason is included when present.</returns>
        public static ContainerHealthStatus Unavailable(string url, string? reason = null) =>
        new(false, false, null, url, reason, null, null, 0, 0, 0, false, null);

    public string StatusLine
    {
        get
        {
            if (!IsAvailable) return "Container unavailable";

            var cuda = CudaAvailable
                ? $"CUDA {CudaVersion ?? "✓"}"
                : "CPU-only";
            var activity = BuildActivitySummary();

            if (Capabilities is null)
            {
                return string.IsNullOrWhiteSpace(CapabilitiesError)
                    ? $"Healthy ({cuda}){activity}"
                    : $"Healthy ({cuda}){activity} · capabilities unavailable";
            }

            return $"Healthy ({cuda}){activity} · tx={(Capabilities.TranscriptionReady ? "✓" : "x")} · tl={(Capabilities.TranslationReady ? "✓" : "x")} · tts={(Capabilities.TtsReady ? "✓" : "x")} · diar={(Capabilities.DiarizationReady ? "✓" : "x")} · vox={(Capabilities.VocalSeparationReady ? "✓" : "x")}";
        }
    }

    private string BuildActivitySummary()
    {
        var parts = new List<string>();

        if (ActiveRequests > 0)
            parts.Add($"active={ActiveRequests}");
        if (ActiveQwenRequests > 0)
            parts.Add($"qwen={ActiveQwenRequests}");
        if (ActiveDiarizationRequests > 0)
            parts.Add($"diarization={ActiveDiarizationRequests}");
        if (QwenQueueDepth > 0)
            parts.Add($"qwen-queue={QwenQueueDepth}");
        if (QwenMaxConcurrency > 0)
            parts.Add($"qwen-max={QwenMaxConcurrency}");
        if (Busy)
            parts.Add(string.IsNullOrWhiteSpace(BusyReason) ? "busy" : $"busy={BusyReason}");

        return parts.Count == 0 ? string.Empty : $" · {string.Join(" · ", parts)}";
    }
}
