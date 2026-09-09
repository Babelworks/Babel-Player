using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Babel.Player.ViewModels;

public sealed partial class EmbeddedPlaybackSpeakerRoutingViewModel : ViewModelBase
{
    private readonly EmbeddedPlaybackViewModel _parent;
    private readonly SessionWorkflowCoordinator _coordinator;
    private string _autoSpeakerDetectionStatus = "Manual speaker mapping is the default release flow.";
    private bool _detectMultipleSpeakers;
    private string _lastEnabledDiarizationProvider = ProviderNames.WeSpeakerLocal;

    internal EmbeddedPlaybackSpeakerRoutingViewModel(
        EmbeddedPlaybackViewModel parent,
        SessionWorkflowCoordinator coordinator)
    {
        _parent = parent;
        _coordinator = coordinator;
    }

    /// <summary>
    /// Local diarization providers shown when multi-speaker detection is enabled (WeSpeaker, SortFormer).
    /// </summary>
    public IReadOnlyList<string> AvailableLocalDiarizationProviders { get; } =
    [
        ProviderNames.WeSpeakerLocal,
        ProviderNames.SortFormerLocal,
    ];

    /// <summary>
    /// When true, speaker separation runs using the selected local diarization provider.
    /// Turning on defaults to WeSpeaker unless SortFormer was the last enabled choice.
    /// </summary>
    public bool DetectMultipleSpeakers
    {
        get => _detectMultipleSpeakers;
        set
        {
            if (_parent.IsSynchronizingPipelineSettings)
            {
                SetProperty(ref _detectMultipleSpeakers, value);
                return;
            }

            if (_detectMultipleSpeakers == value)
                return;

            var target = value
                ? ResolveEnabledDiarizationProvider()
                : string.Empty;
            if (!string.Equals(target, _coordinator.CurrentSettings.DiarizationProvider, StringComparison.Ordinal))
            {
                _coordinator.CurrentSettings.DiarizationProvider = target;
                _coordinator.CurrentSettings.DiarizationMinSpeakers = null;
                _coordinator.CurrentSettings.DiarizationMaxSpeakers = null;
                _coordinator.NotifySettingsModified();
                OnPropertyChanged(nameof(DiarizationProvider));
                _parent.RefreshProviderHealthDiagnostics();
                _parent.Pipeline.NotifySessionStateChanged();
            }

            SetProperty(ref _detectMultipleSpeakers, value);
            OnPropertyChanged(nameof(IsDiarizationProviderSelectable));
        }
    }

    /// <summary>
    /// True when multi-speaker detection is on and the provider combo should be shown.
    /// </summary>
    public bool IsDiarizationProviderSelectable => DetectMultipleSpeakers;

    /// <summary>
    /// Backing value in session settings (empty = off). Prefer <see cref="DetectMultipleSpeakers"/> in UI.
    /// </summary>
    public string DiarizationProvider
    {
        get => _coordinator.CurrentSettings.DiarizationProvider;
        set
        {
            if (_parent.IsSynchronizingPipelineSettings)
                return;

            var normalized = InferenceRuntimeCatalog.NormalizeDiarizationProvider(
                string.IsNullOrWhiteSpace(value) ? string.Empty : value);

            if (!string.IsNullOrWhiteSpace(normalized) &&
                (string.Equals(normalized, ProviderNames.WeSpeakerLocal, StringComparison.Ordinal) ||
                 string.Equals(normalized, ProviderNames.SortFormerLocal, StringComparison.Ordinal)))
            {
                _lastEnabledDiarizationProvider = normalized;
            }

            if (string.Equals(normalized, _coordinator.CurrentSettings.DiarizationProvider, StringComparison.Ordinal))
            {
                _parent.IsSynchronizingPipelineSettings = true;
                try
                {
                    SetProperty(
                        ref _detectMultipleSpeakers,
                        !string.IsNullOrWhiteSpace(normalized),
                        nameof(DetectMultipleSpeakers));
                }
                finally
                {
                    _parent.IsSynchronizingPipelineSettings = false;
                }

                OnPropertyChanged(nameof(IsDiarizationProviderSelectable));
                return;
            }

            _coordinator.CurrentSettings.DiarizationProvider = normalized;
            _coordinator.CurrentSettings.DiarizationMinSpeakers = null;
            _coordinator.CurrentSettings.DiarizationMaxSpeakers = null;
            _coordinator.NotifySettingsModified();

            _parent.IsSynchronizingPipelineSettings = true;
            try
            {
                SetProperty(
                    ref _detectMultipleSpeakers,
                    !string.IsNullOrWhiteSpace(normalized),
                    nameof(DetectMultipleSpeakers));
            }
            finally
            {
                _parent.IsSynchronizingPipelineSettings = false;
            }

            OnPropertyChanged(nameof(DiarizationProvider));
            OnPropertyChanged(nameof(IsDiarizationProviderSelectable));
            _parent.RefreshProviderHealthDiagnostics();
            _parent.Pipeline.NotifySessionStateChanged();
        }
    }

    [ObservableProperty]
    private decimal? _diarizationMinSpeakers;

    [ObservableProperty]
    private decimal? _diarizationMaxSpeakers;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSpeakers))]
    [NotifyPropertyChangedFor(nameof(IsMultiSpeakerNoSpeakersYet))]
    [NotifyPropertyChangedFor(nameof(CanOpenSpeakerSetupWizard))]
    private ObservableCollection<string> _speakerIds = new();

    [ObservableProperty]
    private string? _selectedSpeakerId;

    [ObservableProperty]
    private string _selectedSpeakerAssignedVoice = string.Empty;

    [ObservableProperty]
    private string _selectedSpeakerReferenceAudioPath = string.Empty;

    public string AutoSpeakerDetectionStatus
    {
        get => _autoSpeakerDetectionStatus;
        private set
        {
            if (!SetProperty(ref _autoSpeakerDetectionStatus, value))
                return;

            OnPropertyChanged(nameof(HasAutoSpeakerDetectionStatus));
        }
    }

    public bool HasSpeakers => SpeakerIds.Count > 0;
    public bool HasAutoSpeakerDetectionStatus => !string.IsNullOrWhiteSpace(AutoSpeakerDetectionStatus);
    public bool IsMultiSpeakerNoSpeakersYet => !HasSpeakers;
    public bool IsTtsCloningProvider => string.Equals(_parent.TtsProvider, ProviderNames.Qwen, StringComparison.Ordinal);
    public bool CanOpenSpeakerSetupWizard => HasSpeakers;

    internal void SyncFromSettings()
    {
        _parent.IsSynchronizingPipelineSettings = true;
        try
        {
            var normalized = InferenceRuntimeCatalog.NormalizeDiarizationProvider(
                _coordinator.CurrentSettings.DiarizationProvider);
            if (!string.Equals(normalized, _coordinator.CurrentSettings.DiarizationProvider, StringComparison.Ordinal))
                _coordinator.CurrentSettings.DiarizationProvider = normalized;

            if (!string.IsNullOrWhiteSpace(normalized) &&
                (string.Equals(normalized, ProviderNames.WeSpeakerLocal, StringComparison.Ordinal) ||
                 string.Equals(normalized, ProviderNames.SortFormerLocal, StringComparison.Ordinal)))
            {
                _lastEnabledDiarizationProvider = normalized;
            }

            _detectMultipleSpeakers = !string.IsNullOrWhiteSpace(normalized);
            OnPropertyChanged(nameof(DetectMultipleSpeakers));
            OnPropertyChanged(nameof(DiarizationProvider));
            OnPropertyChanged(nameof(IsDiarizationProviderSelectable));
            DiarizationMinSpeakers = null;
            DiarizationMaxSpeakers = null;
            RebuildSpeakerIds(_parent.Preview.Segments, _parent.Preview.SelectedSegment?.SpeakerId);
        }
        finally
        {
            _parent.IsSynchronizingPipelineSettings = false;
        }
    }

    internal void SetAutoSpeakerDetectionStatus(string status) => AutoSpeakerDetectionStatus = status;

    internal void NotifyTtsProviderChanged()
    {
        OnPropertyChanged(nameof(IsTtsCloningProvider));
        OnPropertyChanged(nameof(CanOpenSpeakerSetupWizard));
    }

    internal void RebuildSpeakerIds(IEnumerable<WorkflowSegmentState> segments, string? preferredSpeakerId = null)
    {
        var ordered = segments
            .Select(segment => segment.SpeakerId)
            .Where(speakerId => !string.IsNullOrWhiteSpace(speakerId))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(speakerId => speakerId, StringComparer.Ordinal)
            .ToList();

        SpeakerIds = new ObservableCollection<string>(ordered!);

        var candidate = !string.IsNullOrWhiteSpace(preferredSpeakerId) && SpeakerIds.Contains(preferredSpeakerId)
            ? preferredSpeakerId
            : !string.IsNullOrWhiteSpace(SelectedSpeakerId) && SpeakerIds.Contains(SelectedSpeakerId)
                ? SelectedSpeakerId
                : SpeakerIds.FirstOrDefault();

        SelectedSpeakerId = candidate;
        UpdateSelectedSpeakerDetails(SelectedSpeakerId);
    }

    internal void TrySelectSpeakerForSegment(string? speakerId)
    {
        if (!string.IsNullOrWhiteSpace(speakerId) && SpeakerIds.Contains(speakerId))
            SelectedSpeakerId = speakerId;
    }

    [RelayCommand]
    public async Task AssignSelectedSpeakerVoiceAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedSpeakerId) || string.IsNullOrWhiteSpace(_parent.TtsModelOrVoice))
            return;

        _coordinator.SetSpeakerVoiceAssignment(SelectedSpeakerId, _parent.TtsModelOrVoice);
        _parent.StatusText = $"Assigned {_parent.TtsModelOrVoice} to {SelectedSpeakerId}.";
        UpdateSelectedSpeakerDetails(SelectedSpeakerId);
        await _parent.Preview.RefreshSegmentsAsync();
    }

    [RelayCommand]
    public async Task ClearSelectedSpeakerVoiceAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedSpeakerId))
            return;

        _coordinator.RemoveSpeakerVoiceAssignment(SelectedSpeakerId);
        _parent.StatusText = $"Cleared voice assignment for {SelectedSpeakerId}.";
        UpdateSelectedSpeakerDetails(SelectedSpeakerId);
        await _parent.Preview.RefreshSegmentsAsync();
    }

    public async Task SetReferenceAudioForSelectedSpeakerAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(SelectedSpeakerId) || string.IsNullOrWhiteSpace(path))
            return;

        _coordinator.SetSpeakerReferenceAudioPath(SelectedSpeakerId, path);
        _parent.StatusText = $"Set reference audio for {SelectedSpeakerId}.";
        UpdateSelectedSpeakerDetails(SelectedSpeakerId);
        await _parent.Preview.RefreshSegmentsAsync();
    }

    [RelayCommand]
    public async Task ClearSelectedSpeakerReferenceAudioAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedSpeakerId))
            return;

        _coordinator.RemoveSpeakerReferenceAudioPath(SelectedSpeakerId);
        _parent.StatusText = $"Cleared reference audio for {SelectedSpeakerId}.";
        UpdateSelectedSpeakerDetails(SelectedSpeakerId);
        await _parent.Preview.RefreshSegmentsAsync();
    }

    partial void OnSelectedSpeakerIdChanged(string? value)
    {
        UpdateSelectedSpeakerDetails(value);
    }

    partial void OnDiarizationMinSpeakersChanged(decimal? value)
    {
        if (_parent.IsSynchronizingPipelineSettings)
            return;

        var normalized = NormalizeSpeakerCount(value);
        if (HasInvalidDiarizationSpeakerBounds(normalized, _coordinator.CurrentSettings.DiarizationMaxSpeakers))
        {
            RejectInvalidDiarizationSpeakerBoundsChange(
                nameof(DiarizationMinSpeakers),
                _coordinator.CurrentSettings.DiarizationMinSpeakers);
            return;
        }

        _coordinator.CurrentSettings.DiarizationMinSpeakers = normalized;
        _coordinator.NotifySettingsModified();
    }

    partial void OnDiarizationMaxSpeakersChanged(decimal? value)
    {
        if (_parent.IsSynchronizingPipelineSettings)
            return;

        var normalized = NormalizeSpeakerCount(value);
        if (HasInvalidDiarizationSpeakerBounds(_coordinator.CurrentSettings.DiarizationMinSpeakers, normalized))
        {
            RejectInvalidDiarizationSpeakerBoundsChange(
                nameof(DiarizationMaxSpeakers),
                _coordinator.CurrentSettings.DiarizationMaxSpeakers);
            return;
        }

        _coordinator.CurrentSettings.DiarizationMaxSpeakers = normalized;
        _coordinator.NotifySettingsModified();
    }

    internal void UpdateSelectedSpeakerDetails(string? speakerId)
    {
        if (string.IsNullOrWhiteSpace(speakerId))
        {
            SelectedSpeakerAssignedVoice = string.Empty;
            SelectedSpeakerReferenceAudioPath = string.Empty;
            return;
        }

        var voiceMap = _coordinator.GetSpeakerVoiceAssignments();
        var referenceMap = _coordinator.GetSpeakerReferenceAudioPaths();
        SelectedSpeakerAssignedVoice = voiceMap.TryGetValue(speakerId, out var voice) ? voice : string.Empty;
        SelectedSpeakerReferenceAudioPath = referenceMap.TryGetValue(speakerId, out var path) ? path : string.Empty;
    }

    private string ResolveEnabledDiarizationProvider()
    {
        if (string.Equals(_lastEnabledDiarizationProvider, ProviderNames.SortFormerLocal, StringComparison.Ordinal) ||
            string.Equals(_lastEnabledDiarizationProvider, ProviderNames.WeSpeakerLocal, StringComparison.Ordinal))
        {
            return _lastEnabledDiarizationProvider;
        }

        return ProviderNames.WeSpeakerLocal;
    }

    private static int? NormalizeSpeakerCount(decimal? value)
    {
        if (!value.HasValue)
            return null;

        var rounded = (int)Math.Round(value.Value, MidpointRounding.AwayFromZero);
        return Math.Clamp(rounded, 1, 20);
    }

    private static bool HasInvalidDiarizationSpeakerBounds(int? minSpeakers, int? maxSpeakers) =>
        minSpeakers.HasValue && maxSpeakers.HasValue && minSpeakers.Value > maxSpeakers.Value;

    private void RejectInvalidDiarizationSpeakerBoundsChange(string propertyName, int? previousValue)
    {
        _parent.IsSynchronizingPipelineSettings = true;
        try
        {
            var previousDecimal = previousValue.HasValue ? (decimal?)previousValue.Value : null;
            if (string.Equals(propertyName, nameof(DiarizationMinSpeakers), StringComparison.Ordinal))
                DiarizationMinSpeakers = previousDecimal;
            else
                DiarizationMaxSpeakers = previousDecimal;
        }
        finally
        {
            _parent.IsSynchronizingPipelineSettings = false;
        }

        _parent.StatusText = "Diarization min speakers cannot be greater than max speakers.";
        _parent.ClearStatusErrorDetail();
    }
}
