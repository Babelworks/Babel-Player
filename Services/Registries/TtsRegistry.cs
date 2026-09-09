using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Models.LanguageSupport;
using Babel.Player.Services.Chatterbox;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services.Registries;

public interface ITtsRegistry
{
    IReadOnlyList<ProviderDescriptor> GetAvailableProviders(ComputeProfile? profile = null);
    IReadOnlyList<string> GetAvailableModels(string providerId, ComputeProfile profile, AppSettings settings);
    ITtsProvider CreateProvider(string providerId, AppSettings settings, ApiKeyStore? keyStore = null, ComputeProfile? profile = null);
    ProviderReadiness CheckReadiness(string providerId, string modelOrVoice, AppSettings settings, ApiKeyStore? keyStore, ComputeProfile? profile = null);
    Task<bool> EnsureModelAsync(string providerId, string modelOrVoice, AppSettings settings,
                                 IProgress<double>? progress = null, CancellationToken ct = default, ComputeProfile? profile = null, ApiKeyStore? keyStore = null);
}

public sealed class TtsRegistry : ITtsRegistry
{
    private readonly AppLog _log;
    private readonly ContainerizedServiceProbe? _containerizedProbe;
    private readonly IAudioProcessingService? _audioProcessingService;
    private readonly ContainerizedRequestLeaseTracker? _requestLeaseTracker;

    /// <summary>
    /// Creates a TtsRegistry and stores runtime and service dependencies used to resolve and instantiate TTS providers.
    /// </summary>
    /// <param name="log">Application logger used by the registry.</param>
    /// <param name="containerizedProbe">Probe for checking containerized TTS runtime availability; may be null.</param>
    /// <param name="audioProcessingService">Optional audio processing service used by some providers; may be null.</param>
    /// <param name="requestLeaseTracker">Optional tracker for containerized request leases passed to containerized providers; may be null.</param>
    public TtsRegistry(
        AppLog log,
        ContainerizedServiceProbe? containerizedProbe = null,
        IAudioProcessingService? audioProcessingService = null,
        ContainerizedRequestLeaseTracker? requestLeaseTracker = null)
    {
        _log = log;
        _containerizedProbe = containerizedProbe;
        _audioProcessingService = audioProcessingService;
        _requestLeaseTracker = requestLeaseTracker;
    }

    /// <summary>
    /// Lists TTS provider descriptors available for the given compute profile.
    /// </summary>
    /// <param name="profile">Optional compute profile to filter providers; when null returns local (CPU), GPU, and cloud providers combined.</param>
    /// <returns>A read-only list of ProviderDescriptor objects representing providers supported for the specified profile; when no descriptor matches a profile the list will be empty for that profile.</returns>
    public IReadOnlyList<ProviderDescriptor> GetAvailableProviders(ComputeProfile? profile = null)
    {
        if (profile is null)
        {
            return
            [
                .. GetAvailableProviders(ComputeProfile.Cpu),
                .. GetAvailableProviders(ComputeProfile.Gpu),
                .. GetAvailableProviders(ComputeProfile.Cloud),
            ];
        }

        if (profile == ComputeProfile.Cpu)
        {
            return
            [
                new(
                    ProviderNames.Piper,
                    "Piper (Local)",
                    false,
                    null,
                    PiperTtsCatalog.VoiceIds,
                    SupportedRuntimes: [InferenceRuntime.Local],
                    DefaultRuntime: InferenceRuntime.Local),
                new(
                    ProviderNames.Chatterbox,
                    "Chatterbox (Voice Cloning, Local)",
                    false,
                    null,
                    [ChatterboxModelCatalog.ModelId],
                    SupportedRuntimes: [InferenceRuntime.Local],
                    DefaultRuntime: InferenceRuntime.Local),
            ];
        }

        if (profile == ComputeProfile.Gpu)
        {
            return
            [
                new(
                    ProviderNames.Qwen,
                    "Qwen3-TTS (Local GPU Host)",
                    false,
                    null,
                    QwenTtsCatalog.ModelIds,
                    SupportedRuntimes: [InferenceRuntime.Containerized],
                    DefaultRuntime: InferenceRuntime.Containerized,
                    IsImplemented: true),
            ];
        }

        var providers = new List<ProviderDescriptor>
        {
            new(
                ProviderNames.EdgeTts,
                "Edge TTS",
                false,
                null,
                EdgeTtsCatalog.VoiceIds,
                SupportedRuntimes: [InferenceRuntime.Cloud],
                DefaultRuntime: InferenceRuntime.Cloud),
            new(
                ProviderNames.ElevenLabs,
                "ElevenLabs API",
                true,
                CredentialKeys.ElevenLabs,
                ["eleven_multilingual_v2", "eleven_turbo_v2_5", "eleven_flash_v2_5"],
                SupportedRuntimes: [InferenceRuntime.Cloud],
                DefaultRuntime: InferenceRuntime.Cloud,
                IsImplemented: true),
            new(
                ProviderNames.OpenAiTts,
                "OpenAI API",
                true,
                CredentialKeys.OpenAi,
                ["tts-1", "tts-1-hd", "gpt-4o-mini-tts"],
                SupportedRuntimes: [InferenceRuntime.Cloud],
                DefaultRuntime: InferenceRuntime.Cloud,
                IsImplemented: true),
        };

        return providers;
    }

    public IReadOnlyList<string> GetAvailableModels(string providerId, ComputeProfile profile, AppSettings settings)
    {
        var normalizedProviderId = ResolveProviderId(providerId, settings, profile);
        if (profile == ComputeProfile.Gpu && string.Equals(normalizedProviderId, ProviderNames.Qwen, StringComparison.Ordinal))
            return QwenTtsCatalog.ModelIds;

        return GetAvailableProviders(profile)
            .FirstOrDefault(p => p.Id == normalizedProviderId)?.SupportedModels
            ?? ["default"];
    }

    public ProviderReadiness CheckReadiness(string providerId, string modelOrVoice, AppSettings settings, ApiKeyStore? keyStore, ComputeProfile? profile = null)
    {
        var resolvedProfile = ResolveProfile(providerId, settings, profile);
        var resolvedRuntime = InferenceRuntimeCatalog.ResolveRuntime(resolvedProfile);
        var normalizedProviderId = ResolveProviderId(providerId, settings, resolvedProfile);
        var desc = GetAvailableProviders(resolvedProfile).FirstOrDefault(p => p.Id == normalizedProviderId);
        if (desc == null)
            return new ProviderReadiness(false, $"Unknown TTS provider '{normalizedProviderId}'.");
        if (!desc.IsImplemented)
            return new ProviderReadiness(false, $"Provider '{desc.DisplayName}' is not implemented yet.");
        if (desc.RequiresApiKey && string.IsNullOrEmpty(keyStore?.GetKey(desc.CredentialKey!)))
            return new ProviderReadiness(false, $"API key missing for provider '{desc.DisplayName}'.");

        if (resolvedRuntime == InferenceRuntime.Containerized)
            return ContainerizedProviderReadiness.CheckTts(settings, serviceProbe: _containerizedProbe);

        var provider = CreateProvider(normalizedProviderId, settings, keyStore, resolvedProfile);
        return provider.CheckReadiness(settings, keyStore);
    }

    public async Task<bool> EnsureModelAsync(string providerId, string modelOrVoice, AppSettings settings,
                                              IProgress<double>? progress = null, CancellationToken ct = default, ComputeProfile? profile = null, ApiKeyStore? keyStore = null)
    {
        var resolvedProfile = ResolveProfile(providerId, settings, profile);
        var normalizedProviderId = ResolveProviderId(providerId, settings, resolvedProfile);
        var desc = GetAvailableProviders(resolvedProfile).FirstOrDefault(p => p.Id == normalizedProviderId);
        if (desc == null || !desc.IsImplemented) return false;
        var provider = CreateProvider(normalizedProviderId, settings, keyStore, resolvedProfile);
        return await provider.EnsureReadyAsync(settings, progress, ct);
    }

    /// <summary>
    /// Create an ITtsProvider instance for the resolved provider identifier and runtime.
    /// </summary>
    /// <param name="providerId">The requested provider identifier or alias to resolve.</param>
    /// <param name="settings">Application settings used to resolve provider normalization, profile and runtime.</param>
    /// <param name="keyStore">Optional store to obtain provider API keys (used for cloud providers).</param>
    /// <param name="profile">Optional compute profile override; if null the profile is inferred from settings or provider.</param>
    /// <returns>The concrete ITtsProvider implementation corresponding to the resolved provider and runtime.</returns>
    /// <exception cref="PipelineProviderException">Thrown when the requested provider or containerized provider is not implemented.</exception>
    public ITtsProvider CreateProvider(string providerId, AppSettings settings, ApiKeyStore? keyStore = null, ComputeProfile? profile = null)
    {
        var resolvedProfile = ResolveProfile(providerId, settings, profile);
        var resolvedRuntime = InferenceRuntimeCatalog.ResolveRuntime(resolvedProfile);
        var normalizedProviderId = ResolveProviderId(providerId, settings, resolvedProfile);

        if (resolvedRuntime == InferenceRuntime.Containerized)
        {
            return normalizedProviderId switch
            {
                ProviderNames.Qwen => new QwenContainerTtsProvider(
                    new ContainerizedInferenceClient(settings.EffectiveContainerizedServiceUrl, _log, null, _requestLeaseTracker),
                    _log,
                    new TtsReferenceExtractor(_log),
                    _audioProcessingService),
                _ => throw new PipelineProviderException(
                    $"Containerized TTS provider '{providerId}' is not implemented. Select a supported provider.")
            };
        }

        return normalizedProviderId switch
        {
            ProviderNames.Piper => new PiperTtsProvider(_log, settings.PiperModelDir),
            ProviderNames.Chatterbox => new ChatterboxTtsProvider(
                _log,
                ModelDownloader.ResolveChatterboxModelDir(settings.ChatterboxModelDir),
                settings.ChatterboxVoiceCloneConsent),
            ProviderNames.EdgeTts => new EdgeTtsProvider(_log),
            ProviderNames.ElevenLabs => new ElevenLabsTtsProvider(
                _log, keyStore?.GetKey(CredentialKeys.ElevenLabs) ?? string.Empty, audioProcessingService: _audioProcessingService),
            ProviderNames.GoogleCloudTts => new GoogleCloudTtsProvider(
                _log, keyStore?.GetKey(CredentialKeys.GoogleAi) ?? string.Empty),
            ProviderNames.OpenAiTts => new OpenAiTtsProvider(
                _log, keyStore?.GetKey(CredentialKeys.OpenAi) ?? string.Empty, audioProcessingService: _audioProcessingService),
            _ => throw new PipelineProviderException(
                $"TTS provider '{providerId}' is not implemented. " +
                "Select an implemented provider in Settings.")
        };
    }

    private static ComputeProfile ResolveProfile(
        string providerId,
        AppSettings settings,
        ComputeProfile? profile)
    {
        if (profile.HasValue)
            return profile.Value;

        if (string.Equals(settings.TtsProvider, providerId, StringComparison.Ordinal))
            return settings.TtsProfile;

        return InferenceRuntimeCatalog.InferTtsProfile(providerId);
    }

    private static string ResolveProviderId(string providerId, AppSettings settings, ComputeProfile profile)
    {
        if (!InferenceRuntimeCatalog.IsKnownTtsProvider(providerId)
            && !string.Equals(providerId, ProviderNames.ContainerizedService, StringComparison.Ordinal))
        {
            return providerId;
        }

        if (string.Equals(settings.TtsProvider, providerId, StringComparison.Ordinal))
            return InferenceRuntimeCatalog.NormalizeTtsProvider(profile, settings.TtsProvider);

        return InferenceRuntimeCatalog.NormalizeTtsProvider(profile, providerId);
    }
}
