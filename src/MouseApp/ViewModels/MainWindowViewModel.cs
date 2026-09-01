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
    private readonly ProfileService? _profileService;
    private readonly IWindowService? _windowService;
    private readonly IStorageService? _storageService;

    /// <summary>Injected after construction via <see cref="WireMouseService"/>. Null until wired.</summary>
    private IMouseService? _mouseService;

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
        Profiles = new() { new MouseProfile { ProfileName = "Default", IsDefault = true } };
        SelectedProfile = Profiles[0];

        OpenSettingsCommand = new AsyncRelayCommand(async () =>
        {
            if (_windowService != null && _profileService != null && _storageService != null && _mouseService != null)
                await _windowService.ShowSettingsAsync(_profileService, _storageService, _mouseService);
        });
        AddProfileCommand = new RelayCommand(ExecuteAddProfile);
        DeleteProfileCommand = new RelayCommand(ExecuteDeleteProfile, CanDeleteProfile);
        CloneProfileCommand = new RelayCommand(ExecuteCloneProfile, CanCloneProfile);
        MoveProfileUpCommand = new RelayCommand(ExecuteMoveProfileUp, CanMoveProfileUp);
        MoveProfileDownCommand = new RelayCommand(ExecuteMoveProfileDown, CanMoveProfileDown);
        ApplySelectedCommand = new RelayCommand(ExecuteApplySelected, CanApplySelected);
        SetAsDefaultCommand = new RelayCommand(ExecuteSetAsDefault, CanSetAsDefault);
    }

    public MainWindowViewModel(ProfileEditorViewModel profileEditor, ProfileService profileService, IWindowService windowService, IStorageService storageService)
        : this()
    {
        _profileEditor = profileEditor;
        _profileService = profileService;
        _windowService = windowService;
        _storageService = storageService;

        // Use the service's collection directly — single source of truth, no duplication.
        Profiles = profileService.Profiles;
        OnPropertyChanged(nameof(Profiles));
        Profiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(FilteredProfiles));

        // LoadAsync has already run before this ctor is called (see App.axaml.cs);
        // select the first profile (Default) so the editor is never blank.
        SelectedProfile = Profiles.FirstOrDefault();

        // "Apply Now" in ProfileEditor should light the active indicator.
        profileEditor.ProfileManuallyApplied += profile =>
            Dispatcher.UIThread.Post(() => ActiveProfile = profile);
    }

    public void WireMouseService(IMouseService mouseService)
    {
        _mouseService = mouseService ?? throw new ArgumentNullException(nameof(mouseService));
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

    /// <summary>
    /// Persists <see cref="Profiles"/> to disk. Called from <c>MainWindow.axaml.cs</c>'s inline
    /// rename handlers (<c>ProfileNameEdit_KeyDown</c>/<c>ProfileNameEdit_LostFocus</c>) after a
    /// rename commits -- found live: those handlers only ever flipped <c>IsEditing</c> back off,
    /// never actually saved, so a renamed profile updated in memory (the TwoWay binding already
    /// applied it) but silently reverted the next time anything else triggered a save from
    /// scratch, or was just never written to profiles.json at all. Public specifically so the
    /// View's code-behind has something to call without reaching into the private
    /// <see cref="_profileService"/> field directly.
    /// </summary>
    public async void SaveProfiles()
    {
        if (_profileService == null) return;
        await _profileService.SaveAsync().ConfigureAwait(false);
    }

    private bool CanSetAsDefault() =>
        _selectedProfile != null && !_selectedProfile.IsDefault;

    private async void ExecuteSetAsDefault()
    {
        if (_selectedProfile == null || _profileService == null) return;
        await _profileService.SetDefaultAsync(_selectedProfile);
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
            RawAccelSettingsFile       = _selectedProfile.RawAccelSettingsFile,
            SensMultiplier         = _selectedProfile.SensMultiplier,
            RawSyncMode            = _selectedProfile.RawSyncMode,
            OSSyncMode             = _selectedProfile.OSSyncMode,
            WinPointerSpeed        = _selectedProfile.WinPointerSpeed,
            RegSyncMode            = _selectedProfile.RegSyncMode,
            RegistryCurveOverride  = _selectedProfile.RegistryCurveOverride,
            TargetScreenWidth      = _selectedProfile.TargetScreenWidth,
            TargetScreenHeight     = _selectedProfile.TargetScreenHeight,
            YXRatioSyncMode        = _selectedProfile.YXRatioSyncMode,
            YXRatioOverride        = _selectedProfile.YXRatioOverride,
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
        return index > 0 && !(_selectedProfile.IsDefault && index == 0);
    }

    private async void ExecuteMoveProfileUp()
    {
        var current = _selectedProfile;
        if (current == null || _profileService == null) return;
        var index = Profiles.IndexOf(current);
        if (index <= 0) return;
        if (index == 1 && Profiles[0].IsDefault) return;

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
        _selectedProfile != null && !_selectedProfile.IsDefault;

    private async void ExecuteAddProfile()
    {
        var newProfile = new MouseProfile
        {
            ProfileName        = $"New Profile {Profiles.Count}",
            RawSyncMode        = SyncMode.Skip,
            OSSyncMode         = SyncMode.Skip,
            RegSyncMode        = SyncMode.Skip,
            YXRatioSyncMode    = SyncMode.Skip,
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
        if (_selectedProfile == null || _selectedProfile.IsDefault) return;

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
