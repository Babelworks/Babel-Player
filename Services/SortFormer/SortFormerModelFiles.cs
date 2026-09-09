using System;
using System.IO;
using System.Security.Cryptography;

namespace Babel.Player.Services.SortFormer;

internal sealed record SortFormerModelFiles(string RootDirectory, string ModelPath)
{
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

            using var stream = File.OpenRead(modelPath);
            var hash = SHA256.HashData(stream);
            actualHash = Convert.ToHexString(hash).ToLowerInvariant();
            return string.Equals(actualHash, SortFormerModelCatalog.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
