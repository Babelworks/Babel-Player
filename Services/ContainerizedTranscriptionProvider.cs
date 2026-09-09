using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

/// <summary>
/// Transcription provider backed by the containerized inference service (<c>/transcribe</c>).
/// Bridges the file-path-based <see cref="ITranscriptionProvider"/> contract to the HTTP
/// client. Writes a transcript JSON artifact to <see cref="TranscriptionRequest.OutputJsonPath"/>
/// in the same format produced by <see cref="FasterWhisperTranscriptionProvider"/>.
/// </summary>
public sealed class ContainerizedTranscriptionProvider : ITranscriptionProvider, IStreamingTranscriptionProvider
{
    private readonly ContainerizedInferenceClient _client;
    private readonly AppLog _log;

    public ContainerizedTranscriptionProvider(ContainerizedInferenceClient client, AppLog log)
    {
        _client = client;
        _log = log;
    }

    public async Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(request.SourceAudioPath))
            throw new FileNotFoundException($"Audio file not found: {request.SourceAudioPath}");

        var cpuThreads = request.CpuThreads > 0 ? request.CpuThreads.ToString() : "auto";
        var cpuWorkers = Math.Max(1, request.NumWorkers);
        var cpuCompute = string.IsNullOrWhiteSpace(request.CpuComputeType) ? "int8" : request.CpuComputeType;
        _log.Debug($"[ContainerizedTranscription] Transcribing: {request.SourceAudioPath} " +
                  $"(model={request.ModelName}, cpu_compute={cpuCompute}, cpu_threads={cpuThreads}, cpu_workers={cpuWorkers})");

        var result = await _client.TranscribeAsync(
            request.SourceAudioPath,
            request.ModelName,
            request.LanguageHint,
            request.CpuComputeType,
            request.CpuThreads,
            request.NumWorkers,
            cancellationToken);

        if (!result.Success)
            throw new InvalidOperationException(
                $"Containerized transcription failed: {result.ErrorMessage}");

        // Write transcript JSON artifact in the canonical format expected by downstream stages.
        // Format: { language, language_probability, segments: [{start, end, text}] }
        var artifactDir = Path.GetDirectoryName(request.OutputJsonPath);
        if (!string.IsNullOrEmpty(artifactDir))
            Directory.CreateDirectory(artifactDir);

        var transcript = new TranscriptArtifact
        {
            SchemaVersion = ArtifactJson.CurrentSchemaVersion,
            Language = result.Language,
            LanguageProbability = result.LanguageProbability,
            Segments =
            [
                .. System.Linq.Enumerable.Select(result.Segments, s => new TranscriptSegmentArtifact
                {
                    Start = s.StartSeconds,
                    End = s.EndSeconds,
                    Text = s.Text,
                })
            ],
        };

        await ArtifactPersistence.AtomicWriteTextAsync(
                request.OutputJsonPath,
                ArtifactJson.SerializeTranscript(transcript),
                cancellationToken)
            .ConfigureAwait(false);

        _log.Debug($"[ContainerizedTranscription] Complete: {result.Segments.Count} segments, lang={result.Language}");

        return result;
    }

    async Task<TranscriptionResult> IStreamingTranscriptionProvider.TranscribeStreamingAsync(
        TranscriptionRequest request,
        ChannelWriter<TranscriptChannelItem> writer,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(request.SourceAudioPath))
            throw new FileNotFoundException($"Audio file not found: {request.SourceAudioPath}");

        var cpuThreads = request.CpuThreads > 0 ? request.CpuThreads.ToString() : "auto";
        var cpuWorkers = Math.Max(1, request.NumWorkers);
        var cpuCompute = string.IsNullOrWhiteSpace(request.CpuComputeType) ? "int8" : request.CpuComputeType;
        _log.Debug($"[ContainerizedTranscription] Streaming transcription: {request.SourceAudioPath} " +
                  $"(model={request.ModelName}, cpu_compute={cpuCompute}, cpu_threads={cpuThreads}, cpu_workers={cpuWorkers})");

        var result = await _client.TranscribeStreamingAsync(
            request.SourceAudioPath,
            writer,
            request.ModelName,
            request.LanguageHint,
            request.CpuComputeType,
            request.CpuThreads,
            request.NumWorkers,
            cancellationToken).ConfigureAwait(false);

        if (!result.Success)
            throw new InvalidOperationException(
                $"Containerized transcription failed: {result.ErrorMessage}");

        return result;
    }

    public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null) =>
        ContainerizedProviderReadiness.CheckTranscription(settings);
}
