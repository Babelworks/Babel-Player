using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services;

/// <summary>
/// A real implementation of IAudioProcessingService that uses ffmpeg.
/// </summary>
public sealed class FfmpegAudioProcessingService(AppLog log) : IAudioProcessingService
{
    private readonly AppLog _log = log;
    private static readonly string DebugLogPath = ResolveDebugLogPath();

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

    public async Task CombineAudioSegmentsAsync(
        IReadOnlyList<string> segmentAudioPaths,
        string outputAudioPath,
        CancellationToken cancellationToken)
    {
        if (segmentAudioPaths.Count == 0)
            throw new InvalidOperationException("Cannot combine zero segment audio files.");

        var outputDir = Path.GetDirectoryName(outputAudioPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        if (segmentAudioPaths.Count == 1)
        {
            File.Copy(segmentAudioPaths[0], outputAudioPath, overwrite: true);
            return;
        }

        var ffmpegPath = DependencyLocator.FindFfmpeg()
            ?? throw new InvalidOperationException("ffmpeg not found. Combined output requires ffmpeg.");

        var concatListDir = Path.Combine(Path.GetTempPath(), $"babel-concat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(concatListDir);
        var concatListPath = Path.Combine(concatListDir, "inputs.txt");
        var concatFile = string.Join(
            Environment.NewLine,
            segmentAudioPaths.Select(path => $"file '{EscapeConcatListPath(path)}'"));

        await File.WriteAllTextAsync(concatListPath, concatFile, cancellationToken).ConfigureAwait(false);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("concat");
            psi.ArgumentList.Add("-safe");
            psi.ArgumentList.Add("0");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(concatListPath);
            psi.ArgumentList.Add("-vn");
            psi.ArgumentList.Add("-c:a");
            psi.ArgumentList.Add("libmp3lame");
            psi.ArgumentList.Add("-q:a");
            psi.ArgumentList.Add("3");
            psi.ArgumentList.Add(outputAudioPath);

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start ffmpeg for segment concatenation.");

            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best-effort termination
                }
            });

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0 || !File.Exists(outputAudioPath))
            {
                throw new InvalidOperationException(
                    $"ffmpeg concatenation failed or produced no output file (exit code {process.ExitCode}): {stderr} {stdout}".Trim());
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(concatListDir))
                    Directory.Delete(concatListDir, recursive: true);
            }
            catch
            {
                // ignored
            }
        }
    }

    public async Task ComposeTimelineDubAsync(
        IReadOnlyList<TimelineDubSegment> segments,
        string outputAudioPath,
        CancellationToken cancellationToken)
    {
        if (segments.Count == 0)
            throw new InvalidOperationException("Cannot compose timeline dub without segments.");

        foreach (var segment in segments)
        {
            if (!File.Exists(segment.AudioPath))
                throw new FileNotFoundException($"Timeline segment audio not found: {segment.AudioPath}");
        }

        var ffmpegPath = DependencyLocator.FindFfmpeg()
            ?? throw new InvalidOperationException("ffmpeg not found. Timeline dub composition requires ffmpeg.");

        var outputDir = Path.GetDirectoryName(outputAudioPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-y");
        for (var index = 0; index < segments.Count; index++)
        {
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(segments[index].AudioPath);
        }

        var filterParts = new List<string>(segments.Count + 1);
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            var delayMs = Math.Max(0, (int)Math.Round(segment.StartSeconds * 1000, MidpointRounding.AwayFromZero));
            var escapedDuration = Math.Max(0, segment.SegmentDurationSeconds)
                .ToString("0.###", CultureInfo.InvariantCulture);
            var label = $"s{index}";
            filterParts.Add(segment.TrimToSegmentWindow
                ? $"[{index}:a]atrim=0:{escapedDuration},adelay={delayMs}|{delayMs}[{label}]"
                : $"[{index}:a]adelay={delayMs}|{delayMs}[{label}]");
        }

        var mixedInputs = string.Concat(Enumerable.Range(0, segments.Count).Select(index => $"[s{index}]"));
        var spanEndSeconds = segments.Max(static segment => segment.StartSeconds + segment.SegmentDurationSeconds);
        var spanEnd = spanEndSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        filterParts.Add($"{mixedInputs}amix=inputs={segments.Count}:normalize=0:dropout_transition=0[mixed];[mixed]apad=whole_dur={spanEnd}[outa]");

        psi.ArgumentList.Add("-filter_complex");
        psi.ArgumentList.Add(string.Join(";", filterParts));
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add("[outa]");
        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("-c:a");
        psi.ArgumentList.Add("libmp3lame");
        psi.ArgumentList.Add("-q:a");
        psi.ArgumentList.Add("3");
        psi.ArgumentList.Add(outputAudioPath);

        await RunFfmpegProcessAsync(psi, outputAudioPath, "timeline dub composition", cancellationToken).ConfigureAwait(false);
    }

    public async Task MixDubOverAmbianceAsync(
        string dubbedAudioPath,
        string ambianceAudioPath,
        string outputAudioPath,
        double ambianceGainDb,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(dubbedAudioPath))
            throw new FileNotFoundException($"Dubbed track not found: {dubbedAudioPath}");
        if (!File.Exists(ambianceAudioPath))
            throw new FileNotFoundException($"Ambiance track not found: {ambianceAudioPath}");

        var ffmpegPath = DependencyLocator.FindFfmpeg()
            ?? throw new InvalidOperationException("ffmpeg not found. Ambiance mixing requires ffmpeg.");

        var outputDir = Path.GetDirectoryName(outputAudioPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(dubbedAudioPath);
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(ambianceAudioPath);
        psi.ArgumentList.Add("-filter_complex");
        psi.ArgumentList.Add(
            $"[1:a]volume={ambianceGainDb.ToString("0.###", CultureInfo.InvariantCulture)}dB[bg];" +
            "[0:a][bg]amix=inputs=2:normalize=0:dropout_transition=0[outa]");
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add("[outa]");
        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("-c:a");
        psi.ArgumentList.Add("libmp3lame");
        psi.ArgumentList.Add("-q:a");
        psi.ArgumentList.Add("3");
        psi.ArgumentList.Add(outputAudioPath);

        await RunFfmpegProcessAsync(psi, outputAudioPath, "dub over ambiance mix", cancellationToken).ConfigureAwait(false);
    }

    public async Task ExtractAudioClipAsync(
        string inputPath,
        string outputPath,
        double startTimeSeconds,
        double durationSeconds,
        CancellationToken cancellationToken)
    {
        var ffmpegPath = DependencyLocator.FindFfmpeg()
            ?? throw new InvalidOperationException("ffmpeg not found. Audio extraction requires ffmpeg.");

        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = false,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-y");
        
        // Use -ss before -i for faster seeking
        psi.ArgumentList.Add("-ss");
        psi.ArgumentList.Add(startTimeSeconds.ToString("F3", CultureInfo.InvariantCulture));
        
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(inputPath);
        
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(durationSeconds.ToString("F3", CultureInfo.InvariantCulture));
        
        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add("16000"); // Standard for speech inference
        psi.ArgumentList.Add("-sample_fmt");
        psi.ArgumentList.Add("s16");
        psi.ArgumentList.Add(outputPath);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ffmpeg for audio extraction.");

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!proc.HasExited)
                    proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort termination
            }
        });

        var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);
        await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (proc.ExitCode != 0 || !File.Exists(outputPath))
        {
            throw new InvalidOperationException($"ffmpeg audio extraction failed with exit code {proc.ExitCode}: {stderr}");
        }
    }

    /// <summary>
    /// Extracts the entire audio track from <paramref name="inputPath"/> and writes it to <paramref name="outputPath"/> as mono 16 kHz PCM signed 16-bit (pcm_s16le).
    /// </summary>
    /// <param name="inputPath">Path to the source media file.</param>
    /// <param name="outputPath">Path where the extracted audio file will be created; the containing directory will be created if it does not exist.</param>
    /// <param name="cancellationToken">Token that, when cancelled, will attempt to terminate the ffmpeg process and will propagate cancellation to the caller.</param>
    /// <exception cref="InvalidOperationException">Thrown if ffmpeg is not found, if the ffmpeg process fails to start, or if ffmpeg exits with a non-zero code or the expected output file is not produced.</exception>
    public async Task ExtractFullAudioAsync(
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken)
    {

        var ffmpegPath = DependencyLocator.FindFfmpeg()
            ?? throw new InvalidOperationException("ffmpeg not found. Audio extraction requires ffmpeg.");

        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = false,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(inputPath);
        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("-acodec");
        psi.ArgumentList.Add("pcm_s16le");
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add("16000");
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add(outputPath);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ffmpeg for full audio extraction.");

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!proc.HasExited)
                    proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort termination
            }
        });

        var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);
        await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (proc.ExitCode != 0 || !File.Exists(outputPath))
        {
            throw new InvalidOperationException($"ffmpeg full audio extraction failed with exit code {proc.ExitCode}: {stderr}");
        }

        var outputFileInfo = new FileInfo(outputPath);
    }

    /// <summary>
    /// Probes a media file with ffprobe and obtains its duration in seconds.
    /// </summary>
    /// <param name="filePath">Path to the media file to probe.</param>
    /// <param name="cancellationToken">Token to cancel the probe; cancellation attempts to terminate the ffprobe process and will throw <see cref="OperationCanceledException"/> when observed.</param>
    /// <returns>The duration in seconds as a <see cref="double"/> if ffprobe succeeds and the output parses, or <c>null</c> if ffprobe is unavailable, parsing fails, or an error occurs.</returns>
    public async Task<double?> ProbeDurationAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var ffprobePath = DependencyLocator.FindFfprobe();
        if (ffprobePath is null)
        {
            _log.Warning("ffprobe not found; cannot probe audio duration.");
            return null;
        }

        var psi = new ProcessStartInfo
        {
            FileName = ffprobePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-show_entries");
        psi.ArgumentList.Add("format=duration");
        psi.ArgumentList.Add("-of");
        psi.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
        psi.ArgumentList.Add(filePath);

        try
        {
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start ffprobe.");

            using var reg = cancellationToken.Register(() =>
            {
                try
                {
                    if (!proc.HasExited)
                        proc.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    _log.Warning($"Failed to terminate ffprobe process on cancellation for '{filePath}': {ex.Message}");
                }
            });

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);
            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false);

            if (proc.ExitCode == 0 && double.TryParse(
                    stdout.Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var duration))
                return duration;

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warning($"ffprobe duration probe failed for '{filePath}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Adjusts the audio playback speed of an input file to target a specific duration and writes the result to an output file.
    /// </summary>
    /// <param name="inputPath">Path to the source audio file to be time-stretched.</param>
    /// <param name="outputPath">Path where the time-stretched output will be written. The method creates the output directory if needed.</param>
    /// <param name="targetDurationSeconds">Desired duration, in seconds, for the output file.</param>
    /// <param name="minRatio">Minimum allowed tempo ratio (sourceDuration / targetDuration). If the computed tempo is below this value the operation is skipped and the method returns <c>false</c>.</param>
    /// <param name="maxRatio">Maximum allowed tempo ratio (sourceDuration / targetDuration). If the computed tempo is above this value the operation is skipped and the method returns <c>false</c>.</param>
    /// <param name="cancellationToken">Token to cancel the operation; cancellation attempts to terminate the spawned ffmpeg process.</param>
    /// <returns>`true` if the time-stretch operation completed and produced the output file; `false` if the operation was skipped because the source duration was invalid or the tempo ratio fell outside [<paramref name="minRatio"/>, <paramref name="maxRatio"/>].</returns>
    /// <exception cref="InvalidOperationException">Thrown if ffmpeg is not found, if the ffmpeg process fails to start, or if ffmpeg exits non-zero or does not produce the expected output file.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the provided <paramref name="cancellationToken"/> is canceled during processing.</exception>
    public async Task<bool> TimeStretchAsync(
        string inputPath,
        string outputPath,
        double targetDurationSeconds,
        double minRatio = DubTimingDefaults.StretchMinTempoRatio,
        double maxRatio = DubTimingDefaults.StretchMaxTempoRatio,
        CancellationToken cancellationToken = default)
    {
        var sourceDuration = await ProbeDurationAsync(inputPath, cancellationToken).ConfigureAwait(false);
        if (sourceDuration is null or <= 0 || targetDurationSeconds <= 0)
        {
            _log.Warning(
                $"TimeStretch skipped: ffprobe could not determine a valid source duration for '{inputPath}' " +
                $"or the target duration '{targetDurationSeconds:F3}' was invalid.");
            return false;
        }

        // tempo = source / target  (>1 = speed up, <1 = slow down)
        double tempo = sourceDuration.Value / targetDurationSeconds;

        if (tempo < minRatio || tempo > maxRatio)
        {
            _log.Debug(
                $"TimeStretch skipped: tempo ratio {tempo:F3} outside [{minRatio:F2}, {maxRatio:F2}] " +
                $"for '{Path.GetFileName(inputPath)}'.");
            return false;
        }

        var ffmpegPath = DependencyLocator.FindFfmpeg()
            ?? throw new InvalidOperationException("ffmpeg not found. TimeStretch requires ffmpeg.");

        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        // atempo accepts [0.5, 2.0] per stage; build a chain for out-of-range tempos.
        string atempoFilter = BuildAtempoFilter(tempo);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(inputPath);
        psi.ArgumentList.Add("-filter:a");
        psi.ArgumentList.Add(atempoFilter);
        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("-c:a");
        psi.ArgumentList.Add("libmp3lame");
        psi.ArgumentList.Add("-q:a");
        psi.ArgumentList.Add("3");
        psi.ArgumentList.Add(outputPath);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ffmpeg for time-stretch.");

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                _log.Warning($"Failed to terminate ffmpeg time-stretch process on cancellation for '{inputPath}': {ex.Message}");
            }
        });

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0 || !File.Exists(outputPath))
        {
            _log.Warning(
                $"TimeStretch failed for '{Path.GetFileName(inputPath)}': exit={process.ExitCode} stderr={stderr}");
            throw new InvalidOperationException(
                $"ffmpeg time-stretch failed (exit {process.ExitCode}): {stderr} {stdout}".Trim());
        }

        _log.Debug($"TimeStretch: '{Path.GetFileName(inputPath)}' {sourceDuration.Value:F2}s -> {targetDurationSeconds:F2}s (tempo {tempo:F3})");
        return true;
    }

    /// <summary>
    /// Builds an atempo filter chain. Each <c>atempo</c> is bounded to [0.5, 2.0] per ffmpeg docs;
    /// larger ratio changes are achieved by chaining (e.g. <c>atempo=2.0,atempo=1.25</c>).
    /// <summary>
    /// Builds an ffmpeg `atempo` filter chain that achieves the requested tempo while ensuring each stage is within ffmpeg's per-stage bounds (0.5 to 2.0).
    /// </summary>
    /// <param name="tempo">Target tempo ratio (source duration divided by target duration); must be a finite value greater than 0.</param>
    /// <returns>A comma-separated ffmpeg `atempo=` filter string representing one or more stages (e.g., "atempo=1.500000,atempo=0.750000").</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="tempo"/> is not finite or is less than or equal to zero.</exception>
    private static string BuildAtempoFilter(double tempo)
    {
        if (double.IsNaN(tempo) || double.IsInfinity(tempo) || tempo <= 0)
            throw new ArgumentOutOfRangeException(nameof(tempo), "Tempo must be a finite positive value.");

        const double minAtempo = 0.5;
        const double maxAtempo = 2.0;
        const double epsilon = 1e-12;

        var factors = new List<double>();
        var remaining = tempo;
        var maxFactorCount = 0;
        var minFactorCount = 0;

        while (remaining > maxAtempo + epsilon)
        {
            remaining /= maxAtempo;
            maxFactorCount++;
        }

        while (remaining < minAtempo - epsilon)
        {
            remaining /= minAtempo;
            minFactorCount++;
        }

        // Avoid an unnecessary atempo=1.000000 stage for exact power-of-two decompositions.
        if (Math.Abs(remaining - 1.0) > epsilon || (maxFactorCount == 0 && minFactorCount == 0))
            factors.Add(remaining);

        for (var i = 0; i < maxFactorCount; i++)
            factors.Add(maxAtempo);

        for (var i = 0; i < minFactorCount; i++)
            factors.Add(minAtempo);

        return string.Join(
            ",",
            factors.Select(value => $"atempo={value.ToString("F6", CultureInfo.InvariantCulture)}"));
    }

    /// <summary>
            /// Escapes a filesystem path for inclusion in an ffmpeg concat demuxer list file.
            /// </summary>
            /// <param name="path">The input file path to escape.</param>
            /// <returns>The escaped path with backslashes converted to forward slashes and single quotes escaped for use inside single-quoted concat list entries.</returns>
            private static string EscapeConcatListPath(string path) =>
        path.Replace("\\", "/", StringComparison.Ordinal)
            .Replace("'", "'\\''", StringComparison.Ordinal);

    private static async Task RunFfmpegProcessAsync(
        ProcessStartInfo startInfo,
        string expectedOutputPath,
        string operationName,
        CancellationToken cancellationToken)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start ffmpeg for {operationName}.");

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort termination.
            }
        });

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0 || !File.Exists(expectedOutputPath))
        {
            throw new InvalidOperationException(
                $"ffmpeg {operationName} failed or produced no output file (exit code {process.ExitCode}): {stderr} {stdout}".Trim());
        }
    }
}
