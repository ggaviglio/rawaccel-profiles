using System;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using MouseApp.Models;
using MouseApp.Services;

namespace MouseApp.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private ViewModelBase? _currentView;
    private MouseProfile? _selectedProfile;
    private readonly NavigationService? _navigation;
    private readonly ProfileEditorViewModel? _profileEditor;
    private readonly SettingsViewModel? _settingsVm;
    private readonly ProfileService? _profileService;

    public ViewModelBase? CurrentView
    {
        get => _currentView;
        set
        {
            if (value == _currentView) return;
            _currentView = value;
            OnPropertyChanged(nameof(CurrentView));
        }
    }

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
            if (value != null)
            {
                _profileEditor?.LoadProfile(value);
                _navigation?.Navigate(_profileEditor);
            }
        }
    }

    public System.Collections.ObjectModel.ObservableCollection<MouseProfile> Profiles { get; private set; }

    public System.Windows.Input.ICommand OpenSettingsCommand { get; }
    public System.Windows.Input.ICommand OpenProfilesCommand { get; }
    public RelayCommand AddProfileCommand { get; }
    public RelayCommand DeleteProfileCommand { get; }

    public MainWindowViewModel()
    {
        Profiles = new() { new MouseProfile { ProfileName = "Global Default", IsGlobalDefault = true } };
        SelectedProfile = Profiles[0];

        OpenSettingsCommand = new RelayCommand(() => _navigation?.Navigate(_settingsVm));
        OpenProfilesCommand = new RelayCommand(() => _navigation?.Navigate(_profileEditor));
        AddProfileCommand = new RelayCommand(ExecuteAddProfile);
        DeleteProfileCommand = new RelayCommand(ExecuteDeleteProfile, CanDeleteProfile);
    }

    public MainWindowViewModel(NavigationService navigation, ProfileEditorViewModel profileEditor, SettingsViewModel settings, ProfileService profileService)
        : this()
    {
        _navigation = navigation;
        _profileEditor = profileEditor;
        _settingsVm = settings;
        _profileService = profileService;

        // Use the service's collection directly — single source of truth, no duplication.
        Profiles = profileService.Profiles;
        OnPropertyChanged(nameof(Profiles));

        navigation.CurrentChanged += vm => CurrentView = vm;
        CurrentView = profileEditor;

        // LoadAsync has already run before this ctor is called (see App.axaml.cs);
        // select the first profile (Global Default) so the editor is never blank.
        SelectedProfile = Profiles.FirstOrDefault();
    }

    private bool CanDeleteProfile() =>
        _selectedProfile != null && !_selectedProfile.IsGlobalDefault;

    private async void ExecuteAddProfile()
    {
        var newProfile = new MouseProfile
        {
            ProfileName        = $"New Profile {Profiles.Count}",
            SensMultiplier     = 1.0,
            TargetAssociations = new()
        };

        Profiles.Add(newProfile);
        SelectedProfile = newProfile;

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
