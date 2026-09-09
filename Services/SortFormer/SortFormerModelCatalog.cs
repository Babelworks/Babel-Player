using System.Collections.Generic;
using System.IO;

namespace Babel.Player.Services.SortFormer;

internal static class SortFormerModelCatalog
{
    public const string ModelId = "sortformer-4spk";
    public const string RepositoryId = "cgus/diar_streaming_sortformer_4spk-v2.1-onnx";
    public const string MirrorRepositoryId = "tonythethompson/diar-streaming-sortformer-4spk-v2.1-onnx";
    public const string HubFileName = "diar_streaming_sortformer_4spk-v2.1.onnx";
    public static string RelativeModelPath => Path.Combine("onnx", "model.onnx");
    public const string Sha256 = "82b9c735e1cfc6b36b4ff8a994d9a0573e922d0e80a58a8553b2c58f7aff0c00";
    public const string License = "NVIDIA-Open-Model-License";
    public const string LicenseUrl = "https://huggingface.co/cgus/diar_streaming_sortformer_4spk-v2.1-onnx";
    public const string SourceUrl = "https://huggingface.co/cgus/diar_streaming_sortformer_4spk-v2.1-onnx";

    public static string ModelDownloadUrl =>
        $"https://huggingface.co/{RepositoryId}/resolve/main/{HubFileName}";

    public static IReadOnlyList<string> RequiredFiles { get; } = new[]
    {
        RelativeModelPath,
    };
}
