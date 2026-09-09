using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Settings;
using Xunit;

namespace BabelPlayer.Tests;

/// <summary>
/// Unit tests for <see cref="InferenceRuntimeCatalog"/> — pure routing/normalization functions.
/// </summary>
public sealed class InferenceRuntimeCatalogTests
{
    // ── MapLegacyRuntimeToProfile ─────────────────────────────────────────────

    [Fact]
    public void MapLegacyRuntimeToProfile_Containerized_ReturnsGpu()
    {
        Assert.Equal(ComputeProfile.Gpu, InferenceRuntimeCatalog.MapLegacyRuntimeToProfile(InferenceRuntime.Containerized));
    }

    [Fact]
    public void MapLegacyRuntimeToProfile_Cloud_ReturnsCloud()
    {
        Assert.Equal(ComputeProfile.Cloud, InferenceRuntimeCatalog.MapLegacyRuntimeToProfile(InferenceRuntime.Cloud));
    }

    [Fact]
    public void MapLegacyRuntimeToProfile_Local_ReturnsCpu()
    {
        Assert.Equal(ComputeProfile.Cpu, InferenceRuntimeCatalog.MapLegacyRuntimeToProfile(InferenceRuntime.Local));
    }

    // ── ResolveRuntime ────────────────────────────────────────────────────────

    [Fact]
    public void ResolveRuntime_Gpu_ReturnsContainerized()
    {
        Assert.Equal(InferenceRuntime.Containerized, InferenceRuntimeCatalog.ResolveRuntime(ComputeProfile.Gpu));
    }

    [Fact]
    public void ResolveRuntime_Cloud_ReturnsCloud()
    {
        Assert.Equal(InferenceRuntime.Cloud, InferenceRuntimeCatalog.ResolveRuntime(ComputeProfile.Cloud));
    }

    [Fact]
    public void ResolveRuntime_Cpu_ReturnsLocal()
    {
        Assert.Equal(InferenceRuntime.Local, InferenceRuntimeCatalog.ResolveRuntime(ComputeProfile.Cpu));
    }

    // ── InferTranscriptionProfile ─────────────────────────────────────────────

    [Theory]
    [InlineData(ProviderNames.OpenAiWhisperApi, ComputeProfile.Cloud)]
    [InlineData(ProviderNames.GoogleStt, ComputeProfile.Cloud)]
    [InlineData(ProviderNames.GeminiTranscription, ComputeProfile.Cloud)]
    public void InferTranscriptionProfile_CloudProviders_ReturnCloud(string providerId, ComputeProfile expected)
    {
        Assert.Equal(expected, InferenceRuntimeCatalog.InferTranscriptionProfile(providerId));
    }

    [Fact]
    public void InferTranscriptionProfile_FasterWhisper_ReturnsCpu()
    {
        Assert.Equal(ComputeProfile.Cpu, InferenceRuntimeCatalog.InferTranscriptionProfile(ProviderNames.FasterWhisper));
    }

    [Fact]
    public void InferTranscriptionProfile_Null_ReturnsCpu()
    {
        Assert.Equal(ComputeProfile.Cpu, InferenceRuntimeCatalog.InferTranscriptionProfile(null));
    }

    // ── InferTranslationProfile ───────────────────────────────────────────────

    [Theory]
    [InlineData(ProviderNames.Deepl, ComputeProfile.Cloud)]
    [InlineData(ProviderNames.OpenAi, ComputeProfile.Cloud)]
    [InlineData(ProviderNames.GeminiTranslation, ComputeProfile.Cloud)]
    public void InferTranslationProfile_CloudProviders_ReturnCloud(string providerId, ComputeProfile expected)
    {
        Assert.Equal(expected, InferenceRuntimeCatalog.InferTranslationProfile(providerId));
    }

    [Theory]
    [InlineData(ProviderNames.Nllb200)]
    [InlineData(ProviderNames.CTranslate2)]
    public void InferTranslationProfile_CpuProviders_ReturnCpu(string providerId)
    {
        Assert.Equal(ComputeProfile.Cpu, InferenceRuntimeCatalog.InferTranslationProfile(providerId));
    }

    // ── InferTtsProfile ───────────────────────────────────────────────────────

    [Fact]
    public void InferTtsProfile_Piper_ReturnsCpu()
    {
        Assert.Equal(ComputeProfile.Cpu, InferenceRuntimeCatalog.InferTtsProfile(ProviderNames.Piper));
    }

    [Fact]
    public void InferTtsProfile_Qwen_ReturnsGpu()
    {
        Assert.Equal(ComputeProfile.Gpu, InferenceRuntimeCatalog.InferTtsProfile(ProviderNames.Qwen));
    }

    [Theory]
    [InlineData(ProviderNames.EdgeTts)]
    [InlineData(ProviderNames.ElevenLabs)]
    [InlineData(ProviderNames.GoogleCloudTts)]
    [InlineData(ProviderNames.OpenAiTts)]
    public void InferTtsProfile_CloudProviders_ReturnCloud(string providerId)
    {
        Assert.Equal(ComputeProfile.Cloud, InferenceRuntimeCatalog.InferTtsProfile(providerId));
    }

    // ── DefaultTranscriptionProvider ─────────────────────────────────────────

    [Fact]
    public void DefaultTranscriptionProvider_Cloud_ReturnsOpenAiWhisperApi()
    {
        Assert.Equal(ProviderNames.OpenAiWhisperApi, InferenceRuntimeCatalog.DefaultTranscriptionProvider(ComputeProfile.Cloud));
    }

    [Theory]
    [InlineData(ComputeProfile.Cpu)]
    [InlineData(ComputeProfile.Gpu)]
    public void DefaultTranscriptionProvider_NonCloud_ReturnsFasterWhisper(ComputeProfile profile)
    {
        Assert.Equal(ProviderNames.FasterWhisper, InferenceRuntimeCatalog.DefaultTranscriptionProvider(profile));
    }

    // ── DefaultTranslationProvider ────────────────────────────────────────────

    [Theory]
    [InlineData(ComputeProfile.Cpu)]
    [InlineData(ComputeProfile.Gpu)]
    public void DefaultTranslationProvider_CpuOrGpu_ReturnsNllb200(ComputeProfile profile)
    {
        Assert.Equal(ProviderNames.Nllb200, InferenceRuntimeCatalog.DefaultTranslationProvider(profile));
    }

    [Fact]
    public void DefaultTranslationProvider_Cloud_ReturnsDeepl()
    {
        Assert.Equal(ProviderNames.Deepl, InferenceRuntimeCatalog.DefaultTranslationProvider(ComputeProfile.Cloud));
    }

    // ── DefaultTtsProvider ────────────────────────────────────────────────────

    [Fact]
    public void DefaultTtsProvider_Cpu_ReturnsPiper()
    {
        Assert.Equal(ProviderNames.Piper, InferenceRuntimeCatalog.DefaultTtsProvider(ComputeProfile.Cpu));
    }

    [Fact]
    public void DefaultTtsProvider_Gpu_ReturnsQwen()
    {
        Assert.Equal(ProviderNames.Qwen, InferenceRuntimeCatalog.DefaultTtsProvider(ComputeProfile.Gpu));
    }

    [Fact]
    public void DefaultTtsProvider_Cloud_ReturnsEdgeTts()
    {
        Assert.Equal(ProviderNames.EdgeTts, InferenceRuntimeCatalog.DefaultTtsProvider(ComputeProfile.Cloud));
    }

    [Fact]
    public void DefaultDiarizationProvider_ReturnsWeSpeakerLocal()
    {
        Assert.Equal(ProviderNames.WeSpeakerLocal, InferenceRuntimeCatalog.DefaultDiarizationProvider());
    }

    // ── NormalizeTranscriptionProvider ───────────────────────────────────────

    [Fact]
    public void NormalizeTranscriptionProvider_NullProvider_ReturnsCpuDefault()
    {
        var result = InferenceRuntimeCatalog.NormalizeTranscriptionProvider(ComputeProfile.Cpu, null);
        Assert.Equal(ProviderNames.FasterWhisper, result);
    }

    [Fact]
    public void NormalizeTranscriptionProvider_ContainerizedServiceId_ReturnsCpuDefault()
    {
        var result = InferenceRuntimeCatalog.NormalizeTranscriptionProvider(ComputeProfile.Cpu, ProviderNames.ContainerizedService);
        Assert.Equal(ProviderNames.FasterWhisper, result);
    }

    [Fact]
    public void NormalizeTranscriptionProvider_CpuProfile_AlwaysReturnsFasterWhisper()
    {
        var result = InferenceRuntimeCatalog.NormalizeTranscriptionProvider(ComputeProfile.Cpu, ProviderNames.OpenAiWhisperApi);
        Assert.Equal(ProviderNames.FasterWhisper, result);
    }

    [Fact]
    public void NormalizeTranscriptionProvider_CloudProfile_PreservesGoogleStt()
    {
        var result = InferenceRuntimeCatalog.NormalizeTranscriptionProvider(ComputeProfile.Cloud, ProviderNames.GoogleStt);
        Assert.Equal(ProviderNames.GoogleStt, result);
    }

    [Fact]
    public void NormalizeTranscriptionProvider_CloudProfile_PreservesGeminiTranscription()
    {
        var result = InferenceRuntimeCatalog.NormalizeTranscriptionProvider(ComputeProfile.Cloud, ProviderNames.GeminiTranscription);
        Assert.Equal(ProviderNames.GeminiTranscription, result);
    }

    [Fact]
    public void NormalizeTranscriptionProvider_CloudProfile_FasterWhisperMapsToDefault()
    {
        var result = InferenceRuntimeCatalog.NormalizeTranscriptionProvider(ComputeProfile.Cloud, ProviderNames.FasterWhisper);
        Assert.Equal(ProviderNames.OpenAiWhisperApi, result);
    }

    [Fact]
    public void NormalizeTranscriptionProvider_UnknownProvider_IsPassedThrough()
    {
        var result = InferenceRuntimeCatalog.NormalizeTranscriptionProvider(ComputeProfile.Cpu, "my-custom-provider");
        Assert.Equal("my-custom-provider", result);
    }

    // ── NormalizeTranslationProvider ──────────────────────────────────────────

    [Fact]
    public void NormalizeTranslationProvider_NullProvider_ReturnsDeepl()
    {
        var result = InferenceRuntimeCatalog.NormalizeTranslationProvider(ComputeProfile.Cloud, null);
        Assert.Equal(ProviderNames.Deepl, result);
    }

    [Fact]
    public void NormalizeTranslationProvider_CpuProfile_DeeplMapsToNllb200()
    {
        var result = InferenceRuntimeCatalog.NormalizeTranslationProvider(ComputeProfile.Cpu, ProviderNames.Deepl);
        Assert.Equal(ProviderNames.Nllb200, result);
    }

    [Fact]
    public void NormalizeTranslationProvider_CpuProfile_PreservesCTranslate2()
    {
        var result = InferenceRuntimeCatalog.NormalizeTranslationProvider(ComputeProfile.Cpu, ProviderNames.CTranslate2);
        Assert.Equal(ProviderNames.CTranslate2, result);
    }

    [Fact]
    public void NormalizeTranslationProvider_CloudProfile_PreservesDeepL()
    {
        var result = InferenceRuntimeCatalog.NormalizeTranslationProvider(ComputeProfile.Cloud, ProviderNames.Deepl);
        Assert.Equal(ProviderNames.Deepl, result);
    }

    [Fact]
    public void NormalizeTranslationProvider_GpuProfile_ReturnsNllb200()
    {
        var result = InferenceRuntimeCatalog.NormalizeTranslationProvider(ComputeProfile.Gpu, ProviderNames.Deepl);
        Assert.Equal(ProviderNames.Nllb200, result);
    }

    [Fact]
    public void NormalizeTranslationProvider_ContainerizedServiceId_ReturnsDefault()
    {
        var result = InferenceRuntimeCatalog.NormalizeTranslationProvider(ComputeProfile.Cpu, ProviderNames.ContainerizedService);
        Assert.Equal(ProviderNames.Nllb200, result);
    }

    // ── NormalizeTtsProvider ──────────────────────────────────────────────────

    [Fact]
    public void NormalizeTtsProvider_NullProvider_ReturnsCpuDefault()
    {
        var result = InferenceRuntimeCatalog.NormalizeTtsProvider(ComputeProfile.Cpu, null);
        Assert.Equal(ProviderNames.Piper, result);
    }

    [Fact]
    public void NormalizeTtsProvider_CpuProfile_AlwaysReturnsPiper()
    {
        var result = InferenceRuntimeCatalog.NormalizeTtsProvider(ComputeProfile.Cpu, ProviderNames.EdgeTts);
        Assert.Equal(ProviderNames.Piper, result);
    }

    [Fact]
    public void NormalizeTtsProvider_GpuProfile_ElevenLabsMapsToQwen()
    {
        var result = InferenceRuntimeCatalog.NormalizeTtsProvider(ComputeProfile.Gpu, ProviderNames.ElevenLabs);
        Assert.Equal(ProviderNames.Qwen, result);
    }

    [Fact]
    public void NormalizeTtsProvider_CloudProfile_PreservesElevenLabs()
    {
        var result = InferenceRuntimeCatalog.NormalizeTtsProvider(ComputeProfile.Cloud, ProviderNames.ElevenLabs);
        Assert.Equal(ProviderNames.ElevenLabs, result);
    }

    [Fact]
    public void NormalizeTtsProvider_CloudProfile_PreservesGoogleCloudTts()
    {
        var result = InferenceRuntimeCatalog.NormalizeTtsProvider(ComputeProfile.Cloud, ProviderNames.GoogleCloudTts);
        Assert.Equal(ProviderNames.GoogleCloudTts, result);
    }

    [Fact]
    public void NormalizeTtsProvider_CloudProfile_CpuOnlyProviderMapsToDefault()
    {
        var result = InferenceRuntimeCatalog.NormalizeTtsProvider(ComputeProfile.Cloud, ProviderNames.Piper);
        Assert.Equal(ProviderNames.EdgeTts, result);
    }

    [Fact]
    public void NormalizeTtsProvider_ContainerizedServiceId_ReturnsDefault()
    {
        var result = InferenceRuntimeCatalog.NormalizeTtsProvider(ComputeProfile.Gpu, ProviderNames.ContainerizedService);
        Assert.Equal(ProviderNames.Qwen, result);
    }

    [Fact]
    public void NormalizeDiarizationProvider_NemoAlias_MigratesToWeSpeakerLocal()
    {
        var result = InferenceRuntimeCatalog.NormalizeDiarizationProvider(ProviderNames.NemoDiarizationAlias);
        Assert.Equal(ProviderNames.WeSpeakerLocal, result);
    }

    [Fact]
    public void NormalizeDiarizationProvider_WeSpeakerAlias_ReturnsWeSpeakerLocal()
    {
        var result = InferenceRuntimeCatalog.NormalizeDiarizationProvider(ProviderNames.WeSpeakerDiarizationAlias);
        Assert.Equal(ProviderNames.WeSpeakerLocal, result);
    }

    [Fact]
    public void NormalizeDiarizationProvider_SortFormerLocal_PreservesKnownId()
    {
        var result = InferenceRuntimeCatalog.NormalizeDiarizationProvider(ProviderNames.SortFormerLocal);
        Assert.Equal(ProviderNames.SortFormerLocal, result);
    }

    [Fact]
    public void NormalizeDiarizationProvider_SortFormerAlias_ReturnsSortFormerLocal()
    {
        var result = InferenceRuntimeCatalog.NormalizeDiarizationProvider(ProviderNames.SortFormerDiarizationAlias);
        Assert.Equal(ProviderNames.SortFormerLocal, result);
    }

    [Fact]
    public void NormalizeDiarizationProvider_UnknownId_FallsBackToWeSpeakerLocal()
    {
        var result = InferenceRuntimeCatalog.NormalizeDiarizationProvider("not-a-real-diarizer");
        Assert.Equal(ProviderNames.WeSpeakerLocal, result);
    }

    [Fact]
    public void IsKnownDiarizationProvider_SortFormerLocal_ReturnsTrue()
    {
        Assert.True(InferenceRuntimeCatalog.IsKnownDiarizationProvider(ProviderNames.SortFormerLocal));
    }

    [Fact]
    public void InferDiarizationRuntime_SortFormerLocal_ReturnsLocal()
    {
        Assert.Equal(
            InferenceRuntime.Local,
            InferenceRuntimeCatalog.InferDiarizationRuntime(ProviderNames.SortFormerLocal));
    }

    [Fact]
    public void NormalizeDiarizationProvider_Null_ReturnsDefaultProvider()
    {
        var result = InferenceRuntimeCatalog.NormalizeDiarizationProvider(null);
        Assert.Equal(ProviderNames.WeSpeakerLocal, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void NormalizeDiarizationProvider_Whitespace_PreservesDisabledState(string providerId)
    {
        var result = InferenceRuntimeCatalog.NormalizeDiarizationProvider(providerId);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void NormalizeDiarizationCapabilityProviderId_NemoAlias_ReturnsNemoLocal()
    {
        var result = InferenceRuntimeCatalog.NormalizeDiarizationCapabilityProviderId(ProviderNames.NemoDiarizationAlias);
        Assert.Equal(ProviderNames.NemoLocal, result);
    }

    [Fact]
    public void NormalizeDiarizationCapabilityProviderId_WeSpeakerAlias_ReturnsWeSpeakerLocal()
    {
        var result = InferenceRuntimeCatalog.NormalizeDiarizationCapabilityProviderId(ProviderNames.WeSpeakerDiarizationAlias);
        Assert.Equal(ProviderNames.WeSpeakerLocal, result);
    }

    // ── IsKnownTranscriptionProvider ──────────────────────────────────────────

    [Theory]
    [InlineData(ProviderNames.FasterWhisper)]
    [InlineData(ProviderNames.OpenAiWhisperApi)]
    [InlineData(ProviderNames.GoogleStt)]
    [InlineData(ProviderNames.GeminiTranscription)]
    public void IsKnownTranscriptionProvider_KnownProviders_ReturnTrue(string providerId)
    {
        Assert.True(InferenceRuntimeCatalog.IsKnownTranscriptionProvider(providerId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown-provider")]
    [InlineData(ProviderNames.EdgeTts)]
    public void IsKnownTranscriptionProvider_UnknownProviders_ReturnFalse(string? providerId)
    {
        Assert.False(InferenceRuntimeCatalog.IsKnownTranscriptionProvider(providerId));
    }

    // ── IsKnownTranslationProvider ────────────────────────────────────────────

    [Theory]
    [InlineData(ProviderNames.Nllb200)]
    [InlineData(ProviderNames.CTranslate2)]
    [InlineData(ProviderNames.Deepl)]
    [InlineData(ProviderNames.OpenAi)]
    [InlineData(ProviderNames.GeminiTranslation)]
    public void IsKnownTranslationProvider_KnownProviders_ReturnTrue(string providerId)
    {
        Assert.True(InferenceRuntimeCatalog.IsKnownTranslationProvider(providerId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("some-other-provider")]
    [InlineData(ProviderNames.Piper)]
    public void IsKnownTranslationProvider_UnknownProviders_ReturnFalse(string? providerId)
    {
        Assert.False(InferenceRuntimeCatalog.IsKnownTranslationProvider(providerId));
    }

    // ── IsKnownTtsProvider ────────────────────────────────────────────────────

    [Theory]
    [InlineData(ProviderNames.Piper)]
    [InlineData(ProviderNames.EdgeTts)]
    [InlineData(ProviderNames.ElevenLabs)]
    [InlineData(ProviderNames.GoogleCloudTts)]
    [InlineData(ProviderNames.OpenAiTts)]

    [InlineData(ProviderNames.Qwen)]
    public void IsKnownTtsProvider_KnownProviders_ReturnTrue(string providerId)
    {
        Assert.True(InferenceRuntimeCatalog.IsKnownTtsProvider(providerId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown-tts")]
    [InlineData(ProviderNames.FasterWhisper)]
    public void IsKnownTtsProvider_UnknownProviders_ReturnFalse(string? providerId)
    {
        Assert.False(InferenceRuntimeCatalog.IsKnownTtsProvider(providerId));
    }

    [Theory]
    [InlineData(ProviderNames.NemoLocal)]
    [InlineData(ProviderNames.WeSpeakerLocal)]
    public void IsKnownDiarizationProvider_KnownProviders_ReturnTrue(string providerId)
    {
        Assert.True(InferenceRuntimeCatalog.IsKnownDiarizationProvider(providerId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown-diarizer")]
    [InlineData(ProviderNames.Piper)]
    public void IsKnownDiarizationProvider_UnknownProviders_ReturnFalse(string? providerId)
    {
        Assert.False(InferenceRuntimeCatalog.IsKnownDiarizationProvider(providerId));
    }

    // ── NormalizeSettings ─────────────────────────────────────────────────────

    [Fact]
    public void NormalizeSettings_NullSettings_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => InferenceRuntimeCatalog.NormalizeSettings(null!));
    }

    [Fact]
    public void NormalizeSettings_CpuProfileWithCloudTranscriptionProvider_NormalizesToFasterWhisper()
    {
        var settings = new AppSettings
        {
            TranscriptionProfile = ComputeProfile.Cpu,
            TranscriptionProvider = ProviderNames.OpenAiWhisperApi,
        };
        InferenceRuntimeCatalog.NormalizeSettings(settings);
        Assert.Equal(ProviderNames.FasterWhisper, settings.TranscriptionProvider);
    }

    [Fact]
    public void NormalizeSettings_DefaultSettings_AreStable()
    {
        var settings = new AppSettings();
        var originalTranscription = settings.TranscriptionProvider;
        var originalTranslation = settings.TranslationProvider;
        var originalTts = settings.TtsProvider;
        var originalDiarization = settings.DiarizationProvider;

        InferenceRuntimeCatalog.NormalizeSettings(settings);

        Assert.Equal(originalTranscription, settings.TranscriptionProvider);
        Assert.Equal(originalTranslation, settings.TranslationProvider);
        Assert.Equal(originalTts, settings.TtsProvider);
        Assert.Equal(originalDiarization, settings.DiarizationProvider);
    }

    [Fact]
    public void NormalizeSettings_LegacyNemoDiarization_MigratesToWeSpeaker()
    {
        var settings = new AppSettings { DiarizationProvider = ProviderNames.NemoLocal };
        InferenceRuntimeCatalog.NormalizeSettings(settings);
        Assert.Equal(ProviderNames.WeSpeakerLocal, settings.DiarizationProvider);
    }

    // ── Infer*Runtime convenience wrappers ────────────────────────────────────

    [Fact]
    public void InferTranscriptionRuntime_CloudProvider_ReturnsCloud()
    {
        Assert.Equal(InferenceRuntime.Cloud, InferenceRuntimeCatalog.InferTranscriptionRuntime(ProviderNames.OpenAiWhisperApi));
    }

    [Fact]
    public void InferTranslationRuntime_NllbProvider_ReturnsLocal()
    {
        Assert.Equal(InferenceRuntime.Local, InferenceRuntimeCatalog.InferTranslationRuntime(ProviderNames.Nllb200));
    }

    [Fact]
    public void InferTtsRuntime_Qwen_ReturnsContainerized()
    {
        Assert.Equal(InferenceRuntime.Containerized, InferenceRuntimeCatalog.InferTtsRuntime(ProviderNames.Qwen));
    }
}
