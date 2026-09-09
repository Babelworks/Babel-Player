using System;
using System.IO;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Settings;

namespace BabelPlayer.Tests;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _settingsPath;
    private readonly AppLog _log;

    public SettingsServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"babel-settings-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _settingsPath = Path.Combine(_dir, "app-settings.json");
        _log = new AppLog(Path.Combine(_dir, "settings.log"));
    }

    public void Dispose()
    {
        _log.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch { }
    }

    private string[] FindCorruptBackups() =>
        Directory.GetFiles(_dir, "app-settings.json.corrupt.*", SearchOption.TopDirectoryOnly);

    [Fact]
    public void LoadOrDefault_LegacyContainerizedRuntime_MigratesToGpuProfilesAndDockerBackend()
    {
        File.WriteAllText(
            _settingsPath,
            """
            {
              "TranscriptionProvider": "containerized",
              "TranscriptionRuntime": "Containerized",
              "TranslationProvider": "containerized",
              "TranslationRuntime": "Containerized",
              "TtsProvider": "containerized",
              "TtsRuntime": "Containerized",
              "ContainerizedServiceUrl": "http://localhost:8000",
              "AlwaysRunContainerAtAppStart": true
            }
            """);

        var service = new SettingsService(_settingsPath, _log);
        var settings = service.LoadOrDefault();

        Assert.Equal(ComputeProfile.Gpu, settings.TranscriptionProfile);
        Assert.Equal(ProviderNames.FasterWhisper, settings.TranscriptionProvider);
        Assert.Equal(ComputeProfile.Gpu, settings.TranslationProfile);
        Assert.Equal(ProviderNames.Nllb200, settings.TranslationProvider);
        Assert.Equal(ComputeProfile.Gpu, settings.TtsProfile);
        Assert.Equal(ProviderNames.Qwen, settings.TtsProvider);
        Assert.Equal(GpuHostBackend.DockerHost, settings.PreferredLocalGpuBackend);
        Assert.True(settings.AlwaysStartLocalGpuRuntimeAtAppStart);
        Assert.Equal("http://localhost:8000", settings.AdvancedGpuServiceUrl);
    }

    [Fact]
    public void LoadOrDefault_WhenProfilesMissing_InfersProfilesFromProviderCatalog()
    {
        File.WriteAllText(
            _settingsPath,
            """
            {
              "TranscriptionProvider": "gemini-transcription",
              "TranslationProvider": "nllb-200",
              "TtsProvider": "piper"
            }
            """);

        var service = new SettingsService(_settingsPath, _log);
        var settings = service.LoadOrDefault();

        Assert.Equal(ComputeProfile.Cloud, settings.TranscriptionProfile);
        Assert.Equal(ComputeProfile.Cpu, settings.TranslationProfile);
        Assert.Equal(ComputeProfile.Cpu, settings.TtsProfile);
    }

    [Fact]
    public void Save_DoesNotPersistLegacyRuntimeFields()
    {
        var service = new SettingsService(_settingsPath, _log);
        service.Save(new AppSettings
        {
            TranscriptionProfile = ComputeProfile.Gpu,
            TranscriptionProvider = ProviderNames.FasterWhisper,
            TranslationProfile = ComputeProfile.Cpu,
            TranslationProvider = ProviderNames.Nllb200,
            TtsProfile = ComputeProfile.Cloud,
            TtsProvider = ProviderNames.EdgeTts,
            PreferredLocalGpuBackend = GpuHostBackend.ManagedVenv
        });

        var json = File.ReadAllText(_settingsPath);

        Assert.Contains("\"TranscriptionProfile\": \"gpu\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"TranscriptionRuntime\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"TranslationRuntime\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"TtsRuntime\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ContainerizedServiceUrl\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"AlwaysRunContainerAtAppStart\"", json, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void SaveAndLoad_DubTimingMode_PauseIsCoercedToOff()
    {
        var service = new SettingsService(_settingsPath, _log);
        service.Save(new AppSettings
        {
            DubTimingMode = SegmentTimingMode.Pause
        });

        var loaded = service.LoadOrDefault();

        Assert.Equal(SegmentTimingMode.Off, loaded.DubTimingMode);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void SaveAndLoad_AmbianceMixDb_RoundTrips()
    {
        var service = new SettingsService(_settingsPath, _log);
        service.Save(new AppSettings
        {
            AmbianceMixDb = -9.5
        });

        var loaded = service.LoadOrDefault();

        Assert.Equal(-9.5, loaded.AmbianceMixDb, precision: 3);
    }

    [Fact]
    public void SaveAndLoad_VocalSeparationEnabled_RoundTrips()
    {
        var service = new SettingsService(_settingsPath, _log);
        service.Save(new AppSettings
        {
            VocalSeparationEnabled = true
        });

        var loaded = service.LoadOrDefault();

        Assert.True(loaded.VocalSeparationEnabled);
    }

    [Fact]
    public void SaveAndLoad_KeepRenderArtifacts_RoundTrips()
    {
        var service = new SettingsService(_settingsPath, _log);
        service.Save(new AppSettings
        {
            KeepRenderArtifacts = true
        });

        var loaded = service.LoadOrDefault();

        Assert.True(loaded.KeepRenderArtifacts);
        Assert.False(new AppSettings().KeepRenderArtifacts);
    }

    [Fact]
    public void SaveAndLoad_StoreProjectsNextToMedia_RoundTrips()
    {
        var service = new SettingsService(_settingsPath, _log);
        Assert.True(new AppSettings().StoreProjectsNextToMedia);

        service.Save(new AppSettings
        {
            StoreProjectsNextToMedia = false
        });

        var loaded = service.LoadOrDefault();
        Assert.False(loaded.StoreProjectsNextToMedia);
    }

    [Fact]
    public void SaveAndLoad_PaneLayout_RoundTrips()
    {
        var service = new SettingsService(_settingsPath, _log);
        service.Save(new AppSettings
        {
            IsPipelinePaneVisible = false,
            IsSegmentsPaneVisible = true,
            PipelinePaneWidth = 312,
            SegmentsPaneWidth = 418,
            SwapPaneSides = true
        });

        var loaded = service.LoadOrDefault();

        Assert.False(loaded.IsPipelinePaneVisible);
        Assert.True(loaded.IsSegmentsPaneVisible);
        Assert.Equal(312, loaded.PipelinePaneWidth, precision: 3);
        Assert.Equal(418, loaded.SegmentsPaneWidth, precision: 3);
        Assert.True(loaded.SwapPaneSides);
    }

    [Fact]
    public void LoadOrDefault_InvalidPaneWidths_FallBackToDefaults()
    {
        // Valid JSON so deserialization succeeds and NormalizePaneWidth runs per field (invalid numbers, not a thrown JsonException).
        File.WriteAllText(
            _settingsPath,
            """
            {
              "PipelinePaneWidth": -1,
              "SegmentsPaneWidth": 0
            }
            """);

        var service = new SettingsService(_settingsPath, _log);
        var loaded = service.LoadOrDefault();

        Assert.Equal(AppSettings.PipelinePaneDefaultWidth, loaded.PipelinePaneWidth, precision: 3);
        Assert.Equal(AppSettings.SegmentsPaneDefaultWidth, loaded.SegmentsPaneWidth, precision: 3);
    }

    [Fact]
    public void Save_InvalidPaneWidths_WritesDefaults()
    {
        var service = new SettingsService(_settingsPath, _log);
        service.Save(new AppSettings
        {
            PipelinePaneWidth = double.PositiveInfinity,
            SegmentsPaneWidth = 0
        });

        var loaded = service.LoadOrDefault();

        Assert.Equal(AppSettings.PipelinePaneDefaultWidth, loaded.PipelinePaneWidth, precision: 3);
        Assert.Equal(AppSettings.SegmentsPaneDefaultWidth, loaded.SegmentsPaneWidth, precision: 3);
    }

    [Fact]
    public void LoadOrDefault_CorruptJson_MovesUnreadableFileToBackup()
    {
        File.WriteAllText(_settingsPath, "{ definitely not valid json");

        var service = new SettingsService(_settingsPath, _log);
        var loaded = service.LoadOrDefault();

        Assert.Equal(new AppSettings().Theme, loaded.Theme);
        Assert.False(File.Exists(_settingsPath));
        Assert.Single(FindCorruptBackups());
    }

    [Fact]
    public void LoadOrDefault_WhitespaceFile_MovesUnreadableFileToBackup()
    {
        File.WriteAllText(_settingsPath, "   \r\n   ");

        var service = new SettingsService(_settingsPath, _log);
        var loaded = service.LoadOrDefault();

        Assert.Equal(new AppSettings().Theme, loaded.Theme);
        Assert.False(File.Exists(_settingsPath));
        Assert.Single(FindCorruptBackups());
    }
}
