using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace Babel.Player.Services.SortFormer;

internal sealed record SortFormerModelFiles(string RootDirectory, string ModelPath)
{
    private static readonly Lock VerifyCacheGate = new();
    private static string? _cachedPath;
    private static long _cachedLength;
    private static long _cachedLastWriteUtcTicks;
    private static string? _cachedHash;
    private static bool _cachedMatchesExpected;

    public static SortFormerModelFiles Resolve(string modelDir)
    {
        var rootDirectory = Path.GetFullPath(modelDir);
        var modelPath = Path.Combine(rootDirectory, SortFormerModelCatalog.RelativeModelPath);
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("SortFormer diarization requires the ONNX model package.", modelPath);

        var info = new FileInfo(modelPath);
        if (info.Length == 0)
            throw new InvalidDataException($"SortFormer model file is empty: {modelPath}");

        return new SortFormerModelFiles(rootDirectory, modelPath);
    }

    public static bool TryVerifySha256(string modelPath, out string? actualHash)
    {
        actualHash = null;
        try
        {
            if (!File.Exists(modelPath))
                return false;

            var info = new FileInfo(modelPath);
            if (info.Length == 0)
                return false;

            long lastWriteTicks = info.LastWriteTimeUtc.Ticks;
            lock (VerifyCacheGate)
            {
                if (_cachedPath is not null
                    && string.Equals(_cachedPath, modelPath, StringComparison.OrdinalIgnoreCase)
                    && _cachedLength == info.Length
                    && _cachedLastWriteUtcTicks == lastWriteTicks)
                {
                    actualHash = _cachedHash;
                    return _cachedMatchesExpected;
                }
            }

            using var stream = File.OpenRead(modelPath);
            var hash = SHA256.HashData(stream);
            actualHash = Convert.ToHexString(hash).ToLowerInvariant();
            bool matches = string.Equals(actualHash, SortFormerModelCatalog.Sha256, StringComparison.OrdinalIgnoreCase);

            lock (VerifyCacheGate)
            {
                _cachedPath = modelPath;
                _cachedLength = info.Length;
                _cachedLastWriteUtcTicks = lastWriteTicks;
                _cachedHash = actualHash;
                _cachedMatchesExpected = matches;
            }

            return matches;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
