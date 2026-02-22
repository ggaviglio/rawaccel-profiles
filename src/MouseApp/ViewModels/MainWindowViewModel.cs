namespace MouseApp.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private ViewModelBase? _currentView;
    private string? _selectedProfile;
    private readonly Services.NavigationService? _navigation;
    private readonly ProfileEditorViewModel? _profileEditor;
    private readonly SettingsViewModel? _settingsVm;

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
    public string? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (value == _selectedProfile) return;
            _selectedProfile = value;
            OnPropertyChanged(nameof(SelectedProfile));
            if (value != null)
                _navigation?.Navigate(_profileEditor);
        }
    }

    public System.Collections.ObjectModel.ObservableCollection<string> Profiles { get; } = new();

    public System.Windows.Input.ICommand OpenSettingsCommand { get; }
    public System.Windows.Input.ICommand OpenProfilesCommand { get; }

    public MainWindowViewModel()
    {
        Profiles.Add("Global Default");
        OpenSettingsCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(() => _navigation?.Navigate(_settingsVm));
        OpenProfilesCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(() => _navigation?.Navigate(_profileEditor));
    }

    public MainWindowViewModel(Services.NavigationService navigation, ProfileEditorViewModel profileEditor, SettingsViewModel settings)
        : this()
    {
        _navigation = navigation;
        _profileEditor = profileEditor;
        _settingsVm = settings;

        navigation.CurrentChanged += vm => CurrentView = vm;
        CurrentView = profileEditor;
    }
}
