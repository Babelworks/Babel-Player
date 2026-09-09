using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Settings;
using Babel.Player.Services.SortFormer;

namespace Babel.Player.Services.Registries;

public interface IDiarizationProvider
{
    ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore);
    Task<bool> EnsureReadyAsync(AppSettings settings, IProgress<double>? progress = null, CancellationToken ct = default);
    Task<DiarizationResult> DiarizeAsync(DiarizationRequest request, CancellationToken ct = default);
}

public interface IDiarizationRegistry
{
    IReadOnlyList<ProviderDescriptor> GetAvailableProviders();
    IDiarizationProvider CreateProvider(string providerId, AppSettings settings, ApiKeyStore? keyStore = null);
    ProviderReadiness CheckReadiness(string providerId, AppSettings settings, ApiKeyStore? keyStore);
}

public sealed class DiarizationRegistry : IDiarizationRegistry
{
    private readonly AppLog _log;
    private readonly ContainerizedServiceProbe? _containerizedProbe;
    private readonly ContainerizedRequestLeaseTracker? _requestLeaseTracker;
    private readonly ConcurrentDictionary<string, ContainerizedInferenceClient> _clientCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a DiarizationRegistry with the required application log and optional components for containerized inference support.
    /// </summary>
    /// <param name="log">Application logger used for registry and provider operations.</param>
    /// <param name="containerizedProbe">Optional probe to check containerized inference service health and availability; may be <c>null</c>.</param>
    /// <param name="requestLeaseTracker">Optional tracker for managing request leases when creating or using containerized inference clients; may be <c>null</c>.</param>
    public DiarizationRegistry(
        AppLog log,
        ContainerizedServiceProbe? containerizedProbe = null,
        ContainerizedRequestLeaseTracker? requestLeaseTracker = null)
    {
        _log = log;
        _containerizedProbe = containerizedProbe;
        _requestLeaseTracker = requestLeaseTracker;
    }

    /// <summary>
    /// Lists the diarization providers supported by this registry.
    /// </summary>
    /// <returns>A read-only list of <see cref="ProviderDescriptor"/> instances describing each available diarization provider.</returns>
    public IReadOnlyList<ProviderDescriptor> GetAvailableProviders() =>
    [
        new ProviderDescriptor(
            ProviderNames.NemoLocal,
            "NeMo",
            false,
            null,
            [ProviderNames.NemoDiarizationAlias],
            SupportedRuntimes: [InferenceRuntime.Containerized],
            DefaultRuntime: InferenceRuntime.Containerized,
            IsImplemented: true,
            Notes: "Uses the containerized NeMo ClusteringDiarizer endpoint."),
        new ProviderDescriptor(
            ProviderNames.WeSpeakerLocal,
            "WeSpeaker",
            false,
            null,
            [ProviderNames.WeSpeakerDiarizationAlias],
            SupportedRuntimes: [InferenceRuntime.Local],
            DefaultRuntime: InferenceRuntime.Local,
            IsImplemented: true,
            Notes: "Uses the managed CPU WeSpeaker provider."),
        new ProviderDescriptor(
            ProviderNames.SortFormerLocal,
            "SortFormer",
            false,
            null,
            [SortFormerModelCatalog.ModelId],
            SupportedRuntimes: [InferenceRuntime.Local],
            DefaultRuntime: InferenceRuntime.Local,
            IsImplemented: true,
            Notes: "CPU ONNX SortFormer diarization (max 4 speakers)."),
    ];

    /// <summary>
    /// Determines whether the diarization provider identified by <paramref name="providerId"/> is available, implemented, and ready to use.
    /// </summary>
    /// <param name="providerId">The identifier of the diarization provider to check.</param>
    /// <param name="settings">Application settings that influence provider configuration.</param>
    /// <param name="keyStore">Optional API key store used by some providers; may be null.</param>
    /// <returns>
    /// A <see cref="ProviderReadiness"/> describing readiness. The result will indicate failure with a diagnostic message if the provider is unknown or not implemented; otherwise it reflects the provider's own readiness status.
    /// </returns>
    public ProviderReadiness CheckReadiness(string providerId, AppSettings settings, ApiKeyStore? keyStore)
    {
        var desc = GetAvailableProviders().FirstOrDefault(p => p.Id == providerId);
        if (desc == null)
            return new ProviderReadiness(false, $"Unknown diarization provider '{providerId}'.");
        if (!desc.IsImplemented)
            return new ProviderReadiness(false, $"Provider '{desc.DisplayName}' is not implemented yet.");

        var provider = CreateProvider(providerId, settings, keyStore);
        return provider.CheckReadiness(settings, keyStore);
    }

    /// <summary>
    /// Creates a diarization provider instance for the specified provider ID.
    /// </summary>
    /// <param name="providerId">The identifier of the diarization provider to create.</param>
    /// <param name="settings">Application settings used when constructing provider instances (e.g., service endpoints).</param>
    /// <param name="keyStore">Optional API key store; may be used by some providers to obtain credentials.</param>
    /// <returns>An <see cref="IDiarizationProvider"/> implementation corresponding to <paramref name="providerId"/>.</returns>
    /// <exception cref="PipelineProviderException">Thrown when the specified providerId is not implemented.</exception>
    public IDiarizationProvider CreateProvider(string providerId, AppSettings settings, ApiKeyStore? keyStore = null)
    {
        return providerId switch
        {
            ProviderNames.NemoLocal => new NemoContainerizedDiarizationProvider(
                GetOrCreateContainerizedClient(settings),
                _log,
                _containerizedProbe),
            ProviderNames.WeSpeakerLocal => new WeSpeakerCpuDiarizationProvider(_log),
            ProviderNames.SortFormerLocal => new SortFormerDiarizationProvider(
                _log,
                ModelDownloader.ResolveSortFormerModelDir(settings.SortFormerModelDir)),
            _ => throw new PipelineProviderException(
                $"Diarization provider '{providerId}' is not implemented. " +
                "Select an implemented provider in Settings.")
        };
    }

    /// <summary>
    /// Get or create a cached ContainerizedInferenceClient for the effective containerized service URL in the provided settings.
    /// </summary>
    /// <param name="settings">Application settings used to determine the effective containerized service URL.</param>
    /// <returns>The ContainerizedInferenceClient associated with the normalized service URL; a previously created instance is returned when available.</returns>
    private ContainerizedInferenceClient GetOrCreateContainerizedClient(AppSettings settings)
    {
        var serviceUrl = ContainerizedInferenceClient.NormalizeBaseUrl(settings.EffectiveContainerizedServiceUrl);
        return _clientCache.GetOrAdd(
            serviceUrl,
            normalizedServiceUrl => new ContainerizedInferenceClient(normalizedServiceUrl, _log, null, _requestLeaseTracker));
    }
}
