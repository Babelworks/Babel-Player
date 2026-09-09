using System;
using System.IO;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;
using Babel.Player.ViewModels;

namespace BabelPlayer.Tests;

/// <summary>
/// Seam tests for the Chatterbox voice-cloning consent checkbox: AXAML binding,
/// resource key, provider predicate, and persisted default. The checkbox setter
/// is intentionally not exercised here: it calls
/// <c>NotifySettingsModified</c>, whose full playback handler can deadlock in CI
/// when Application.Current is set without a pumping UI thread.
/// </summary>
public sealed class ChatterboxConsentTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"babel-consent-tests-{Guid.NewGuid():N}");
    private readonly AppLog _log;
    private readonly SessionSnapshotStore _store;
    private readonly PerSessionSnapshotStore _perSessionStore;
    private readonly RecentSessionsStore _recentStore;

    public ChatterboxConsentTests()
    {
        Directory.CreateDirectory(_dir);
        _log = new AppLog(Path.Combine(_dir, "test.log"));
        _store = new SessionSnapshotStore(Path.Combine(_dir, "session.json"), _log);
        _perSessionStore = new PerSessionSnapshotStore(Path.Combine(_dir, "sessions"), _log);
        _recentStore = new RecentSessionsStore(Path.Combine(_dir, "recent-sessions.json"), _log);
    }

    [Fact]
    public void MainWindow_BindsCloneConsentCheckboxToChatterboxSelection()
    {
        var axaml = File.ReadAllText(FindRepoFile("Views", "MainWindow.axaml"));

        Assert.Contains("IsChecked=\"{Binding Playback.ChatterboxVoiceCloneConsent, Mode=TwoWay}\"", axaml, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding Playback.IsChatterboxTtsSelected}\"", axaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"{local:Localize Check_ChatterboxCloneConsent}\"", axaml, StringComparison.Ordinal);
    }

    [Fact]
    public void NeutralResources_ContainConsentLabel()
    {
        var resx = File.ReadAllText(FindRepoFile("Resources", "Strings.resx"));

        Assert.Contains("Check_ChatterboxCloneConsent", resx, StringComparison.Ordinal);
    }

    [Fact]
    public void IsChatterboxProvider_MatchesCloneProviderOnly()
    {
        Assert.True(EmbeddedPlaybackViewModel.IsChatterboxProvider(ProviderNames.Chatterbox));
        Assert.True(EmbeddedPlaybackViewModel.IsChatterboxProvider("Chatterbox"));
        Assert.False(EmbeddedPlaybackViewModel.IsChatterboxProvider(ProviderNames.Piper));
        Assert.False(EmbeddedPlaybackViewModel.IsChatterboxProvider(ProviderNames.EdgeTts));
        Assert.False(EmbeddedPlaybackViewModel.IsChatterboxProvider(null));
        Assert.False(EmbeddedPlaybackViewModel.IsChatterboxProvider(string.Empty));
    }

    [Fact]
    public void Consent_DefaultsOffAndSelectionHidden()
    {
        using var coordinator = CreateCoordinator(new AppSettings());
        using var playback = new EmbeddedPlaybackViewModel(coordinator);

        Assert.False(playback.ChatterboxVoiceCloneConsent);
        Assert.False(playback.IsChatterboxTtsSelected);
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
