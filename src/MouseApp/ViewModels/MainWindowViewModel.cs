using System;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using MouseApp.Models;
using MouseApp.Services;

namespace MouseApp.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private MouseProfile? _selectedProfile;
    private MouseProfile? _activeProfile;
    private readonly ProfileEditorViewModel? _profileEditor;
    private readonly SettingsViewModel? _settingsVm;
    private readonly ProfileService? _profileService;
    private readonly IWindowService? _windowService;

    /// <summary>The Profile Editor, always shown in the main content area.</summary>
    public ProfileEditorViewModel? ProfileEditor => _profileEditor;

    /// <summary>
    /// Bound to the sidebar ListBox SelectedItem; navigates to the ProfileEditor on change.
    /// </summary>
    public MouseProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (value == _selectedProfile) return;
            _selectedProfile = value;
            OnPropertyChanged(nameof(SelectedProfile));
            DeleteProfileCommand?.NotifyCanExecuteChanged();
            CloneProfileCommand?.NotifyCanExecuteChanged();
            MoveProfileUpCommand?.NotifyCanExecuteChanged();
            MoveProfileDownCommand?.NotifyCanExecuteChanged();
            ApplySelectedCommand?.NotifyCanExecuteChanged();
            SetAsDefaultCommand?.NotifyCanExecuteChanged();
            if (value != null)
                _profileEditor?.LoadProfile(value);
        }
    }

    private void RestoreSelectedProfile(MouseProfile? profile)
    {
        if (profile == null) return;
        if (!Profiles.Contains(profile)) return;

        if (ReferenceEquals(_selectedProfile, profile))
        {
            OnPropertyChanged(nameof(SelectedProfile));
            DeleteProfileCommand?.NotifyCanExecuteChanged();
            CloneProfileCommand?.NotifyCanExecuteChanged();
            MoveProfileUpCommand?.NotifyCanExecuteChanged();
            MoveProfileDownCommand?.NotifyCanExecuteChanged();
            ApplySelectedCommand?.NotifyCanExecuteChanged();
            SetAsDefaultCommand?.NotifyCanExecuteChanged();
        }
        else
        {
            SelectedProfile = profile;
        }
    }

    /// <summary>
    /// The profile currently applied to hardware by ProcessWatcher.
    /// Null means no profile has been applied yet this session.
    /// </summary>
    public MouseProfile? ActiveProfile
    {
        get => _activeProfile;
        set
        {
            if (value == _activeProfile) return;
            if (_activeProfile != null) _activeProfile.IsActive = false;
            _activeProfile = value;
            if (_activeProfile != null) _activeProfile.IsActive = true;
            OnPropertyChanged(nameof(ActiveProfile));
        }
    }

    public System.Collections.ObjectModel.ObservableCollection<MouseProfile> Profiles { get; private set; }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (value == _searchText) return;
            _searchText = value ?? string.Empty;
            OnPropertyChanged(nameof(SearchText));
            OnPropertyChanged(nameof(FilteredProfiles));
        }
    }

    public System.Collections.Generic.IEnumerable<MouseProfile> FilteredProfiles =>
        string.IsNullOrWhiteSpace(_searchText)
            ? Profiles
            : Profiles.Where(p => p.ProfileName?.Contains(_searchText, StringComparison.OrdinalIgnoreCase) == true);

    public System.Windows.Input.ICommand OpenSettingsCommand { get; }
    public RelayCommand AddProfileCommand { get; }
    public RelayCommand DeleteProfileCommand { get; }
    public RelayCommand CloneProfileCommand { get; }
    public RelayCommand MoveProfileUpCommand { get; }
    public RelayCommand MoveProfileDownCommand { get; }
    public RelayCommand ApplySelectedCommand { get; }
    public RelayCommand SetAsDefaultCommand { get; }

    public MainWindowViewModel()
    {
        Profiles = new() { new MouseProfile { ProfileName = "Global Default", IsGlobalDefault = true } };
        SelectedProfile = Profiles[0];

        OpenSettingsCommand = new AsyncRelayCommand(async () =>
        {
            if (_windowService != null && _settingsVm != null)
                await _windowService.ShowSettingsAsync(_settingsVm);
        });
        AddProfileCommand = new RelayCommand(ExecuteAddProfile);
        DeleteProfileCommand = new RelayCommand(ExecuteDeleteProfile, CanDeleteProfile);
        CloneProfileCommand = new RelayCommand(ExecuteCloneProfile, CanCloneProfile);
        MoveProfileUpCommand = new RelayCommand(ExecuteMoveProfileUp, CanMoveProfileUp);
        MoveProfileDownCommand = new RelayCommand(ExecuteMoveProfileDown, CanMoveProfileDown);
        ApplySelectedCommand = new RelayCommand(ExecuteApplySelected, CanApplySelected);
        SetAsDefaultCommand = new RelayCommand(ExecuteSetAsDefault, CanSetAsDefault);
    }

    public MainWindowViewModel(ProfileEditorViewModel profileEditor, SettingsViewModel settings, ProfileService profileService, IWindowService windowService)
        : this()
    {
        _profileEditor = profileEditor;
        _settingsVm = settings;
        _profileService = profileService;
        _windowService = windowService;

        // Use the service's collection directly — single source of truth, no duplication.
        Profiles = profileService.Profiles;
        OnPropertyChanged(nameof(Profiles));
        Profiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(FilteredProfiles));

        // LoadAsync has already run before this ctor is called (see App.axaml.cs);
        // select the first profile (Global Default) so the editor is never blank.
        SelectedProfile = Profiles.FirstOrDefault();

        // "Apply Now" in ProfileEditor should light the active indicator.
        profileEditor.ProfileManuallyApplied += profile =>
            Dispatcher.UIThread.Post(() => ActiveProfile = profile);
    }

    /// <summary>
    /// Called by <see cref="ProcessWatcher"/> (background thread) when a process match
    /// auto-activates a profile.  Marshals back to the UI thread before updating the
    /// sidebar selection so Avalonia bindings fire correctly.
    /// </summary>
    public void OnExternalProfileActivated(MouseProfile profile)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ActiveProfile = profile;
            SelectedProfile = profile;
        });
    }

    private bool CanApplySelected() => _selectedProfile != null;

    private void ExecuteApplySelected()
    {
        if (_profileEditor?.ApplyCommand is { } cmd && cmd.CanExecute(null))
            cmd.Execute(null);
    }

    private bool CanSetAsDefault() =>
        _selectedProfile != null && !_selectedProfile.IsGlobalDefault;

    private async void ExecuteSetAsDefault()
    {
        if (_selectedProfile == null || _profileService == null) return;
        await _profileService.SetGlobalDefaultAsync(_selectedProfile);
        _profileEditor?.LoadProfile(_selectedProfile);
        SetAsDefaultCommand.NotifyCanExecuteChanged();
    }

    private bool CanCloneProfile() => _selectedProfile != null;

    private async void ExecuteCloneProfile()
    {
        if (_selectedProfile == null || _profileService == null) return;

        var baseName = _selectedProfile.ProfileName;
        var cloneName = $"{baseName} Copy";
        var suffix = 1;
        while (Profiles.Any(p => p.ProfileName == cloneName))
            cloneName = $"{baseName} Copy {suffix++}";

        var clone = new MouseProfile
        {
            ProfileName            = cloneName,
            RawAccelTemplate       = _selectedProfile.RawAccelTemplate,
            SensMultiplier         = _selectedProfile.SensMultiplier,
            RawSyncMode            = _selectedProfile.RawSyncMode,
            OSSyncMode             = _selectedProfile.OSSyncMode,
            WinPointerSpeed        = _selectedProfile.WinPointerSpeed,
            RegSyncMode            = _selectedProfile.RegSyncMode,
            RegistryCurveOverride  = _selectedProfile.RegistryCurveOverride,
            TargetScreenWidth      = _selectedProfile.TargetScreenWidth,
            TargetLogicalDpi       = _selectedProfile.TargetLogicalDpi,
            UseEnhancePointerPrecision = _selectedProfile.UseEnhancePointerPrecision,
            AutoSwitchEnabled      = _selectedProfile.AutoSwitchEnabled,
            TargetAssociations     = _selectedProfile.TargetAssociations
                .Select(t => new TargetAssociation { MatchType = t.MatchType, Value = t.Value })
                .ToList(),
            IsEditing              = true
        };

        var index = Profiles.IndexOf(_selectedProfile);
        Profiles.Insert(index + 1, clone);
        SelectedProfile = clone;

        await _profileService.SaveAsync().ConfigureAwait(false);
    }

    private bool CanMoveProfileUp()
    {
        if (_selectedProfile == null) return false;
        var index = Profiles.IndexOf(_selectedProfile);
        return index > 0 && !(_selectedProfile.IsGlobalDefault && index == 0);
    }

    private async void ExecuteMoveProfileUp()
    {
        var current = _selectedProfile;
        if (current == null || _profileService == null) return;
        var index = Profiles.IndexOf(current);
        if (index <= 0) return;
        if (index == 1 && Profiles[0].IsGlobalDefault) return;

        Profiles.Move(index, index - 1);
        RestoreSelectedProfile(current);
        await _profileService.SaveAsync().ConfigureAwait(false);
    }

    private bool CanMoveProfileDown()
    {
        if (_selectedProfile == null) return false;
        var index = Profiles.IndexOf(_selectedProfile);
        return index >= 0 && index < Profiles.Count - 1;
    }

    private async void ExecuteMoveProfileDown()
    {
        var current = _selectedProfile;
        if (current == null || _profileService == null) return;
        var index = Profiles.IndexOf(current);
        if (index < 0 || index >= Profiles.Count - 1) return;

        Profiles.Move(index, index + 1);
        RestoreSelectedProfile(current);
        await _profileService.SaveAsync().ConfigureAwait(false);
    }

    private bool CanDeleteProfile() =>
        _selectedProfile != null && !_selectedProfile.IsGlobalDefault;

    private async void ExecuteAddProfile()
    {
        var newProfile = new MouseProfile
        {
            ProfileName        = $"New Profile {Profiles.Count}",
            RawSyncMode        = SyncMode.Skip,
            OSSyncMode         = SyncMode.Skip,
            RegSyncMode        = SyncMode.Skip,
            TargetAssociations = new()
        };

        Profiles.Add(newProfile);
        SelectedProfile = newProfile;
        newProfile.IsEditing = true;

        if (_profileService != null)
            await _profileService.SaveAsync().ConfigureAwait(false);
    }

    private async void ExecuteDeleteProfile()
    {
        if (_selectedProfile == null || _selectedProfile.IsGlobalDefault) return;

        var toRemove = _selectedProfile;
        var idx = Profiles.IndexOf(toRemove);

        Profiles.Remove(toRemove);

        SelectedProfile = Profiles.Count > 0
            ? Profiles[Math.Max(0, idx - 1)]
            : null;

        if (_profileService != null)
            await _profileService.SaveAsync().ConfigureAwait(false);
    }
}
