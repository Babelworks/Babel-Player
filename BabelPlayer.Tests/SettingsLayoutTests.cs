using System;
using System.IO;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;
using Babel.Player.ViewModels;

namespace BabelPlayer.Tests;

public sealed class SettingsLayoutTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"babel-settings-layout-tests-{Guid.NewGuid():N}");
    private readonly AppLog _log;
    private readonly SessionSnapshotStore _store;
    private readonly PerSessionSnapshotStore _perSessionStore;
    private readonly RecentSessionsStore _recentStore;
    private readonly SettingsService _settingsService;

    public SettingsLayoutTests()
    {
        Directory.CreateDirectory(_dir);
        _log = new AppLog(Path.Combine(_dir, "test.log"));
        _store = new SessionSnapshotStore(Path.Combine(_dir, "session.json"), _log);
        _perSessionStore = new PerSessionSnapshotStore(Path.Combine(_dir, "sessions"), _log);
        _recentStore = new RecentSessionsStore(Path.Combine(_dir, "recent-sessions.json"), _log);
        _settingsService = new SettingsService(Path.Combine(_dir, "app-settings.json"), _log);
    }

    [Fact]
    public void SettingsWindow_LayoutCard_ExposesPaneVisibilityToggles()
    {
        var axamlPath = FindRepoFile("Views", "SettingsWindow.axaml");
        var axaml = File.ReadAllText(axamlPath);

        Assert.Contains("IsChecked=\"{Binding ShowPipelinePane, Mode=TwoWay}\"", axaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"{local:Localize Settings_Check_ShowPipelinePane}\"", axaml, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding ShowSegmentsPane, Mode=TwoWay}\"", axaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"{local:Localize Settings_Check_ShowSegmentsPane}\"", axaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{local:Localize Settings_Hint_PaneVisibility}\"", axaml, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding StoreProjectsNextToMedia}\"", axaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"{local:Localize Settings_Check_StoreProjectsNextToMedia}\"", axaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_UpdatesPreviewPaneVisibilityFromSettings()
    {
        var settings = new AppSettings
        {
            IsPipelinePaneVisible = true,
            IsSegmentsPaneVisible = false,
            SwapPaneSides = false
        };

        using var coordinator = CreateCoordinator(settings);
        using var playback = new EmbeddedPlaybackViewModel(coordinator);

        Assert.False(playback.Preview.IsSegmentsPaneVisible);
        Assert.False(playback.Preview.IsRightPaneVisible);

        // Update persisted settings and sync the preview VM directly. Do not call
        // coordinator.NotifySettingsModified() here: that runs the full playback
        // settings handler (provider health refresh, dispatcher marshalling). In CI,
        // another test may leave Application.Current set without a pumping UI thread,
        // and InvokeAsync-based paths can deadlock for minutes under --blame-hang.
        coordinator.CurrentSettings.IsSegmentsPaneVisible = true;
        playback.Preview.SyncPaneLayoutFromSettings();

        Assert.True(coordinator.CurrentSettings.IsSegmentsPaneVisible);
        Assert.True(playback.Preview.IsSegmentsPaneVisible);
        Assert.True(playback.Preview.IsRightPaneVisible);
    }

    public void Dispose()
    {
        _log.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private SessionWorkflowCoordinator CreateCoordinator(AppSettings settings)
    {
        var registries = new RegistryBundle(
            _perSessionStore,
            _recentStore,
            new TranscriptionRegistry(_log),
            new TranslationRegistry(_log),
            new TtsRegistry(_log));
        var coreServices = new CoordinatorCoreServices(_store, _log, settings);
        return new SessionWorkflowCoordinator(coreServices, registries);
    }

    private static string FindRepoFile(params string[] relativePathParts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine([current.FullName, .. relativePathParts]);
            if (File.Exists(candidate))
                return candidate;

            current = current.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate repo file '{Path.Combine(relativePathParts)}' from '{AppContext.BaseDirectory}'.");
    }
}
