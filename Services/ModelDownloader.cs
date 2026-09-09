using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Chatterbox;
using Babel.Player.Services.SortFormer;

namespace Babel.Player.Services;

public sealed partial class ModelDownloader
{
    private readonly AppLog _log;
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(30) };

    public ModelDownloader(AppLog log)
    {
        _log = log;
    }

    public async Task<bool> DownloadFasterWhisperAsync(string model, IProgress<double>? progress = null, CancellationToken token = default)
    {
        string repoId = $"Systran/faster-whisper-{model}";
        return await DownloadHuggingFaceModelAsync(repoId, progress, token);
    }

    public async Task<bool> DownloadNllbAsync(string model, IProgress<double>? progress = null, CancellationToken token = default)
    {
        string repoId = $"facebook/{model}";
        return await DownloadHuggingFaceModelAsync(repoId, progress, token);
    }

    public async Task<bool> DownloadCTranslate2TranslationModelAsync(string model, IProgress<double>? progress = null, CancellationToken token = default)
    {
        string? pythonPath = DependencyLocator.FindPython();
        if (pythonPath == null)
            throw new InvalidOperationException("Python required for preparing CTranslate2 translation models.");

        string repoId = $"facebook/{model}";
        string outputDir = GetCTranslate2TranslationModelDir(model);
        Directory.CreateDirectory(Path.GetDirectoryName(outputDir)!);

        string script = BuildCTranslate2TranslationModelPrepScript();

        return await RunPythonModelPrepScriptAsync(
            pythonPath,
            script,
            [repoId, outputDir, "int8"],
            progress,
            token,
            $"CTranslate2 translation model {repoId}");
    }

    internal static string BuildCTranslate2TranslationModelPrepScript() => """
import json
import os
import shutil
import subprocess
import sys

def ensure_dependencies():
    try:
        from huggingface_hub import snapshot_download
        import transformers
        import sentencepiece
        from ctranslate2.converters import TransformersConverter
        return snapshot_download, TransformersConverter
    except ImportError:
        # ctranslate2 swallows some optional import failures, so verify converter dependencies explicitly.
        print('[progress] 5', flush=True)
        subprocess.check_call(
            [
                sys.executable,
                '-m',
                'pip',
                'install',
                'huggingface_hub',
                'ctranslate2',
                'transformers',
                'sentencepiece',
            ]
        )
        from huggingface_hub import snapshot_download
        import transformers
        import sentencepiece
        from ctranslate2.converters import TransformersConverter
        return snapshot_download, TransformersConverter

snapshot_download, TransformersConverter = ensure_dependencies()

repo_id = sys.argv[1]
output_dir = sys.argv[2]
quantization = sys.argv[3]
tmp_output_dir = output_dir + '.tmp'

print('[progress] 15', flush=True)
snapshot_dir = snapshot_download(repo_id=repo_id)

if os.path.isdir(tmp_output_dir):
    shutil.rmtree(tmp_output_dir)

print('[progress] 55', flush=True)
converter = TransformersConverter(model_name_or_path=snapshot_dir)
converter.convert(tmp_output_dir, quantization=quantization)

metadata_path = os.path.join(tmp_output_dir, 'babel_ct2_metadata.json')
with open(metadata_path, 'w', encoding='utf-8') as metadata_file:
    json.dump({'repo_id': repo_id, 'source_snapshot': snapshot_dir, 'quantization': quantization}, metadata_file, indent=2)

if os.path.isdir(output_dir):
    shutil.rmtree(output_dir)
shutil.move(tmp_output_dir, output_dir)
print('[progress] 100', flush=True)
""";


    private async Task<bool> DownloadHuggingFaceModelAsync(string repoId, IProgress<double>? progress = null, CancellationToken token = default)
    {
        string? pythonPath = DependencyLocator.FindPython();
        if (pythonPath == null)
            throw new InvalidOperationException("Python required for downloading HuggingFace models.");

        string script = @"
import sys
import os

try:
    from huggingface_hub import snapshot_download
except ImportError:
    print('Installing huggingface_hub...')
    os.system(f'""{sys.executable}"" -m pip install huggingface_hub')
    from huggingface_hub import snapshot_download

repo_id = sys.argv[1]
print(f'Downloading {repo_id}...', flush=True)
try:
    snapshot_download(repo_id=repo_id)
    print('Download complete.', flush=True)
except Exception as e:
    print(f'Error downloading: {e}', file=sys.stderr)
    sys.exit(1)
";
        string scriptPath = Path.Combine(Path.GetTempPath(), $"hf_dl_{Guid.NewGuid():N}.py");
        await File.WriteAllTextAsync(scriptPath, script);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = pythonPath,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add(scriptPath);
            psi.ArgumentList.Add(repoId);

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            _log.Info($"Started model download for {repoId}. This may take a few minutes...");

            var stdoutReaderTask = Task.Run(async () =>
            {
                while (true)
                {
                    string? line = await proc.StandardOutput.ReadLineAsync(token);
                    if (line == null) break;
                    _log.Debug($"[hf-download] {line}");
                }
            }, token);

            var errorReaderTask = Task.Run(async () =>
            {
                while (true)
                {
                    string? line = await proc.StandardError.ReadLineAsync(token);
                    if (line == null) break;
                    
                    if (progress != null)
                    {
                        var match = ProgressRegex().Match(line);
                        if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double pct))
                        {
                            progress.Report(pct / 100.0);
                        }
                    }
                }
            }, token);

            try
            {
                await proc.WaitForExitAsync(token);
                await Task.WhenAll(stdoutReaderTask, errorReaderTask);
            }
            catch (OperationCanceledException)
            {
                proc.Kill();
                throw;
            }
            
            if (proc.ExitCode != 0)
            {
                 _log.Error($"Download failed for {repoId}", new Exception("Python downloader exited with non-zero exit code."));
                 return false;
            }
            
            _log.Info($"Download succeeded for {repoId}.");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"Exception during model download: {ex.Message}", ex);
            return false;
        }
        finally
        {
             if (File.Exists(scriptPath)) File.Delete(scriptPath);
        }
    }

    public async Task<bool> DownloadPiperVoiceAsync(string voiceName, string? piperDir, IProgress<double>? progress = null, CancellationToken token = default)
    {
        var resolvedDir = ResolvePiperModelDir(piperDir);
        if (string.IsNullOrEmpty(resolvedDir))
        {
            _log.Error("PiperModelDir could not be resolved.", new InvalidOperationException("PiperModelDir is null"));
            return false;
        }

        Directory.CreateDirectory(resolvedDir);
        string onnxPath = Path.Combine(resolvedDir, $"{voiceName}.onnx");
        string jsonPath = Path.Combine(resolvedDir, $"{voiceName}.onnx.json");

        // Piper voice string format: {language}_{region}-{name}-{quality}
        // Ex: en_US-lessac-medium
        string[] parts = voiceName.Split('-');
        if (parts.Length < 3)
        {
            _log.Error($"Invalid piper voice name format: {voiceName}", new ArgumentException("Invalid format"));
            return false;
        }
        
        string langRegion = parts[0]; // en_US
        string language = langRegion.Split('_')[0]; // en
        string name = parts[1];
        string quality = parts[2];

        // We build the rhasspy huggingface repository path
        string baseUrl = $"https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/{language}/{langRegion}/{name}/{quality}/{voiceName}";

        _log.Info($"Downloading Piper voice {voiceName}...");

        try
        {
            // Report fake progress to show something since piper downloads two files. 0 to 90% for onnx, 90 to 100% for json.
            var onnxProgress = new Progress<double>(p => progress?.Report(p * 0.90));
            var jsonProgress = new Progress<double>(p => progress?.Report(0.90 + p * 0.10));

            bool onnxOk = await DownloadFileAsync($"{baseUrl}.onnx", onnxPath, onnxProgress, token);
            bool jsonOk = await DownloadFileAsync($"{baseUrl}.onnx.json", jsonPath, jsonProgress, token);

            if (onnxOk && jsonOk)
            {
                _log.Info($"Piper voice {voiceName} downloaded successfully.");
                return true;
            }

            // Partial failure — clean up both files to avoid a corrupt state.
            if (File.Exists(onnxPath)) File.Delete(onnxPath);
            if (File.Exists(jsonPath)) File.Delete(jsonPath);
            return false;
        }
        catch (OperationCanceledException)
        {
             if (File.Exists(onnxPath)) File.Delete(onnxPath);
             if (File.Exists(jsonPath)) File.Delete(jsonPath);
             throw;
        }
        catch (Exception ex)
        {
             _log.Error($"Exception downloading Piper voice {voiceName}: {ex.Message}", ex);
             // Cleanup partial downloads
             if (File.Exists(onnxPath)) File.Delete(onnxPath);
             if (File.Exists(jsonPath)) File.Delete(jsonPath);
             return false;
        }
    }

    private async Task<bool> DownloadFileAsync(string url, string destinationPath, IProgress<double>? progress = null, CancellationToken token = default)
    {
        try
        {
            var destinationDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDir))
                Directory.CreateDirectory(destinationDir);
        }
        catch (Exception ex)
        {
            _log.Warning($"Could not create download directory for '{destinationPath}': {ex.Message}");
            return false;
        }

        string tmpPath = destinationPath + ".tmp";
        try
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;

            using var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);
            using var stream = await response.Content.ReadAsStreamAsync(token);

            var buffer = new byte[8192];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
            {
                await fs.WriteAsync(buffer, 0, bytesRead, token);
                totalRead += bytesRead;
                if (totalBytes.HasValue)
                {
                    progress?.Report((double)totalRead / totalBytes.Value);
                }
            }
            fs.Close();

            if (File.Exists(destinationPath)) File.Delete(destinationPath);
            File.Move(tmpPath, destinationPath);
            return true;
        }
        catch (OperationCanceledException)
        {
            if (File.Exists(tmpPath)) File.Delete(tmpPath);
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to download {url}: {ex.Message}", ex);
            if (File.Exists(tmpPath)) File.Delete(tmpPath);
            return false;
        }
    }

    public static bool IsFasterWhisperDownloaded(string model)
    {
        string hfCache = GetHuggingFaceCacheDir();
        string modelPath = Path.Combine(hfCache, $"models--Systran--faster-whisper-{model}");
        return IsDownloadedInHfCache(modelPath);
    }

    public static bool IsNllbDownloaded(string model)
    {
        string hfCache = GetHuggingFaceCacheDir();
        string modelPath = Path.Combine(hfCache, $"models--facebook--{model}");
        return IsDownloadedInHfCache(modelPath);
    }

    public static bool IsCTranslate2TranslationModelDownloaded(string model)
    {
        string convertedPath = GetCTranslate2TranslationModelDir(model);
        return Directory.Exists(convertedPath)
            && File.Exists(Path.Combine(convertedPath, "model.bin"))
            && File.Exists(Path.Combine(convertedPath, "config.json"))
            && IsNllbDownloaded(model);
    }


    private static bool IsDownloadedInHfCache(string modelPath)
    {
        if (!Directory.Exists(modelPath)) return false;
        string refsPath = Path.Combine(modelPath, "refs", "main");
        if (File.Exists(refsPath)) return true;
        try
        {
            return Directory.GetFiles(modelPath, "*.bin").Length > 0 ||
                   Directory.GetFiles(modelPath, "*.json").Length > 0 ||
                   Directory.GetFiles(modelPath, "*.model").Length > 0;
        }
        catch { return false; }
    }

    public static bool IsPiperVoiceDownloaded(string voice, string? piperDir)
    {
        var resolvedDir = ResolvePiperModelDir(piperDir);
        if (string.IsNullOrEmpty(resolvedDir) || !Directory.Exists(resolvedDir)) return false;
        string onnxPath = Path.Combine(resolvedDir, $"{voice}.onnx");
        string jsonPath = Path.Combine(resolvedDir, $"{voice}.onnx.json");
        return File.Exists(onnxPath) && File.Exists(jsonPath);
    }

    /// <summary>
    /// Lists Piper voice ids that have both <c>{id}.onnx</c> and <c>{id}.onnx.json</c> in the resolved model directory.
    /// </summary>
    public static IReadOnlyList<string> ListDownloadedPiperVoiceIds(string? piperDir)
    {
        var resolvedDir = ResolvePiperModelDir(piperDir);
        if (string.IsNullOrEmpty(resolvedDir) || !Directory.Exists(resolvedDir))
            return [];

        var list = new List<string>();
        foreach (var onnx in Directory.EnumerateFiles(resolvedDir, "*.onnx", SearchOption.TopDirectoryOnly))
        {
            if (onnx.EndsWith(".onnx.json", StringComparison.OrdinalIgnoreCase))
                continue;

            var baseName = Path.GetFileNameWithoutExtension(onnx);
            if (string.IsNullOrWhiteSpace(baseName))
                continue;

            var jsonPath = Path.Combine(resolvedDir, $"{baseName}.onnx.json");
            if (File.Exists(jsonPath))
                list.Add(baseName);
        }

        list.Sort(StringComparer.Ordinal);
        return list;
    }

    /// <summary>
    /// Returns the Piper model directory to use. If <paramref name="piperDir"/> is null or empty,
    /// falls back to the platform-default location that the Piper CLI and Python script also search.
    /// </summary>
    public static string ResolvePiperModelDir(string? piperDir)
    {
        if (!string.IsNullOrEmpty(piperDir))
            return piperDir;

        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, ProviderNames.Piper, "voices");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "share", ProviderNames.Piper, "voices");
    }

    public static string ResolveChatterboxModelDir(string? modelDir)
    {
        if (!string.IsNullOrEmpty(modelDir))
            return modelDir;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "BabelPlayer", "models", "chatterbox-multilingual");
    }

    public static bool IsChatterboxModelDownloaded(string? modelDir)
    {
        var resolvedDir = ResolveChatterboxModelDir(modelDir);
        if (!Directory.Exists(resolvedDir))
            return false;

        foreach (var relativePath in ChatterboxModelCatalog.RequiredFiles)
        {
            var info = new FileInfo(Path.Combine(resolvedDir, relativePath));
            if (!info.Exists || info.Length == 0)
                return false;
        }

        return true;
    }

    public async Task<bool> DownloadChatterboxModelAsync(string? modelDir, IProgress<double>? progress = null, CancellationToken token = default)
    {
        var resolvedDir = ResolveChatterboxModelDir(modelDir);
        try
        {
            Directory.CreateDirectory(resolvedDir);
        }
        catch (Exception ex)
        {
            _log.Warning($"Could not create Chatterbox model directory '{resolvedDir}': {ex.Message}");
            return false;
        }

        var files = ChatterboxModelCatalog.RequiredFiles;
        for (int index = 0; index < files.Count; index++)
        {
            var relativePath = files[index];
            var destinationPath = Path.Combine(resolvedDir, relativePath);
            var existingInfo = new FileInfo(destinationPath);
            if (existingInfo.Exists)
            {
                if (existingInfo.Length > 0)
                {
                    progress?.Report((double)(index + 1) / files.Count);
                    continue;
                }

                try { existingInfo.Delete(); }
                catch { /* re-download below */ }
            }

            var fileProgress = new Progress<double>(p =>
                progress?.Report(((double)index + p) / files.Count));
            _log.Info($"Downloading Chatterbox model file {index + 1}/{files.Count}: {relativePath}");
            if (!await DownloadFileAsync(ChatterboxModelCatalog.ModelDownloadUrl(relativePath), destinationPath, fileProgress, token))
                return false;
        }

        _log.Info("Chatterbox model downloaded successfully.");
        return true;
    }

    public static string ResolveSortFormerModelDir(string? modelDir)
    {
        if (!string.IsNullOrEmpty(modelDir))
            return modelDir;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "BabelPlayer", "models", "sortformer-4spk");
    }

    public static bool IsSortFormerModelDownloaded(string? modelDir)
    {
        var resolvedDir = ResolveSortFormerModelDir(modelDir);
        if (!Directory.Exists(resolvedDir))
            return false;

        try
        {
            foreach (var relativePath in SortFormerModelCatalog.RequiredFiles)
            {
                var info = new FileInfo(Path.Combine(resolvedDir, relativePath));
                if (!info.Exists || info.Length == 0)
                    return false;
            }

            var modelPath = Path.Combine(resolvedDir, SortFormerModelCatalog.RelativeModelPath);
            return SortFormerModelFiles.TryVerifySha256(modelPath, out _);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    public async Task<bool> DownloadSortFormerModelAsync(
        string? modelDir,
        IProgress<double>? progress = null,
        CancellationToken token = default)
    {
        var resolvedDir = ResolveSortFormerModelDir(modelDir);
        try
        {
            Directory.CreateDirectory(resolvedDir);
            Directory.CreateDirectory(Path.Combine(resolvedDir, "onnx"));
        }
        catch (Exception ex)
        {
            _log.Warning($"Could not create SortFormer model directory '{resolvedDir}': {ex.Message}");
            return false;
        }

        var destinationPath = Path.Combine(resolvedDir, SortFormerModelCatalog.RelativeModelPath);
        if (IsSortFormerModelDownloaded(resolvedDir))
        {
            progress?.Report(1.0);
            return true;
        }

        var existingInfo = new FileInfo(destinationPath);
        if (existingInfo.Exists)
        {
            try { existingInfo.Delete(); }
            catch { /* re-download below */ }
        }

        _log.Info($"Downloading SortFormer model: {SortFormerModelCatalog.HubFileName}");
        if (!await DownloadFileAsync(SortFormerModelCatalog.ModelDownloadUrl, destinationPath, progress, token))
            return false;

        if (!SortFormerModelFiles.TryVerifySha256(destinationPath, out var actualHash))
        {
            _log.Warning(
                $"SortFormer download failed SHA-256 verification (got {actualHash ?? "<none>"}, " +
                $"expected {SortFormerModelCatalog.Sha256}).");
            try { File.Delete(destinationPath); }
            catch { /* best effort */ }
            return false;
        }

        _log.Info("SortFormer model downloaded successfully.");
        progress?.Report(1.0);
        return true;
    }

    private static string GetHuggingFaceCacheDir()
    {
        string? hfHome = Environment.GetEnvironmentVariable("HF_HOME");
        if (!string.IsNullOrEmpty(hfHome)) return Path.Combine(hfHome, "hub");
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".cache", "huggingface", "hub");
    }

    public static string GetCTranslate2TranslationModelDir(string model)
    {
        var root = OperatingSystem.IsWindows()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BabelPlayer",
                "translation-models",
                "ctranslate2")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "share",
                "BabelPlayer",
                "translation-models",
                "ctranslate2");

        return Path.Combine(root, SanitizeModelDirectoryName(model));
    }

    private async Task<bool> RunPythonModelPrepScriptAsync(
        string pythonPath,
        string scriptContent,
        string[] arguments,
        IProgress<double>? progress,
        CancellationToken token,
        string logLabel)
    {
        string scriptPath = Path.Combine(Path.GetTempPath(), $"model_prep_{Guid.NewGuid():N}.py");
        await File.WriteAllTextAsync(scriptPath, scriptContent, token);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = pythonPath,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add(scriptPath);
            foreach (var argument in arguments)
                psi.ArgumentList.Add(argument);

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            _log.Info($"Started model preparation for {logLabel}. This may take a few minutes...");

            var stdoutTask = Task.Run(async () =>
            {
                while (true)
                {
                    var line = await proc.StandardOutput.ReadLineAsync(token);
                    if (line == null) break;

                    TryReportProgress(line, progress);
                    _log.Debug($"[model-prep] {line}");
                }
            }, token);

            var stderrTask = Task.Run(async () =>
            {
                while (true)
                {
                    var line = await proc.StandardError.ReadLineAsync(token);
                    if (line == null) break;

                    TryReportProgress(line, progress);
                    _log.Warning($"[model-prep] {line}");
                }
            }, token);

            try
            {
                await proc.WaitForExitAsync(token);
                await Task.WhenAll(stdoutTask, stderrTask);
            }
            catch (OperationCanceledException)
            {
                proc.Kill();
                throw;
            }

            if (proc.ExitCode != 0)
            {
                _log.Error($"Model preparation failed for {logLabel}", new Exception($"Python downloader exited with code {proc.ExitCode}."));
                return false;
            }

            progress?.Report(1.0);
            _log.Info($"Model preparation succeeded for {logLabel}.");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"Exception during model preparation: {ex.Message}", ex);
            return false;
        }
        finally
        {
            if (File.Exists(scriptPath))
                File.Delete(scriptPath);
        }
    }

    private static void TryReportProgress(string line, IProgress<double>? progress)
    {
        if (progress is null)
            return;

        var explicitMatch = ExplicitProgressRegex().Match(line);
        if (explicitMatch.Success && double.TryParse(explicitMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var explicitPct))
        {
            progress.Report(explicitPct / 100.0);
            return;
        }

        var genericMatch = ProgressRegex().Match(line);
        if (genericMatch.Success && double.TryParse(genericMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
            progress.Report(pct / 100.0);
    }

    private static string SanitizeModelDirectoryName(string model)
    {
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
            model = model.Replace(invalidChar, '_');

        return model.Replace('/', '_').Replace('\\', '_');
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(\d+(?:\.\d+)?)%")]
    private static partial System.Text.RegularExpressions.Regex ProgressRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\[progress\]\s*(\d+(?:\.\d+)?)")]
    private static partial System.Text.RegularExpressions.Regex ExplicitProgressRegex();
}
