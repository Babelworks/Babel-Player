using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;
using Babel.Player.Services.Transcription;

namespace Babel.Player.Services;

public sealed class FasterWhisperTranscriptionProvider : PythonSubprocessServiceBase, ITranscriptionProvider, IStreamingTranscriptionProvider, IBenchmarkableProvider
{
    public string ProviderId => ProviderNames.FasterWhisper;
    private static readonly string DebugLogPath = ResolveDebugLogPath();

    public FasterWhisperTranscriptionProvider(AppLog log) : base(log) { }

    private async Task<string> ExtractAudioAsync(string videoPath, CancellationToken cancellationToken = default)
    {
        var audioPath = Path.Combine(Path.GetTempPath(), $"audio_{Guid.NewGuid():N}.wav");

        var ffmpegPath = DependencyLocator.FindFfmpeg()
            ?? throw new InvalidOperationException(
                "ffmpeg not found. Expected bundled ffmpeg.exe next to the app or ffmpeg on PATH.");
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(videoPath);
        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("-acodec");
        psi.ArgumentList.Add("pcm_s16le");
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add("16000");
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-af");
        psi.ArgumentList.Add("loudnorm=I=-16:LRA=11:TP=-1.5");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add(audioPath);

        using var proc = Process.Start(psi);
        if (proc == null)
            throw new InvalidOperationException("Failed to start ffmpeg for audio extraction.");

        var stderr = await proc.StandardError.ReadToEndAsync(cancellationToken);
        await proc.WaitForExitAsync(cancellationToken);

        if (proc.ExitCode != 0 || !File.Exists(audioPath))
            throw new InvalidOperationException($"Audio extraction failed: {stderr}");

        Log.Debug($"Extracted audio to: {audioPath}");
        return audioPath;
    }

    public async Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(request.SourceAudioPath))
            throw new FileNotFoundException($"Audio file not found: {request.SourceAudioPath}");

        var inputPath = request.SourceAudioPath;
        string? extractedAudioPath = null;
        var extension = Path.GetExtension(request.SourceAudioPath).ToLowerInvariant();

        try
        {
            if (extension == ".mp4" || extension == ".avi" || extension == ".mkv" || extension == ".mov")
            {
                extractedAudioPath = await ExtractAudioAsync(request.SourceAudioPath, cancellationToken);
                inputPath = extractedAudioPath;
            }
            else if (extension != ".wav" && extension != ".mp3" && extension != ".flac" && extension != ".ogg")
            {
                throw new InvalidOperationException($"Unsupported audio format: {extension}. Supported formats: wav, mp3, flac, ogg, mp4, avi, mkv, mov");
            }

            var cpuComputeType = CpuTranscriptionRuntimePolicy.ResolveEffectiveComputeType(
                string.IsNullOrWhiteSpace(request.CpuComputeType) ? "int8" : request.CpuComputeType,
                CpuTranscriptionRuntimePolicy.CreateMinimalProbeSnapshot(),
                Log);
            var cpuThreads = request.CpuThreads;
            var numWorkers = request.NumWorkers < 1 ? 1 : request.NumWorkers;

            var modelNameLiteral = request.ModelName.Replace("\\", "\\\\").Replace("'", "\\'");
            var cpuComputeTypeLiteral = cpuComputeType.Replace("\\", "\\\\").Replace("'", "\\'");
            var languageHintLiteral = string.IsNullOrWhiteSpace(request.LanguageHint)
                ? "None"
                : $"'{request.LanguageHint.Replace("\\", "\\\\").Replace("'", "\\'")}'";

            var whisperCtorArgs =
                $"model_name, device='cpu', compute_type='{cpuComputeTypeLiteral}', num_workers={numWorkers}";
            if (cpuThreads > 0)
                whisperCtorArgs += $", cpu_threads={cpuThreads}";

            // model has already been validated against the whitelist by ProviderCapability before this call
            var script = $@"
import sys, json
from time import perf_counter

# ── Memory sampling helpers (Step 3: VRAM/RAM instrumentation) ──────────────
def _sample_ram_mb():
    try:
        import psutil, os
        return psutil.Process(os.getpid()).memory_info().rss / (1024 * 1024)
    except Exception:
        return -1

def _sample_vram_mb():
    try:
        import pynvml
        pynvml.nvmlInit()
        try:
            handle = pynvml.nvmlDeviceGetHandleByIndex(0)
            info   = pynvml.nvmlDeviceGetMemoryInfo(handle)
            return info.used / (1024 * 1024)
        finally:
            pynvml.nvmlShutdown()
    except Exception:
        return -1

from faster_whisper import WhisperModel

model_name = '{modelNameLiteral}'
language_hint = {languageHintLiteral}
print('CPU transcription runtime: compute_type={cpuComputeTypeLiteral}, cpu_threads={(cpuThreads > 0 ? cpuThreads.ToString() : "auto")}, num_workers={numWorkers}')
t0 = perf_counter()
model = WhisperModel({whisperCtorArgs})
t1 = perf_counter()
print(json.dumps({{'timing':'model_load_s','value': round(t1 - t0, 3)}}), file=sys.stderr)

# Sample baseline memory after model load, before inference
ram_before = _sample_ram_mb()
vram_before = _sample_vram_mb()

t2 = perf_counter()
segments, info = model.transcribe(sys.argv[1], language=language_hint or None)
t3 = perf_counter()
print(json.dumps({{'timing':'transcribe_s','value': round(t3 - t2, 3)}}), file=sys.stderr)

# Sample peak memory immediately after inference completes
ram_after  = _sample_ram_mb()
vram_after = _sample_vram_mb()

peak_ram_mb  = max(ram_before, ram_after)
peak_vram_mb = max(vram_before, vram_after)

result = {{
    'language': info.language,
    'language_probability': info.language_probability,
    'peak_ram_mb': peak_ram_mb,
    'peak_vram_mb': peak_vram_mb,
    'segments': []
}}

for seg in segments:
    result['segments'].append({{
        'start': seg.start,
        'end': seg.end,
        'text': seg.text
    }})

with open(sys.argv[2], 'w', encoding='utf-8') as f:
    json.dump(result, f, ensure_ascii=False, indent=2)

print('Transcription complete')
";

            Log.Debug($"Starting transcription of: {inputPath} [cpu compute={cpuComputeType}, threads={(cpuThreads > 0 ? cpuThreads.ToString() : "auto")}, workers={numWorkers}]");

            var result = await RunPythonScriptAsync(
                script,
                [inputPath, request.OutputJsonPath],
                "transcribe",
                cancellationToken: cancellationToken);
            ThrowIfFailed(result, "Transcription");

            Log.Debug($"Transcription completed: {request.OutputJsonPath}");

            var transcriptionData = await ArtifactJson.LoadTranscriptAsync(request.OutputJsonPath, cancellationToken);

            var segments = new List<TranscriptSegment>();
            foreach (var seg in transcriptionData.Segments ?? [])
            {
                if (!string.IsNullOrWhiteSpace(seg.Text))
                    segments.Add(new TranscriptSegment(seg.Start, seg.End, seg.Text));
            }

            return new TranscriptionResult(
                true,
                segments,
                transcriptionData.Language ?? "unknown",
                transcriptionData.LanguageProbability,
                null,
                ElapsedMs:   result.ElapsedMs,
                PeakVramMb:  transcriptionData.PeakVramMb,
                PeakRamMb:   transcriptionData.PeakRamMb);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(extractedAudioPath) && File.Exists(extractedAudioPath))
            {
                File.Delete(extractedAudioPath);
                Log.Debug($"Deleted temporary extracted audio: {extractedAudioPath}");
            }
        }
    }

    async Task<TranscriptionResult> IStreamingTranscriptionProvider.TranscribeStreamingAsync(
        TranscriptionRequest request,
        ChannelWriter<TranscriptChannelItem> writer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (!File.Exists(request.SourceAudioPath))
            throw new FileNotFoundException($"Audio file not found: {request.SourceAudioPath}");

        var inputPath = request.SourceAudioPath;
        string? extractedAudioPath = null;
        var extension = Path.GetExtension(request.SourceAudioPath).ToLowerInvariant();
        Exception? completionError = null;

        try
        {
            if (extension == ".mp4" || extension == ".avi" || extension == ".mkv" || extension == ".mov")
            {
                extractedAudioPath = await ExtractAudioAsync(request.SourceAudioPath, cancellationToken).ConfigureAwait(false);
                inputPath = extractedAudioPath;
            }
            else if (extension != ".wav" && extension != ".mp3" && extension != ".flac" && extension != ".ogg")
            {
                throw new InvalidOperationException($"Unsupported audio format: {extension}. Supported formats: wav, mp3, flac, ogg, mp4, avi, mkv, mov");
            }

            var cpuComputeType = CpuTranscriptionRuntimePolicy.ResolveEffectiveComputeType(
                string.IsNullOrWhiteSpace(request.CpuComputeType) ? "int8" : request.CpuComputeType,
                CpuTranscriptionRuntimePolicy.CreateMinimalProbeSnapshot(),
                Log);
            var cpuThreads = request.CpuThreads;
            var numWorkers = request.NumWorkers < 1 ? 1 : request.NumWorkers;
            var modelNameLiteral = request.ModelName.Replace("\\", "\\\\").Replace("'", "\\'");
            var cpuComputeTypeLiteral = cpuComputeType.Replace("\\", "\\\\").Replace("'", "\\'");
            var languageHintLiteral = string.IsNullOrWhiteSpace(request.LanguageHint)
                ? "None"
                : $"'{request.LanguageHint.Replace("\\", "\\\\").Replace("'", "\\'")}'";

            var whisperCtorArgs =
                $"model_name, device='cpu', compute_type='{cpuComputeTypeLiteral}', num_workers={numWorkers}";
            if (cpuThreads > 0)
                whisperCtorArgs += $", cpu_threads={cpuThreads}";

            var script = $@"
import json
import os
import sys

def _sample_ram_mb():
    try:
        import psutil
        return psutil.Process(os.getpid()).memory_info().rss / (1024 * 1024)
    except Exception:
        return -1

def _sample_vram_mb():
    try:
        import pynvml
        pynvml.nvmlInit()
        try:
            handle = pynvml.nvmlDeviceGetHandleByIndex(0)
            info = pynvml.nvmlDeviceGetMemoryInfo(handle)
            return info.used / (1024 * 1024)
        finally:
            pynvml.nvmlShutdown()
    except Exception:
        return -1

def _emit(payload):
    print(json.dumps(payload, ensure_ascii=False), flush=True)

from faster_whisper import WhisperModel

model_name = '{modelNameLiteral}'
language_hint = {languageHintLiteral}
model = WhisperModel({whisperCtorArgs})
ram_before = _sample_ram_mb()
vram_before = _sample_vram_mb()
segments, info = model.transcribe(sys.argv[1], language=language_hint or None, word_timestamps=True)
_emit({{
    'type': 'metadata',
    'language': info.language or 'unknown',
    'language_probability': info.language_probability or 0.0,
}})

segment_count = 0
for seg in segments:
    text = (seg.text or '').strip()
    if not text:
        continue
    _emit({{
        'type': 'segment',
        'start': seg.start,
        'end': seg.end,
        'text': text,
        'words': [
            {{
                'text': word.word,
                'start': word.start,
                'end': word.end,
            }}
            for word in (seg.words or [])
        ],
    }})
    segment_count += 1

ram_after = _sample_ram_mb()
vram_after = _sample_vram_mb()
_emit({{
    'type': 'complete',
    'segment_count': segment_count,
    'peak_ram_mb': max(ram_before, ram_after),
    'peak_vram_mb': max(vram_before, vram_after),
}})
";

            var sourceLanguage = request.LanguageHint ?? "unknown";
            var languageProbability = 0d;
            var peakRamMb = -1d;
            var peakVramMb = -1d;
            var segments = new List<TranscriptSegment>();

            Log.Debug(
                $"Starting streaming transcription of: {inputPath} " +
                $"[cpu compute={cpuComputeType}, threads={(cpuThreads > 0 ? cpuThreads.ToString() : "auto")}, workers={numWorkers}]");

            var scriptResult = await RunPythonStreamingScriptAsync(
                script,
                async (line, ct) =>
                {
                    if (string.IsNullOrWhiteSpace(line))
                        return;

                    using var json = JsonDocument.Parse(line);
                    var root = json.RootElement;
                    if (!root.TryGetProperty("type", out var typeProperty))
                        return;

                    var eventType = typeProperty.GetString();
                    if (string.Equals(eventType, "metadata", StringComparison.Ordinal))
                    {
                        sourceLanguage = root.TryGetProperty("language", out var languageProperty)
                            ? languageProperty.GetString() ?? sourceLanguage
                            : sourceLanguage;
                        languageProbability = root.TryGetProperty("language_probability", out var probabilityProperty)
                            ? probabilityProperty.GetDouble()
                            : languageProbability;
                        return;
                    }

                    if (string.Equals(eventType, "complete", StringComparison.Ordinal))
                    {
                        if (root.TryGetProperty("peak_ram_mb", out var peakRamProperty))
                            peakRamMb = peakRamProperty.GetDouble();
                        if (root.TryGetProperty("peak_vram_mb", out var peakVramProperty))
                            peakVramMb = peakVramProperty.GetDouble();
                        return;
                    }

                    if (!string.Equals(eventType, "segment", StringComparison.Ordinal))
                        return;

                    var text = root.GetProperty("text").GetString();
                    if (string.IsNullOrWhiteSpace(text))
                        return;

                    var segment = new TranscriptSegmentArtifact
                    {
                        Start = root.GetProperty("start").GetDouble(),
                        End = root.GetProperty("end").GetDouble(),
                        Text = text,
                        Words = TryReadWords(root),
                    };
                    var segmentId = SessionWorkflowCoordinator.SegmentId(segment.Start);
                    segments.Add(new TranscriptSegment(segment.Start, segment.End, text));
                    await writer.WriteAsync(
                        new TranscriptChannelItem(segmentId, segment, sourceLanguage, languageProbability),
                        ct).ConfigureAwait(false);
                },
                [inputPath],
                "transcribe_stream",
                cancellationToken: cancellationToken).ConfigureAwait(false);

            ThrowIfFailed(scriptResult, "Streaming transcription");

            return new TranscriptionResult(
                true,
                segments,
                sourceLanguage,
                languageProbability,
                null,
                ElapsedMs: scriptResult.ElapsedMs,
                PeakVramMb: peakVramMb,
                PeakRamMb: peakRamMb);
        }
        catch (Exception ex)
        {
            completionError = ex;
            throw;
        }
        finally
        {
            writer.TryComplete(completionError);
            if (!string.IsNullOrWhiteSpace(extractedAudioPath) && File.Exists(extractedAudioPath))
            {
                File.Delete(extractedAudioPath);
                Log.Debug($"Deleted temporary extracted audio: {extractedAudioPath}");
            }
        }
    }

    public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null)
    {
        var model = settings.TranscriptionModel;
        if (!ModelDownloader.IsFasterWhisperDownloaded(model))
            return new ProviderReadiness(false,
                $"Model '{model}' not downloaded yet.",
                RequiresModelDownload: true,
                ModelDownloadDescription: $"Download faster-whisper {model} model");
        return ProviderReadiness.Ready;
    }

    public async Task<bool> EnsureReadyAsync(AppSettings settings, IProgress<double>? progress, CancellationToken ct = default)
    {
        var model = settings.TranscriptionModel;
        if (!ModelDownloader.IsFasterWhisperDownloaded(model))
        {
            Log.Debug($"Model {model} requires download. Starting download...");
            return await new ModelDownloader(Log).DownloadFasterWhisperAsync(model, progress, ct);
        }
        return true;
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

    private static List<WordTimestamp>? TryReadWords(JsonElement root)
    {
        if (!root.TryGetProperty("words", out var wordsProperty) || wordsProperty.ValueKind != JsonValueKind.Array)
            return null;

        var words = new List<WordTimestamp>();
        foreach (var word in wordsProperty.EnumerateArray())
        {
            if (!word.TryGetProperty("text", out var textProperty) ||
                !word.TryGetProperty("start", out var startProperty) ||
                !word.TryGetProperty("end", out var endProperty))
            {
                continue;
            }

            var text = textProperty.GetString();
            if (string.IsNullOrWhiteSpace(text))
                continue;

            words.Add(new WordTimestamp(text, startProperty.GetDouble(), endProperty.GetDouble()));
        }

        return words.Count == 0 ? null : words;
    }
}
