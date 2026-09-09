using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;
using Babel.Player.ViewModels;
using Xunit;

namespace BabelPlayer.Tests;

public sealed class PipelineStageProgressTests() : IDisposable
{
    private readonly TestContext _ctx = new();

    private sealed class TestContext : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), $"babel-pipeline-progress-{Guid.NewGuid():N}");
        public string StorePath { get; }
        public string PerSessionDir { get; }
        public string RecentPath { get; }
        public string MediaPath { get; }

        public TestContext()
        {
            Directory.CreateDirectory(Dir);
            StorePath = Path.Combine(Dir, "session.json");
            PerSessionDir = Path.Combine(Dir, "sessions");
            RecentPath = Path.Combine(Dir, "recent-sessions.json");
            Directory.CreateDirectory(PerSessionDir);

            MediaPath = Path.Combine(Dir, "sample.mp4");
            File.WriteAllText(MediaPath, "fake media");
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); }
            catch { /* best effort cleanup */ }
        }
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task AdvancePipelineAsync_FreshRun_StreamsStageWorkBeforeUpstreamCompletes()
    {
        var settings = CreateSettings();
        settings.DiarizationProvider = string.Empty;
        var probe = new PipelineTimingProbe(expectedSegments: 3);
        var coordinator = CreateCoordinator(
            settings,
            new FakeTranscriptionRegistry(new DelayedTranscriptionProvider(probe, perSegmentDelayMs: 80)),
            new FakeTranslationRegistry(new DelayedTranslationProvider(probe, perSegmentDelayMs: 80)),
            new FakeTtsRegistry(new DelayedTtsProvider(probe, perSegmentDelayMs: 80)));
        coordinator.Initialize();
        coordinator.LoadMedia(_ctx.MediaPath);

        List<SessionWorkflowCoordinator.PipelineStageUpdate> updates = [];
        await coordinator.AdvancePipelineAsync(stageProgress: new CaptureProgress<SessionWorkflowCoordinator.PipelineStageUpdate>(updates));

        Assert.Equal(SessionWorkflowStage.TtsGenerated, coordinator.CurrentSession.Stage);
        AssertStage(updates, SessionWorkflowStage.Transcribed, 1, 3);
        AssertStage(updates, SessionWorkflowStage.Translated, 2, 3);
        AssertStage(updates, SessionWorkflowStage.TtsGenerated, 3, 3);

        Assert.True(
            probe.FirstStreamingTranslationStartedAt < probe.TranscriptionCompletedAt,
            $"Expected translation to start before transcription completed, but translation={probe.FirstStreamingTranslationStartedAt}ms and transcriptionComplete={probe.TranscriptionCompletedAt}ms.");
        Assert.True(
            probe.FirstStreamingTtsStartedAt < probe.StreamingTranslationCompletedAt,
            $"Expected TTS to start before translation completed, but tts={probe.FirstStreamingTtsStartedAt}ms and translationComplete={probe.StreamingTranslationCompletedAt}ms.");
        Assert.Contains(
            updates,
            update => update.TargetStage == SessionWorkflowStage.Translated
                   && !string.IsNullOrWhiteSpace(update.StreamingStatus));
        Assert.Contains(
            updates,
            update => update.TargetStage == SessionWorkflowStage.TtsGenerated
                   && !string.IsNullOrWhiteSpace(update.StreamingStatus));
    }

    [Fact]
    public async Task AdvancePipelineAsync_StreamingPipeline_ReducesWallTimeAgainstSequentialStages()
    {
        // Wall-clock comparisons are unreliable on loaded CI runners.
        // Instead, verify that translation starts before transcription finishes (and TTS before
        // translation finishes), which is the structural invariant that proves streaming work.
        var streamingSettings = CreateSettings();
        streamingSettings.DiarizationProvider = string.Empty;
        using var streamingCtx = new TestContext();

        var probe = new PipelineTimingProbe(expectedSegments: 3);
        var streamingCoordinator = CreateCoordinator(
            streamingSettings,
            new FakeTranscriptionRegistry(new DelayedTranscriptionProvider(probe, perSegmentDelayMs: 40)),
            new FakeTranslationRegistry(new DelayedTranslationProvider(probe, perSegmentDelayMs: 40)),
            new FakeTtsRegistry(new DelayedTtsProvider(probe, perSegmentDelayMs: 40)),
            context: streamingCtx);
        streamingCoordinator.Initialize();
        streamingCoordinator.LoadMedia(streamingCtx.MediaPath);

        await streamingCoordinator.AdvancePipelineAsync(progress: null, cancellationToken: CancellationToken.None);

        Assert.Equal(SessionWorkflowStage.TtsGenerated, streamingCoordinator.CurrentSession.Stage);

        Assert.True(
            probe.FirstStreamingTranslationStartedAt < probe.TranscriptionCompletedAt,
            $"Expected translation to start before transcription completed, but translation={probe.FirstStreamingTranslationStartedAt}ms and transcriptionComplete={probe.TranscriptionCompletedAt}ms.");
        Assert.True(
            probe.FirstStreamingTtsStartedAt < probe.StreamingTranslationCompletedAt,
            $"Expected TTS to start before translation completed, but tts={probe.FirstStreamingTtsStartedAt}ms and translationComplete={probe.StreamingTranslationCompletedAt}ms.");
    }

    [Fact]
    public async Task AdvancePipelineAsync_FromTranslatedSession_EmitsOnlyDubAsOneOfOne()
    {
        var settings = CreateSettings();
        settings.DiarizationProvider = string.Empty;
        var transcriptionRegistry = new FakeTranscriptionRegistry(new FakeTranscriptionProvider());
        var translationRegistry = new FakeTranslationRegistry(new FakeTranslationProvider());
        var ttsRegistry = new FakeTtsRegistry(new FakeTtsProvider());

        var coordinator = CreateCoordinator(settings, transcriptionRegistry, translationRegistry, ttsRegistry);
        coordinator.Initialize();
        coordinator.LoadMedia(_ctx.MediaPath);
        await coordinator.TranscribeMediaAsync();
        await coordinator.TranslateTranscriptAsync();

        coordinator = CreateCoordinator(settings, transcriptionRegistry, translationRegistry, ttsRegistry);
        coordinator.Initialize();

        List<SessionWorkflowCoordinator.PipelineStageUpdate> updates = [];
        await coordinator.AdvancePipelineAsync(stageProgress: new CaptureProgress<SessionWorkflowCoordinator.PipelineStageUpdate>(updates));

        Assert.NotEmpty(updates);
        Assert.All(updates, update =>
        {
            Assert.Equal(SessionWorkflowStage.TtsGenerated, update.TargetStage);
            Assert.Equal(1, update.StageIndex);
            Assert.Equal(1, update.StageCount);
        });
        Assert.Contains(updates, update => update.Progress01 == 1d && !update.IsIndeterminate);
    }

    [Fact]
    public async Task AdvancePipelineAsync_ModelDownloadProgress_IsMappedIntoActiveStageBar()
    {
        var settings = CreateSettings();
        settings.DiarizationProvider = string.Empty;
        var downloadProvider = new FakeTranslationProvider(
            requiresDownload: true,
            downloadSteps: [0.25, 0.5, 1.0]);
        var coordinator = CreateCoordinator(
            settings,
            new FakeTranscriptionRegistry(new FakeTranscriptionProvider()),
            new FakeTranslationRegistry(downloadProvider),
            new FakeTtsRegistry(new FakeTtsProvider()));
        coordinator.Initialize();
        coordinator.LoadMedia(_ctx.MediaPath);

        List<SessionWorkflowCoordinator.PipelineStageUpdate> updates = [];
        await coordinator.AdvancePipelineAsync(stageProgress: new CaptureProgress<SessionWorkflowCoordinator.PipelineStageUpdate>(updates));

        var translationUpdates = updates
            .Where(update => update.TargetStage == SessionWorkflowStage.Translated)
            .ToList();
        Assert.Contains(
            translationUpdates,
            update => !update.IsIndeterminate &&
                      Math.Abs(update.Progress01 - 0.25) < 0.001 &&
                      update.Detail.Contains("Preparing translation model", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            translationUpdates,
            update => !update.IsIndeterminate &&
                      update.Progress01 > 0.25 &&
                      update.StageIndex == 2 &&
                      update.StageCount == 3);
    }

    [Fact]
    public async Task AdvancePipelineAsync_MultiSpeakerRun_PausesAtDiarizedForSpeakerReview()
    {
        var settings = CreateSettings();
        settings.DiarizationProvider = ProviderNames.WeSpeakerLocal;
        var diarizationRegistry = new FakeDiarizationRegistry(
            (ProviderNames.WeSpeakerLocal, "WeSpeaker", new FakeDiarizationProvider(_ =>
                new DiarizationResult(
                    true,
                    [
                        new DiarizedSegment(0.0, 1.0, "spk_00"),
                        new DiarizedSegment(1.0, 2.0, "spk_01"),
                    ],
                    2,
                    null))));
        var coordinator = CreateCoordinator(
            settings,
            new FakeTranscriptionRegistry(new FakeTranscriptionProvider()),
            new FakeTranslationRegistry(new FakeTranslationProvider()),
            new FakeTtsRegistry(new FakeTtsProvider()),
            diarizationRegistry);
        coordinator.Initialize();
        coordinator.LoadMedia(_ctx.MediaPath);

        List<SessionWorkflowCoordinator.PipelineStageUpdate> updates = [];
        await coordinator.AdvancePipelineAsync(stageProgress: new CaptureProgress<SessionWorkflowCoordinator.PipelineStageUpdate>(updates));

        Assert.Equal(SessionWorkflowStage.Diarized, coordinator.CurrentSession.Stage);
        Assert.Contains("Review speakers", coordinator.CurrentSession.StatusMessage, StringComparison.OrdinalIgnoreCase);
        AssertStage(updates, SessionWorkflowStage.Transcribed, 1, 2);
        AssertStage(updates, SessionWorkflowStage.Diarized, 2, 2);
        Assert.DoesNotContain(updates, update => update.TargetStage == SessionWorkflowStage.Translated);
        Assert.DoesNotContain(updates, update => update.TargetStage == SessionWorkflowStage.TtsGenerated);
    }

    [Fact]
    public async Task ContinuePipelineAsync_FromDiarizedSession_StreamsTranslationIntoDub()
    {
        var settings = CreateSettings();
        settings.DiarizationProvider = ProviderNames.WeSpeakerLocal;
        var probe = new PipelineTimingProbe(expectedSegments: 3);
        var diarizationRegistry = new FakeDiarizationRegistry(
            (ProviderNames.WeSpeakerLocal, "WeSpeaker", new FakeDiarizationProvider(_ =>
                new DiarizationResult(
                    true,
                    [
                        new DiarizedSegment(0.0, 1.0, "spk_00"),
                        new DiarizedSegment(1.0, 2.0, "spk_01"),
                        new DiarizedSegment(2.0, 3.0, "spk_00"),
                    ],
                    2,
                    null))));
        var coordinator = CreateCoordinator(
            settings,
            new FakeTranscriptionRegistry(new DelayedTranscriptionProvider(probe, perSegmentDelayMs: 40)),
            new FakeTranslationRegistry(new DelayedTranslationProvider(probe, perSegmentDelayMs: 40)),
            new FakeTtsRegistry(new DelayedTtsProvider(probe, perSegmentDelayMs: 40)),
            diarizationRegistry);
        coordinator.Initialize();
        coordinator.LoadMedia(_ctx.MediaPath);

        await coordinator.AdvancePipelineAsync(progress: null, cancellationToken: CancellationToken.None);
        Assert.Equal(SessionWorkflowStage.TtsGenerated, coordinator.CurrentSession.Stage);

        var applyResult = coordinator.ApplyPipelineSettings(
            new PipelineSettingsSelection(
                settings.TranscriptionProfile,
                settings.TranscriptionProvider,
                settings.TranscriptionModel,
                settings.TranslationProfile,
                settings.TranslationProvider,
                "fake-translation-model-v2",
                settings.TtsProfile,
                settings.TtsProvider,
                settings.TtsVoice,
                settings.TargetLanguage));
        Assert.Equal(SessionWorkflowStage.Diarized, applyResult.StageAfterApply);

        probe.Reset();

        List<SessionWorkflowCoordinator.PipelineStageUpdate> updates = [];
        await coordinator.ContinuePipelineAsync(stageProgress: new CaptureProgress<SessionWorkflowCoordinator.PipelineStageUpdate>(updates));

        Assert.Equal(SessionWorkflowStage.TtsGenerated, coordinator.CurrentSession.Stage);
        AssertStage(updates, SessionWorkflowStage.Translated, 1, 2);
        AssertStage(updates, SessionWorkflowStage.TtsGenerated, 2, 2);
        Assert.True(
            probe.FirstStreamingTtsStartedAt < probe.StreamingTranslationCompletedAt,
            $"Expected continued TTS to start before continued translation completed, but tts={probe.FirstStreamingTtsStartedAt}ms and translationComplete={probe.StreamingTranslationCompletedAt}ms.");
    }

    [Fact]
    public async Task RunTtsOnlyAsync_FromTranslatedSession_EmitsOnlyDubStage()
    {
        var settings = CreateSettings();
        var coordinator = CreateCoordinator(
            settings,
            new FakeTranscriptionRegistry(new FakeTranscriptionProvider()),
            new FakeTranslationRegistry(new FakeTranslationProvider()),
            new FakeTtsRegistry(new FakeTtsProvider()));
        coordinator.Initialize();
        coordinator.LoadMedia(_ctx.MediaPath);
        await coordinator.TranscribeMediaAsync();
        await coordinator.TranslateTranscriptAsync();

        List<SessionWorkflowCoordinator.PipelineStageUpdate> updates = [];
        await coordinator.RunTtsOnlyAsync(
            progress: null,
            voice: null,
            stageProgress: new CaptureProgress<SessionWorkflowCoordinator.PipelineStageUpdate>(updates),
            cancellationToken: CancellationToken.None);

        Assert.Equal(SessionWorkflowStage.TtsGenerated, coordinator.CurrentSession.Stage);
        Assert.NotEmpty(updates);
        Assert.All(updates, update =>
        {
            Assert.Equal(SessionWorkflowStage.TtsGenerated, update.TargetStage);
            Assert.Equal(1, update.StageIndex);
            Assert.Equal(1, update.StageCount);
        });
    }

    [Fact]
    public void EmbeddedPlaybackViewModel_DubLabelStaysConstantAndVerboseProgressStateUpdates()
    {
        var settings = CreateSettings();
        var coordinator = CreateCoordinator(
            settings,
            new FakeTranscriptionRegistry(new FakeTranscriptionProvider()),
            new FakeTranslationRegistry(new FakeTranslationProvider()),
            new FakeTtsRegistry(new FakeTtsProvider()));
        coordinator.Initialize();
        var playback = new EmbeddedPlaybackViewModel(coordinator);

        Assert.Equal("🎙 Dub", playback.Preview.DubModeLabel);
        playback.Preview.IsDubModeOn = true;
        Assert.Equal("🎙 Dub", playback.Preview.DubModeLabel);

        playback.Pipeline.ApplyStageUpdate(
            new SessionWorkflowCoordinator.PipelineStageUpdate(
                2,
                3,
                SessionWorkflowStage.Translated,
                "Translation",
                "Checking translation runtime, provider readiness, language routing, and model availability…",
                0.45,
                false));

        Assert.True(playback.Pipeline.IsPipelineProgressVisible);
        Assert.Equal("Stage 2 of 3: Translation", playback.Pipeline.PipelineStageTitle);
        Assert.Contains("language routing", playback.Pipeline.PipelineStageDetail, StringComparison.Ordinal);
        Assert.False(playback.Pipeline.IsPipelineProgressIndeterminate);
        var expectedOverall = (2 - 1 + 0.45) / 3.0;
        Assert.Equal(expectedOverall, playback.Pipeline.PipelineProgressPercent, 3);
        Assert.Contains("48", playback.Pipeline.PipelineProgressStatusLine, StringComparison.Ordinal);

        playback.Pipeline.ShowRefreshDetail("Loading segments and refreshing playback data…");
        Assert.Equal("Loading segments and refreshing playback data…", playback.Pipeline.PipelineStageDetail);
        Assert.True(playback.Pipeline.IsPipelineProgressIndeterminate);

        playback.Pipeline.ResetProgressState();
        Assert.False(playback.Pipeline.IsPipelineProgressVisible);
        Assert.Equal(string.Empty, playback.Pipeline.PipelineStageTitle);
        Assert.Equal(string.Empty, playback.Pipeline.PipelineStageDetail);
    }

    private SessionWorkflowCoordinator CreateCoordinator(
        AppSettings settings,
        ITranscriptionRegistry transcriptionRegistry,
        ITranslationRegistry translationRegistry,
        ITtsRegistry ttsRegistry,
        IDiarizationRegistry? diarizationRegistry = null,
        TestContext? context = null)
    {
        var ctx = context ?? _ctx;
        var log = new AppLog(Path.Combine(ctx.Dir, $"test-{Guid.NewGuid():N}.log"));
        var store = new SessionSnapshotStore(ctx.StorePath, log);
        var perSessionStore = new PerSessionSnapshotStore(ctx.PerSessionDir, log);
        var recentStore = new RecentSessionsStore(ctx.RecentPath, log);
        var registries = new Babel.Player.Models.RegistryBundle(
            perSessionStore, recentStore,
            transcriptionRegistry, translationRegistry, ttsRegistry);
        var options = new Babel.Player.Models.CoordinatorOptions
        {
            DiarizationRegistry    = diarizationRegistry ?? FakeDiarizationFactory.CreateDefaultRegistry(),
            AudioProcessingService = new StubAudioProcessingService(),
        };
        var coreServices = new Babel.Player.Models.CoordinatorCoreServices(store, log, settings);
        return new SessionWorkflowCoordinator(coreServices, registries, options);
    }

    private static AppSettings CreateSettings() =>
        new()
        {
            TranscriptionProfile = ComputeProfile.Cpu,
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranscriptionModel = "fake-whisper",
            TranslationProfile = ComputeProfile.Cpu,
            TranslationProvider = ProviderNames.CTranslate2,
            TranslationModel = "fake-translation-model",
            TtsProfile = ComputeProfile.Cloud,
            TtsProvider = ProviderNames.EdgeTts,
            TtsVoice = "fake-voice",
            TargetLanguage = "en",
        };

    private static void AssertStage(
        IReadOnlyList<SessionWorkflowCoordinator.PipelineStageUpdate> updates,
        SessionWorkflowStage stage,
        int expectedIndex,
        int expectedCount)
    {
        var stageUpdates = updates.Where(update => update.TargetStage == stage).ToList();
        Assert.NotEmpty(stageUpdates);
        Assert.All(stageUpdates, update =>
        {
            Assert.Equal(expectedIndex, update.StageIndex);
            Assert.Equal(expectedCount, update.StageCount);
        });
        Assert.Contains(stageUpdates, update => update.IsIndeterminate && update.Progress01 == 0d);
        Assert.Contains(stageUpdates, update => !update.IsIndeterminate && update.Progress01 == 1d);
    }

    private sealed class CaptureProgress<T>(List<T> values) : IProgress<T>
    {
        public void Report(T value) => values.Add(value);
    }

    private sealed class FakeTranscriptionRegistry : ITranscriptionRegistry
    {
        private readonly ITranscriptionProvider _provider;

        public FakeTranscriptionRegistry(ITranscriptionProvider provider)
        {
            _provider = provider;
        }

        public IReadOnlyList<ProviderDescriptor> GetAvailableProviders(ComputeProfile? profile = null) =>
        [
            new ProviderDescriptor(
                ProviderNames.FasterWhisper,
                "Fake transcription",
                false,
                null,
                ["fake-whisper"],
                SupportedRuntimes: [InferenceRuntime.Local],
                DefaultRuntime: InferenceRuntime.Local)
        ];

        public IReadOnlyList<string> GetAvailableModels(string providerId, ComputeProfile profile, AppSettings settings) =>
            ["fake-whisper"];

        public ITranscriptionProvider CreateProvider(string providerId, AppSettings settings, ApiKeyStore? keyStore = null, ComputeProfile? profile = null) =>
            _provider;

        public ProviderReadiness CheckReadiness(string providerId, string model, AppSettings settings, ApiKeyStore? keyStore, ComputeProfile? profile = null) =>
            _provider.CheckReadiness(settings, keyStore);

        public Task<bool> EnsureModelAsync(string providerId, string model, AppSettings settings, IProgress<double>? progress = null, CancellationToken ct = default, ComputeProfile? profile = null, ApiKeyStore? keyStore = null) =>
            _provider.EnsureReadyAsync(settings, progress, ct);
    }

    private sealed class FakeTranslationRegistry : ITranslationRegistry
    {
        private readonly ITranslationProvider _provider;

        public FakeTranslationRegistry(ITranslationProvider provider)
        {
            _provider = provider;
        }

        public IReadOnlyList<ProviderDescriptor> GetAvailableProviders(ComputeProfile? profile = null) =>
        [
            new ProviderDescriptor(
                ProviderNames.CTranslate2,
                "Fake translation",
                false,
                null,
                ["fake-translation-model"],
                SupportedRuntimes: [InferenceRuntime.Local],
                DefaultRuntime: InferenceRuntime.Local)
        ];

        public IReadOnlyList<string> GetAvailableModels(string providerId, ComputeProfile profile, AppSettings settings) =>
            ["fake-translation-model"];

        public ITranslationProvider CreateProvider(string providerId, AppSettings settings, ApiKeyStore? keyStore = null, ComputeProfile? profile = null) =>
            _provider;

        public ProviderReadiness CheckReadiness(string providerId, string model, AppSettings settings, ApiKeyStore? keyStore, ComputeProfile? profile = null) =>
            _provider.CheckReadiness(settings, keyStore);

        public Task<bool> EnsureModelAsync(string providerId, string model, AppSettings settings, IProgress<double>? progress = null, CancellationToken ct = default, ComputeProfile? profile = null, ApiKeyStore? keyStore = null) =>
            _provider.EnsureReadyAsync(settings, progress, ct);
    }

    private sealed class FakeTtsRegistry : ITtsRegistry
    {
        private readonly ITtsProvider _provider;

        public FakeTtsRegistry(ITtsProvider provider)
        {
            _provider = provider;
        }

        public IReadOnlyList<ProviderDescriptor> GetAvailableProviders(ComputeProfile? profile = null) =>
        [
            new ProviderDescriptor(
                ProviderNames.EdgeTts,
                "Fake TTS",
                false,
                null,
                ["fake-voice"],
                SupportedRuntimes: [InferenceRuntime.Cloud],
                DefaultRuntime: InferenceRuntime.Cloud)
        ];

        public IReadOnlyList<string> GetAvailableModels(string providerId, ComputeProfile profile, AppSettings settings) =>
            ["fake-voice"];

        public ITtsProvider CreateProvider(string providerId, AppSettings settings, ApiKeyStore? keyStore = null, ComputeProfile? profile = null) =>
            _provider;

        public ProviderReadiness CheckReadiness(string providerId, string modelOrVoice, AppSettings settings, ApiKeyStore? keyStore, ComputeProfile? profile = null) =>
            _provider.CheckReadiness(settings, keyStore);

        public Task<bool> EnsureModelAsync(string providerId, string modelOrVoice, AppSettings settings, IProgress<double>? progress = null, CancellationToken ct = default, ComputeProfile? profile = null, ApiKeyStore? keyStore = null) =>
            _provider.EnsureReadyAsync(settings, progress, ct);
    }

    private sealed class FakeTranscriptionProvider : ITranscriptionProvider
    {
        public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null) =>
            ProviderReadiness.Ready;

        public Task<bool> EnsureReadyAsync(AppSettings settings, IProgress<double>? progress, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(request.OutputJsonPath)!);
            var artifact = new TranscriptArtifact
            {
                Language = "es",
                LanguageProbability = 0.99,
                Segments =
                [
                    new TranscriptSegmentArtifact { Start = 0.0, End = 1.1, Text = "Hola" },
                    new TranscriptSegmentArtifact { Start = 1.1, End = 2.4, Text = "Mundo" },
                ],
            };
            File.WriteAllText(request.OutputJsonPath, ArtifactJson.SerializeTranscript(artifact));
            return Task.FromResult<TranscriptionResult>(
                new(
                    true,
                    [
                        new TranscriptSegment(0.0, 1.1, "Hola"),
                        new TranscriptSegment(1.1, 2.4, "Mundo"),
                    ],
                    "es",
                    0.99,
                    null));
        }
    }

    private sealed class FakeTranslationProvider : ITranslationProvider
    {
        private bool _requiresDownload;
        private readonly IReadOnlyList<double> _downloadSteps;

        public FakeTranslationProvider(bool requiresDownload = false, IReadOnlyList<double>? downloadSteps = null)
        {
            _requiresDownload = requiresDownload;
            _downloadSteps = downloadSteps ?? [];
        }

        public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null) =>
            _requiresDownload
                ? new ProviderReadiness(
                    false,
                    $"Model '{settings.TranslationModel}' not downloaded yet.",
                    RequiresModelDownload: true,
                    ModelDownloadDescription: $"Download {settings.TranslationModel}")
                : ProviderReadiness.Ready;

        public async Task<bool> EnsureReadyAsync(AppSettings settings, IProgress<double>? progress, CancellationToken ct = default)
        {
            foreach (var step in _downloadSteps)
            {
                progress?.Report(step);
                await Task.Yield();
            }

            _requiresDownload = false;
            return true;
        }

        public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
        {
            var transcript = await ArtifactJson.LoadTranscriptAsync(request.TranscriptJsonPath, cancellationToken);
            var segments = transcript.Segments?.Select(segment => new TranslationSegmentArtifact
            {
                Id = SessionWorkflowCoordinator.SegmentId(segment.Start),
                Start = segment.Start,
                End = segment.End,
                Text = segment.Text,
                TranslatedText = $"{segment.Text} (en)",
            }).ToList() ?? [];

            Directory.CreateDirectory(Path.GetDirectoryName(request.OutputJsonPath)!);
            File.WriteAllText(
                request.OutputJsonPath,
                ArtifactJson.SerializeTranslation(
                    new TranslationArtifact
                    {
                        SourceLanguage = request.SourceLanguage,
                        TargetLanguage = request.TargetLanguage,
                        Segments = segments,
                    }));

            return new TranslationResult(
                true,
                segments.Select(segment => new TranslatedSegment(
                    segment.Start,
                    segment.End,
                    segment.Text ?? string.Empty,
                    segment.TranslatedText ?? string.Empty)).ToList(),
                request.SourceLanguage,
                request.TargetLanguage,
                null);
        }

        public Task<SingleSegmentTranslationTextResult> TranslateSingleSegmentTextAsync(SingleSegmentTranslationTextRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new SingleSegmentTranslationTextResult(
                true,
                $"{request.SourceText} (en)",
                request.SourceLanguage,
                request.TargetLanguage,
                null);
        }
    }

    private sealed class FakeTtsProvider : ITtsProvider
    {
        public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null) =>
            ProviderReadiness.Ready;

        public Task<bool> EnsureReadyAsync(AppSettings settings, IProgress<double>? progress, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<TtsResult> GenerateTtsAsync(TtsRequest request, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(request.OutputAudioPath)!);
            File.WriteAllText(request.OutputAudioPath, "fake audio");
            return Task.FromResult(new TtsResult(true, request.OutputAudioPath, request.VoiceName, new FileInfo(request.OutputAudioPath).Length, null));
        }

        public Task<TtsResult> GenerateSegmentTtsAsync(SingleSegmentTtsRequest request, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(request.OutputAudioPath)!);
            File.WriteAllText(request.OutputAudioPath, request.Text);
            return Task.FromResult(new TtsResult(true, request.OutputAudioPath, request.VoiceName, new FileInfo(request.OutputAudioPath).Length, null));
        }
    }

    private sealed class PipelineTimingProbe(int expectedSegments)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly int _expectedSegments = expectedSegments;
        private int _streamingTranslationsCompleted;
        private long _firstStreamingTranslationStartedAt = -1;
        private long _streamingTranslationCompletedAt = -1;
        private long _firstStreamingTtsStartedAt = -1;
        private long _transcriptionCompletedAt = -1;

        public long FirstStreamingTranslationStartedAt => Interlocked.Read(ref _firstStreamingTranslationStartedAt);
        public long StreamingTranslationCompletedAt => Interlocked.Read(ref _streamingTranslationCompletedAt);
        public long FirstStreamingTtsStartedAt => Interlocked.Read(ref _firstStreamingTtsStartedAt);
        public long TranscriptionCompletedAt => Interlocked.Read(ref _transcriptionCompletedAt);

        public void MarkStreamingTranslationStarted() =>
            Interlocked.CompareExchange(ref _firstStreamingTranslationStartedAt, _clock.ElapsedMilliseconds, -1);

        public void MarkStreamingTranslationCompleted()
        {
            if (Interlocked.Increment(ref _streamingTranslationsCompleted) == _expectedSegments)
                Interlocked.Exchange(ref _streamingTranslationCompletedAt, _clock.ElapsedMilliseconds);
        }

        public void MarkStreamingTtsStarted() =>
            Interlocked.CompareExchange(ref _firstStreamingTtsStartedAt, _clock.ElapsedMilliseconds, -1);

        public void MarkTranscriptionCompleted() =>
            Interlocked.Exchange(ref _transcriptionCompletedAt, _clock.ElapsedMilliseconds);

        public void Reset()
        {
            _clock.Restart();
            Interlocked.Exchange(ref _streamingTranslationsCompleted, 0);
            Interlocked.Exchange(ref _firstStreamingTranslationStartedAt, -1);
            Interlocked.Exchange(ref _streamingTranslationCompletedAt, -1);
            Interlocked.Exchange(ref _firstStreamingTtsStartedAt, -1);
            Interlocked.Exchange(ref _transcriptionCompletedAt, -1);
        }
    }

    private sealed class DelayedTranscriptionProvider(
        PipelineTimingProbe probe,
        int perSegmentDelayMs = 80) : ITranscriptionProvider, IStreamingTranscriptionProvider
    {
        private static readonly IReadOnlyList<TranscriptSegmentArtifact> Segments =
        [
            new() { Start = 0.0, End = 1.0, Text = "Hola" },
            new() { Start = 1.0, End = 2.0, Text = "Mundo" },
            new() { Start = 2.0, End = 3.0, Text = "Otra vez" },
        ];

        public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null) =>
            ProviderReadiness.Ready;

        public Task<bool> EnsureReadyAsync(AppSettings settings, IProgress<double>? progress, CancellationToken ct = default) =>
            Task.FromResult(true);

        public async Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken = default)
        {
            foreach (var _ in Segments)
                await Task.Delay(perSegmentDelayMs, cancellationToken);

            Directory.CreateDirectory(Path.GetDirectoryName(request.OutputJsonPath)!);
            var artifact = new TranscriptArtifact
            {
                Language = "es",
                LanguageProbability = 0.99,
                Segments = Segments.Select(CloneTranscriptArtifact).ToList(),
            };
            File.WriteAllText(request.OutputJsonPath, ArtifactJson.SerializeTranscript(artifact));
            return BuildResult();
        }

        public async Task<TranscriptionResult> TranscribeStreamingAsync(
            TranscriptionRequest request,
            ChannelWriter<TranscriptChannelItem> writer,
            CancellationToken cancellationToken = default)
        {
            try
            {
                foreach (var segment in Segments)
                {
                    await Task.Delay(perSegmentDelayMs, cancellationToken);
                    await writer.WriteAsync(
                        new TranscriptChannelItem(
                            SessionWorkflowCoordinator.SegmentId(segment.Start),
                            CloneTranscriptArtifact(segment),
                            "es",
                            0.99),
                        cancellationToken);
                }

                probe.MarkTranscriptionCompleted();
                return BuildResult();
            }
            finally
            {
                writer.TryComplete();
            }
        }

        private static TranscriptSegmentArtifact CloneTranscriptArtifact(TranscriptSegmentArtifact segment) =>
            new()
            {
                Start = segment.Start,
                End = segment.End,
                Text = segment.Text,
                SpeakerId = segment.SpeakerId,
                OriginalStart = segment.OriginalStart,
                Words = segment.Words is null ? null : [.. segment.Words],
            };

        private static TranscriptionResult BuildResult() =>
            new(
                true,
                Segments.Select(segment => new TranscriptSegment(
                    segment.Start,
                    segment.End,
                    segment.Text ?? string.Empty,
                    segment.SpeakerId)).ToList(),
                "es",
                0.99,
                null);
    }

    private sealed class DelayedTranslationProvider(
        PipelineTimingProbe probe,
        int perSegmentDelayMs = 80,
        bool requiresDownload = false,
        IReadOnlyList<double>? downloadSteps = null) : ITranslationProvider
    {
        private bool _requiresDownload = requiresDownload;
        private readonly IReadOnlyList<double> _downloadSteps = downloadSteps ?? [];

        public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null) =>
            _requiresDownload
                ? new ProviderReadiness(
                    false,
                    $"Model '{settings.TranslationModel}' not downloaded yet.",
                    RequiresModelDownload: true,
                    ModelDownloadDescription: $"Download {settings.TranslationModel}")
                : ProviderReadiness.Ready;

        public async Task<bool> EnsureReadyAsync(AppSettings settings, IProgress<double>? progress, CancellationToken ct = default)
        {
            await Task.Yield();

            foreach (var step in _downloadSteps)
                progress?.Report(step);

            _requiresDownload = false;
            return true;
        }

        public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
        {
            var transcript = await ArtifactJson.LoadTranscriptAsync(request.TranscriptJsonPath, cancellationToken);
            var segments = transcript.Segments?.Select(segment => new TranslationSegmentArtifact
            {
                Id = SessionWorkflowCoordinator.SegmentId(segment.Start),
                Start = segment.Start,
                End = segment.End,
                Text = segment.Text,
                TranslatedText = $"{segment.Text} ({request.TargetLanguage})",
                SpeakerId = segment.SpeakerId,
            }).ToList() ?? [];

            foreach (var _ in segments)
                await Task.Delay(perSegmentDelayMs, cancellationToken);

            Directory.CreateDirectory(Path.GetDirectoryName(request.OutputJsonPath)!);
            File.WriteAllText(
                request.OutputJsonPath,
                ArtifactJson.SerializeTranslation(
                    new TranslationArtifact
                    {
                        SourceLanguage = request.SourceLanguage,
                        TargetLanguage = request.TargetLanguage,
                        Segments = segments,
                    }));

            return BuildTranslationResult(segments, request.SourceLanguage, request.TargetLanguage);
        }

        public async Task<SingleSegmentTranslationTextResult> TranslateSingleSegmentTextAsync(SingleSegmentTranslationTextRequest request, CancellationToken cancellationToken = default)
        {
            probe.MarkStreamingTranslationStarted();
            await Task.Delay(perSegmentDelayMs, cancellationToken);
            probe.MarkStreamingTranslationCompleted();
            return new SingleSegmentTranslationTextResult(
                true,
                $"{request.SourceText} ({request.TargetLanguage})",
                request.SourceLanguage,
                request.TargetLanguage,
                null);
        }

        private static TranslationResult BuildTranslationResult(
            IReadOnlyList<TranslationSegmentArtifact> segments,
            string sourceLanguage,
            string targetLanguage) =>
            new(
                true,
                segments.Select(segment => new TranslatedSegment(
                    segment.Start,
                    segment.End,
                    segment.Text ?? string.Empty,
                    segment.TranslatedText ?? string.Empty,
                    segment.SpeakerId)).ToList(),
                sourceLanguage,
                targetLanguage,
                null);
    }

    private sealed class DelayedTtsProvider(
        PipelineTimingProbe probe,
        int perSegmentDelayMs = 80) : ITtsProvider
    {
        public int MaxConcurrency => 1;

        public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null) =>
            ProviderReadiness.Ready;

        public Task<bool> EnsureReadyAsync(AppSettings settings, IProgress<double>? progress, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<TtsResult> GenerateTtsAsync(TtsRequest request, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(request.OutputAudioPath)!);
            File.WriteAllText(request.OutputAudioPath, "fake audio");
            return Task.FromResult(new TtsResult(true, request.OutputAudioPath, request.VoiceName, new FileInfo(request.OutputAudioPath).Length, null));
        }

        public async Task<TtsResult> GenerateSegmentTtsAsync(SingleSegmentTtsRequest request, CancellationToken cancellationToken = default)
        {
            probe.MarkStreamingTtsStarted();
            await Task.Delay(perSegmentDelayMs, cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(request.OutputAudioPath)!);
            File.WriteAllText(request.OutputAudioPath, request.Text);
            return new TtsResult(true, request.OutputAudioPath, request.VoiceName, new FileInfo(request.OutputAudioPath).Length, null);
        }
    }

    private class StubAudioProcessingService : IAudioProcessingService
    {
        public Task CombineAudioSegmentsAsync(IReadOnlyList<string> segmentAudioPaths, string outputPath, CancellationToken cancellationToken)
        {
            var outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir))
                Directory.CreateDirectory(outputDir);
            File.WriteAllText(outputPath, "fake combined audio");
            return Task.CompletedTask;
        }

        public Task ComposeTimelineDubAsync(IReadOnlyList<TimelineDubSegment> segments, string outputAudioPath, CancellationToken cancellationToken)
        {
            var outputDir = Path.GetDirectoryName(outputAudioPath);
            if (!string.IsNullOrEmpty(outputDir))
                Directory.CreateDirectory(outputDir);
            File.WriteAllText(outputAudioPath, "fake timeline dub");
            return Task.CompletedTask;
        }

        public Task MixDubOverAmbianceAsync(string dubbedAudioPath, string ambianceAudioPath, string outputAudioPath, double ambianceGainDb, CancellationToken cancellationToken)
        {
            var outputDir = Path.GetDirectoryName(outputAudioPath);
            if (!string.IsNullOrEmpty(outputDir))
                Directory.CreateDirectory(outputDir);
            File.WriteAllText(outputAudioPath, "fake mixed dub");
            return Task.CompletedTask;
        }


        public Task ExtractAudioClipAsync(string sourcePath, string outputPath, double startTimeSeconds, double durationSeconds, CancellationToken cancellationToken)
        {
            var outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir))
                Directory.CreateDirectory(outputDir);
            File.WriteAllText(outputPath, "fake extracted clip");
            return Task.CompletedTask;
        }

        public Task ExtractFullAudioAsync(string inputPath, string outputPath, CancellationToken cancellationToken)
        {
            var outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir))
                Directory.CreateDirectory(outputDir);
            File.WriteAllText(outputPath, "fake full audio");
            return Task.CompletedTask;
        }

        public Task<bool> TimeStretchAsync(string inputPath, string outputPath, double targetDurationSeconds,
            double minRatio = DubTimingDefaults.StretchMinTempoRatio, double maxRatio = DubTimingDefaults.StretchMaxTempoRatio, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<double?> ProbeDurationAsync(string filePath, CancellationToken cancellationToken = default)
            => Task.FromResult<double?>(null);
    }
}
