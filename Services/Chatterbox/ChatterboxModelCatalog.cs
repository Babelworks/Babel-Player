using System.Collections.Generic;

namespace Babel.Player.Services.Chatterbox;

internal static class ChatterboxModelCatalog
{
    public const string ModelId = "chatterbox-multilingual";
    public const string RepositoryId = "onnx-community/chatterbox-multilingual-ONNX";
    public const string Revision = "452d3f434aa592098f1eedac9099f33642ab2da5";
    public const string License = "MIT";
    public const string LicenseUrl = "https://huggingface.co/onnx-community/chatterbox-multilingual-ONNX";

    public static string ModelDownloadUrl(string relativePath) =>
        $"https://huggingface.co/{RepositoryId}/resolve/{Revision}/{relativePath}";

    public static IReadOnlyList<string> RequiredFiles { get; } = new[]
    {
        "tokenizer.json",
        "onnx/speech_encoder.onnx",
        "onnx/speech_encoder.onnx_data",
        "onnx/embed_tokens.onnx",
        "onnx/embed_tokens.onnx_data",
        "onnx/language_model.onnx",
        "onnx/language_model.onnx_data",
        "onnx/conditional_decoder.onnx",
        "onnx/conditional_decoder.onnx_data",
    };

    // Chinese is supported by the upstream model only with the Cangjie preprocessing
    // pipeline (Cangjie5 mapping + segmentation); until that lands, zh stays rejected
    // by ApplyMultilingualLanguagePrefix rather than synthesizing degraded output.
    public static IReadOnlySet<string> SupportedLanguages { get; } = new HashSet<string>(
        new[] { "ar", "da", "de", "el", "en", "es", "fi", "fr", "he", "hi", "it", "ja", "ko", "ms", "nl", "no", "pl", "pt", "ru", "sv", "sw", "tr" },
        System.StringComparer.OrdinalIgnoreCase);
}
