using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
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
    public IAsyncRelayCommand AddProfileCommand      { get; }
    public IAsyncRelayCommand DeleteProfileCommand   { get; }
    public IAsyncRelayCommand CloneProfileCommand    { get; }
    public IAsyncRelayCommand MoveProfileUpCommand   { get; }
    public IAsyncRelayCommand MoveProfileDownCommand { get; }
    public IRelayCommand      ApplySelectedCommand   { get; }
    public IAsyncRelayCommand SetAsDefaultCommand    { get; }

    public MainWindowViewModel()
    {
        Profiles = new() { new MouseProfile { ProfileName = "Default", IsDefault = true } };
        SelectedProfile = Profiles[0];

        OpenSettingsCommand = new AsyncRelayCommand(async () =>
        {
            if (_windowService != null && _profileService != null && _storageService != null && _mouseService != null)
                await _windowService.ShowSettingsAsync(_profileService, _storageService, _mouseService);
        });
        AddProfileCommand       = new AsyncRelayCommand(ExecuteAddProfileAsync);
        DeleteProfileCommand    = new AsyncRelayCommand(ExecuteDeleteProfileAsync, CanDeleteProfile);
        CloneProfileCommand     = new AsyncRelayCommand(ExecuteCloneProfileAsync, CanCloneProfile);
        MoveProfileUpCommand    = new AsyncRelayCommand(ExecuteMoveProfileUpAsync, CanMoveProfileUp);
        MoveProfileDownCommand  = new AsyncRelayCommand(ExecuteMoveProfileDownAsync, CanMoveProfileDown);
        ApplySelectedCommand    = new RelayCommand(ExecuteApplySelected, CanApplySelected);
        SetAsDefaultCommand     = new AsyncRelayCommand(ExecuteSetAsDefaultAsync, CanSetAsDefault);
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
        Profiles.CollectionChanged += OnProfilesCollectionChanged;
        foreach (var profile in Profiles)
            profile.PropertyChanged += OnProfilePropertyChanged;

        // LoadAsync has already run before this ctor is called (see App.axaml.cs);
        // select the first profile (Default) so the editor is never blank.
        SelectedProfile = Profiles.FirstOrDefault();

        // "Apply Now" in ProfileEditor should light the active indicator.
        profileEditor.ProfileManuallyApplied += profile =>
            Dispatcher.UIThread.Post(() => ActiveProfile = profile);
    }

    /// <summary>
    /// Keeps the per-profile <see cref="MouseProfile.PropertyChanged"/> subscriptions in step
    /// with the collection, then refreshes the filtered view.
    /// </summary>
    private void OnProfilesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Reset (raised by ProfileService's Profiles.Clear() on reload) reports neither
        // OldItems nor NewItems, so the survivors are re-subscribed defensively instead.
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var profile in Profiles)
            {
                profile.PropertyChanged -= OnProfilePropertyChanged;
                profile.PropertyChanged += OnProfilePropertyChanged;
            }
        }
        else
        {
            if (e.OldItems != null)
                foreach (MouseProfile profile in e.OldItems)
                    profile.PropertyChanged -= OnProfilePropertyChanged;

            if (e.NewItems != null)
                foreach (MouseProfile profile in e.NewItems)
                    profile.PropertyChanged += OnProfilePropertyChanged;
        }

        OnPropertyChanged(nameof(FilteredProfiles));
    }

    /// <summary>
    /// Re-applies the search filter after a rename commits, so a profile renamed out of (or
    /// into) the active search term stops (or starts) showing straight away.
    /// </summary>
    private void OnProfilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(MouseProfile.ProfileName) or nameof(MouseProfile.IsEditing)))
            return;

        // With no search term FilteredProfiles *is* Profiles, so re-raising would swap the
        // ListBox's ItemsSource and drop its selection for nothing.
        if (string.IsNullOrWhiteSpace(_searchText)) return;

        // The inline-rename TextBox writes ProfileName on every keystroke; re-filtering
        // mid-edit would pull the row out from under the caret. Wait for the commit.
        if (sender is MouseProfile { IsEditing: true }) return;

        OnPropertyChanged(nameof(FilteredProfiles));
    }

    public void WireMouseService(IMouseService mouseService)
    {
        _mouseService = mouseService ?? throw new ArgumentNullException(nameof(mouseService));
    }

    /// <summary>
    /// Called when a process match auto-activates a profile.
    /// </summary>
    /// <remarks>
    /// Despite how this reads at the call site, it does <b>not</b> arrive on a background
    /// thread. <see cref="ProcessWatcher"/> installs its <c>SetWinEventHook</c> out-of-context
    /// (<c>Services/ProcessWatcher.cs</c>), so the callback runs on the thread that installed
    /// the hook -- the UI thread, since the watcher is constructed in <c>App.axaml.cs</c>. That
    /// call site also already wraps this in <c>Dispatcher.UIThread.InvokeAsync</c>.
    /// <para>
    /// The <c>Post</c> below is therefore redundant rather than load-bearing. It is kept as
    /// belt-and-braces in case the watcher is ever moved off the UI thread, but do not rely on
    /// this method being safe to call from an arbitrary thread on the strength of that alone.
    /// </para>
    /// </remarks>
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

    private async Task ExecuteSetAsDefaultAsync()
    {
        if (_selectedProfile == null || _profileService == null) return;
        await _profileService.SetDefaultAsync(_selectedProfile);
        _profileEditor?.LoadProfile(_selectedProfile);
        SetAsDefaultCommand.NotifyCanExecuteChanged();
    }

    private bool CanCloneProfile() => _selectedProfile != null;

    private async Task ExecuteCloneProfileAsync()
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

    private async Task ExecuteMoveProfileUpAsync()
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

    private async Task ExecuteMoveProfileDownAsync()
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

    private async Task ExecuteAddProfileAsync()
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

    private async Task ExecuteDeleteProfileAsync()
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
