using System;
using Babel.Player.Models;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

/// <summary>
/// Centralizes stage-specific compute-profile, runtime, and provider normalization so settings
/// migration, registries, and session provenance use one mapping.
/// </summary>
public static class InferenceRuntimeCatalog
{
    public static ComputeProfile MapLegacyRuntimeToProfile(InferenceRuntime runtime) => runtime switch
    {
        InferenceRuntime.Containerized => ComputeProfile.Gpu,
        InferenceRuntime.Cloud => ComputeProfile.Cloud,
        _ => ComputeProfile.Cpu,
    };

    public static InferenceRuntime ResolveRuntime(ComputeProfile profile) => profile switch
    {
        ComputeProfile.Gpu => InferenceRuntime.Containerized,
        ComputeProfile.Cloud => InferenceRuntime.Cloud,
        _ => InferenceRuntime.Local,
    };

    public static ComputeProfile InferTranscriptionProfile(string? providerId) => providerId switch
    {
        ProviderNames.OpenAiWhisperApi
            or ProviderNames.GoogleStt
            or ProviderNames.GeminiTranscription => ComputeProfile.Cloud,
        ProviderNames.Parakeet => ComputeProfile.Gpu,
        _ => ComputeProfile.Cpu,
    };

    public static ComputeProfile InferTranslationProfile(string? providerId) => providerId switch
    {
        ProviderNames.Deepl
            or ProviderNames.OpenAi
            or ProviderNames.GeminiTranslation => ComputeProfile.Cloud,
        _ => ComputeProfile.Cpu,
    };

    public static ComputeProfile InferTtsProfile(string? providerId) => providerId switch
    {
        ProviderNames.Piper => ComputeProfile.Cpu,
        ProviderNames.Chatterbox => ComputeProfile.Cpu,
        ProviderNames.Qwen => ComputeProfile.Gpu,
        _ => ComputeProfile.Cloud,
    };

    public static string DefaultTranscriptionProvider(ComputeProfile profile) => profile switch
    {
        ComputeProfile.Cloud => ProviderNames.OpenAiWhisperApi,
        _ => ProviderNames.FasterWhisper,
    };

    public static string DefaultTranslationProvider(ComputeProfile profile) => profile switch
    {
        ComputeProfile.Cpu or ComputeProfile.Gpu => ProviderNames.Nllb200,
        _ => ProviderNames.Deepl,
    };

    public static string DefaultTtsProvider(ComputeProfile profile) => profile switch
    {
        ComputeProfile.Cpu => ProviderNames.Piper,
        ComputeProfile.Gpu => ProviderNames.Qwen,
        _ => ProviderNames.EdgeTts,
    };

    public static string DefaultTranscriptionProvider(InferenceRuntime runtime) =>
        DefaultTranscriptionProvider(MapLegacyRuntimeToProfile(runtime));

    public static string DefaultTranslationProvider(InferenceRuntime runtime) =>
        DefaultTranslationProvider(MapLegacyRuntimeToProfile(runtime));

    /// <summary>
    /// Selects the default TTS provider identifier for a legacy inference runtime.
    /// </summary>
    /// <param name="runtime">The legacy <see cref="InferenceRuntime"/> used to determine the compute profile.</param>
    /// <returns>
    /// The provider identifier to use by default: <see cref="ProviderNames.Piper"/> when the runtime maps to a CPU profile, <see cref="ProviderNames.Qwen"/> when it maps to a GPU profile, and <see cref="ProviderNames.EdgeTts"/> otherwise.
    /// </returns>
    public static string DefaultTtsProvider(InferenceRuntime runtime) =>
        DefaultTtsProvider(MapLegacyRuntimeToProfile(runtime));

    /// <summary>
    /// Gets the canonical provider identifier used when diarization is enabled but the stored value is missing or unrecognized.
    /// </summary>
    /// <returns>The provider ID for the default on-device diarization engine: <see cref="ProviderNames.WeSpeakerLocal"/>.</returns>
    public static string DefaultDiarizationProvider() => ProviderNames.WeSpeakerLocal;

    /// <summary>
    /// Determines the effective InferenceRuntime for a diarization provider identifier.
    /// </summary>
    /// <param name="providerId">The diarization provider identifier or null; accepts canonical names and legacy aliases.</param>
    /// <returns><see cref="InferenceRuntime.Containerized"/> when the provider is NeMo (canonical or legacy alias); <see cref="InferenceRuntime.Local"/> for WeSpeaker (canonical or legacy alias) or any other/unknown value.</returns>
    public static InferenceRuntime InferDiarizationRuntime(string? providerId) => providerId switch
    {
        ProviderNames.NemoLocal or ProviderNames.NemoDiarizationAlias => InferenceRuntime.Containerized,
        ProviderNames.WeSpeakerLocal or ProviderNames.WeSpeakerDiarizationAlias => InferenceRuntime.Local,
        ProviderNames.SortFormerLocal or ProviderNames.SortFormerDiarizationAlias => InferenceRuntime.Local,
        _ => InferenceRuntime.Local,
    };

    /// <summary>
    /// Normalizes a transcription provider identifier to a canonical provider ID appropriate for the given compute profile.
    /// </summary>
    /// <param name="profile">The compute profile used to determine which providers are appropriate.</param>
    /// <param name="providerId">The incoming provider identifier, which may be null or whitespace.</param>
    /// <returns>
    /// A normalized provider identifier: a known provider suitable for the profile, the original <paramref name="providerId"/> if it is not a recognized transcription provider, or the profile's default transcription provider when the input is null/whitespace, references the containerized service, or is not suitable for the profile.
    /// </returns>
    public static string NormalizeTranscriptionProvider(ComputeProfile profile, string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            return DefaultTranscriptionProvider(profile);

        if (string.Equals(providerId, ProviderNames.ContainerizedService, StringComparison.Ordinal))
            return DefaultTranscriptionProvider(profile);

        if (!IsKnownTranscriptionProvider(providerId))
            return providerId;

        if (profile == ComputeProfile.Cloud)
        {
            return providerId switch
            {
                ProviderNames.GoogleStt => ProviderNames.GoogleStt,
                ProviderNames.GeminiTranscription => ProviderNames.GeminiTranscription,
                ProviderNames.OpenAiWhisperApi => ProviderNames.OpenAiWhisperApi,
                _ => DefaultTranscriptionProvider(profile),
            };
        }

        if (providerId == ProviderNames.Parakeet && profile == ComputeProfile.Gpu)
            return ProviderNames.Parakeet;

        return ProviderNames.FasterWhisper;
    }

    public static string NormalizeTranslationProvider(ComputeProfile profile, string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            return DefaultTranslationProvider(profile);

        if (string.Equals(providerId, ProviderNames.ContainerizedService, StringComparison.Ordinal))
            return DefaultTranslationProvider(profile);

        if (!IsKnownTranslationProvider(providerId))
            return providerId;

        if (profile == ComputeProfile.Cpu)
        {
            return providerId switch
            {
                ProviderNames.CTranslate2 => ProviderNames.CTranslate2,
                ProviderNames.Nllb200 => ProviderNames.Nllb200,
                _ => DefaultTranslationProvider(profile),
            };
        }

        if (profile == ComputeProfile.Gpu)
            return ProviderNames.Nllb200;

        return providerId switch
        {
            ProviderNames.Deepl => ProviderNames.Deepl,
            ProviderNames.OpenAi => ProviderNames.OpenAi,
            ProviderNames.GeminiTranslation => ProviderNames.GeminiTranslation,
            _ => DefaultTranslationProvider(profile),
        };
    }

    /// <summary>
    /// Normalizes a TTS provider identifier for the given compute profile.
    /// </summary>
    /// <param name="profile">The compute profile used to choose or constrain the provider.</param>
    /// <param name="providerId">The requested provider identifier, or null/whitespace to use the profile default.</param>
    /// <returns>
    /// The canonical provider identifier to use:
    /// - If <paramref name="providerId"/> is null/whitespace or equals <see cref="ProviderNames.ContainerizedService"/>, returns the profile's default TTS provider.
    /// - If <paramref name="providerId"/> is not a recognized TTS provider, returns <paramref name="providerId"/> unchanged.
    /// - For <see cref="ComputeProfile.Cpu"/>, returns <see cref="ProviderNames.Piper"/>; for <see cref="ComputeProfile.Gpu"/>, returns <see cref="ProviderNames.Qwen"/>.
    /// - For other profiles, preserves known cloud TTS providers (<see cref="ProviderNames.ElevenLabs"/>, <see cref="ProviderNames.GoogleCloudTts"/>, <see cref="ProviderNames.OpenAiTts"/>, <see cref="ProviderNames.EdgeTts"/>) or falls back to the profile default.
    /// </returns>
    public static string NormalizeTtsProvider(ComputeProfile profile, string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            return DefaultTtsProvider(profile);

        if (string.Equals(providerId, ProviderNames.ContainerizedService, StringComparison.Ordinal))
            return DefaultTtsProvider(profile);

        if (!IsKnownTtsProvider(providerId))
            return providerId;

        return profile switch
        {
            ComputeProfile.Cpu => providerId switch
            {
                ProviderNames.Chatterbox => ProviderNames.Chatterbox,
                _ => ProviderNames.Piper,
            },
            ComputeProfile.Gpu => ProviderNames.Qwen,
            _ => providerId switch
            {
                ProviderNames.ElevenLabs => ProviderNames.ElevenLabs,
                ProviderNames.GoogleCloudTts => ProviderNames.GoogleCloudTts,
                ProviderNames.OpenAiTts => ProviderNames.OpenAiTts,
                ProviderNames.EdgeTts => ProviderNames.EdgeTts,
                _ => DefaultTtsProvider(profile),
            },
        };
    }

    /// <summary>
    /// Normalizes a diarization provider identifier, accepting legacy aliases and falling back to the default when unspecified or unrecognized.
    /// </summary>
    /// <param name="providerId">The provider identifier or legacy alias (may be null or whitespace).</param>
    /// <returns>
    /// The canonical diarization provider identifier. Null values fall back to the default provider,
    /// empty or whitespace values preserve the disabled state as an empty string, and unrecognized
    /// non-empty values fall back to the default provider identifier.
    /// </returns>
    public static string NormalizeDiarizationProvider(string? providerId)
    {
        if (providerId is null)
            return DefaultDiarizationProvider();

        if (string.IsNullOrWhiteSpace(providerId))
            return string.Empty;

        var normalized = providerId.Trim();
        normalized = normalized switch
        {
            ProviderNames.NemoDiarizationAlias => ProviderNames.NemoLocal,
            ProviderNames.WeSpeakerDiarizationAlias => ProviderNames.WeSpeakerLocal,
            ProviderNames.SortFormerDiarizationAlias => ProviderNames.SortFormerLocal,
            _ => normalized,
        };

        // Product UI only offers WeSpeaker/SortFormer; migrate legacy NeMo selections without dropping the registry entry.
        if (string.Equals(normalized, ProviderNames.NemoLocal, StringComparison.Ordinal))
            return ProviderNames.WeSpeakerLocal;

        return IsKnownDiarizationProvider(normalized)
            ? normalized
            : DefaultDiarizationProvider();
    }

    /// <summary>
    /// Normalizes a diarization capability provider identifier to a canonical provider ID.
    /// </summary>
    /// <param name="providerId">The provider identifier or legacy alias (may be null or empty). Recognized aliases: <see cref="ProviderNames.NemoDiarizationAlias"/> and <see cref="ProviderNames.WeSpeakerDiarizationAlias"/>.</param>
    /// <returns>
    /// The canonical provider ID: `ProviderNames.NemoLocal` for <see cref="ProviderNames.NemoDiarizationAlias"/> or `ProviderNames.NemoLocal`,
    /// `ProviderNames.WeSpeakerLocal` for <see cref="ProviderNames.WeSpeakerDiarizationAlias"/> or `ProviderNames.WeSpeakerLocal`,
    /// or the original `providerId` if non-null and unrecognized; otherwise an empty string.
    /// </returns>
    public static string NormalizeDiarizationCapabilityProviderId(string? providerId) => providerId switch
    {
        ProviderNames.NemoDiarizationAlias or ProviderNames.NemoLocal => ProviderNames.NemoLocal,
        ProviderNames.WeSpeakerDiarizationAlias or ProviderNames.WeSpeakerLocal => ProviderNames.WeSpeakerLocal,
        ProviderNames.SortFormerDiarizationAlias or ProviderNames.SortFormerLocal => ProviderNames.SortFormerLocal,
        _ => providerId ?? string.Empty,
    };

    /// <summary>
    /// Determine the legacy InferenceRuntime that best fits a transcription provider identifier.
    /// </summary>
    /// <param name="providerId">The transcription provider identifier to use when inferring the runtime; may be null or whitespace.</param>
    /// <returns>The inferred <see cref="InferenceRuntime"/> for the specified provider.</returns>
    public static InferenceRuntime InferTranscriptionRuntime(string? providerId) =>
        ResolveRuntime(InferTranscriptionProfile(providerId));

    public static InferenceRuntime InferTranslationRuntime(string? providerId) =>
        ResolveRuntime(InferTranslationProfile(providerId));

    public static InferenceRuntime InferTtsRuntime(string? providerId) =>
        ResolveRuntime(InferTtsProfile(providerId));

    public static string NormalizeTranscriptionProvider(InferenceRuntime runtime, string? providerId) =>
        NormalizeTranscriptionProvider(MapLegacyRuntimeToProfile(runtime), providerId);

    public static string NormalizeTranslationProvider(InferenceRuntime runtime, string? providerId) =>
        NormalizeTranslationProvider(MapLegacyRuntimeToProfile(runtime), providerId);

    public static string NormalizeTtsProvider(InferenceRuntime runtime, string? providerId) =>
        NormalizeTtsProvider(MapLegacyRuntimeToProfile(runtime), providerId);

    /// <summary>
    /// Normalizes and normalizes compute profiles and provider identifiers on the given <see cref="AppSettings"/> instance.
    /// </summary>
    /// <param name="settings">The settings object to normalize; updated in place.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="settings"/> is null.</exception>
    public static void NormalizeSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.TranscriptionProfile = ResolveConfiguredProfile(
            settings.TranscriptionProfile,
            settings.TranscriptionProvider,
            InferTranscriptionProfile);
        settings.TranslationProfile = ResolveConfiguredProfile(
            settings.TranslationProfile,
            settings.TranslationProvider,
            InferTranslationProfile);
        settings.TtsProfile = ResolveConfiguredProfile(
            settings.TtsProfile,
            settings.TtsProvider,
            InferTtsProfile);

        settings.TranscriptionProvider = NormalizeTranscriptionProvider(
            settings.TranscriptionProfile,
            settings.TranscriptionProvider);
        settings.TranslationProvider = NormalizeTranslationProvider(
            settings.TranslationProfile,
            settings.TranslationProvider);
        settings.TtsProvider = NormalizeTtsProvider(
            settings.TtsProfile,
            settings.TtsProvider);
        settings.DiarizationProvider = NormalizeDiarizationProvider(settings.DiarizationProvider);
    }

    /// <summary>
    /// Resolve the effective compute profile using the configured profile and an optional provider ID.
    /// </summary>
    /// <param name="configuredProfile">The configured compute profile to use as the default.</param>
    /// <param name="providerId">The provider identifier whose value can override the configured profile; whitespace or null will not override.</param>
    /// <param name="inferProfile">Accepted for API compatibility; not used by this implementation.</param>
    /// <returns>`ComputeProfile.Gpu` when <paramref name="providerId"/> equals <see cref="ProviderNames.ContainerizedService"/>, otherwise the provided <paramref name="configuredProfile"/>.</returns>
    private static ComputeProfile ResolveConfiguredProfile(
        ComputeProfile configuredProfile,
        string? providerId,
        Func<string?, ComputeProfile> inferProfile)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            return configuredProfile;

        return providerId switch
        {
            ProviderNames.ContainerizedService => ComputeProfile.Gpu,
            _ => configuredProfile,
        };
    }

    public static bool IsKnownTranscriptionProvider(string? providerId) => providerId switch
    {
        ProviderNames.FasterWhisper
            or ProviderNames.OpenAiWhisperApi
            or ProviderNames.GoogleStt
            or ProviderNames.GeminiTranscription
            or ProviderNames.Parakeet => true,
        _ => false,
    };

    public static bool IsKnownTranslationProvider(string? providerId) => providerId switch
    {
        ProviderNames.Nllb200
            or ProviderNames.CTranslate2
            or ProviderNames.Deepl
            or ProviderNames.OpenAi
            or ProviderNames.GeminiTranslation => true,
        _ => false,
    };

    /// <summary>
    /// Checks whether the given provider identifier is a recognized text-to-speech (TTS) provider.
    /// </summary>
    /// <param name="providerId">The provider identifier to test; may be null.</param>
    /// <returns>`true` if <paramref name="providerId"/> matches one of: `Piper`, `EdgeTts`, `ElevenLabs`, `GoogleCloudTts`, `OpenAiTts`, or `Qwen`; `false` otherwise.</returns>
    public static bool IsKnownTtsProvider(string? providerId) => providerId switch
    {
        ProviderNames.Piper
            or ProviderNames.Chatterbox
            or ProviderNames.EdgeTts
            or ProviderNames.ElevenLabs
            or ProviderNames.GoogleCloudTts
            or ProviderNames.OpenAiTts
            or ProviderNames.Qwen => true,
        _ => false,
    };

    /// <summary>
    /// Determines whether the specified diarization provider identifier is a known canonical provider.
    /// </summary>
    /// <param name="providerId">The provider identifier to check; may be null or whitespace.</param>
    /// <returns>`true` if the identifier is a recognized diarization provider (`ProviderNames.NemoLocal` or `ProviderNames.WeSpeakerLocal`), `false` otherwise.</returns>
    public static bool IsKnownDiarizationProvider(string? providerId) => providerId switch
    {
        ProviderNames.NemoLocal
            or ProviderNames.WeSpeakerLocal
            or ProviderNames.SortFormerLocal => true,
        _ => false,
    };
}
