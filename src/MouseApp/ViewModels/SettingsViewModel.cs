using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using MouseApp.Models;
using MouseApp.Services;

namespace MouseApp.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly ProfileService  _profileService;
    private readonly IStorageService _storageService;

    /// <summary>Raised after Save or Cancel so the window can close itself.</summary>
    public event Action? RequestClose;

    public SettingsViewModel(ProfileService profileService, IStorageService storageService)
    {
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        _storageService = storageService ?? throw new ArgumentNullException(nameof(storageService));

        // Stage a copy so Cancel can discard edits without ever touching the committed settings.
        var source = _profileService.Settings ?? new AppSettings();
        Settings = new AppSettings
        {
            RawAccelDirectory    = source.RawAccelDirectory,
            RawAccelSettingsDirectory   = source.RawAccelSettingsDirectory,
            EnableRegistryBridge = source.EnableRegistryBridge,
            RunAtStartup         = source.RunAtStartup,
            StartMinimized       = source.StartMinimized,
            CloseToTray          = source.CloseToTray,
        };

        // Ensure RunAtStartup reflects actual system state (HKCU Run) rather than last-saved state.
        if (OperatingSystem.IsWindows())
        {
            try
            {
                const string subKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
                using var key = Registry.CurrentUser.OpenSubKey(subKey, false);
                if (key != null)
                {
                    var value = key.GetValue("MouseApp") as string;
                    if (!string.IsNullOrEmpty(value))
                    {
                        // strip surrounding quotes if present
                        if (value.Length >= 2 && value.StartsWith("\"") && value.EndsWith("\""))
                            value = value[1..^1];
                        var procPath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                        Settings.RunAtStartup = !string.IsNullOrEmpty(procPath)
                            && string.Equals(Path.GetFullPath(procPath), Path.GetFullPath(value), StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
            catch
            {
                Settings.RunAtStartup = false;
            }
        }

        SaveCommand   = new RelayCommand(ExecuteSave);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke());

        BrowseRawAccelDirCommand = new AsyncRelayCommand(async () =>
        {
            var path = await _storageService.PickFolderAsync();
            if (!string.IsNullOrEmpty(path))
                RawAccelDirectory = path;
        });

        BrowseRawAccelSettingsDirCommand = new AsyncRelayCommand(async () =>
        {
            var path = await _storageService.PickFolderAsync();
            if (!string.IsNullOrEmpty(path))
                RawAccelSettingsDirectory = path;
        });
    }

    public AppSettings Settings { get; }

    public string RawAccelDirectory
    {
        get => Settings.RawAccelDirectory ?? string.Empty;
        set
        {
            if (value == Settings.RawAccelDirectory) return;
            Settings.RawAccelDirectory = value ?? string.Empty;
            OnPropertyChanged(nameof(RawAccelDirectory));
        }
    }

    public string RawAccelSettingsDirectory
    {
        get => Settings.RawAccelSettingsDirectory ?? string.Empty;
        set
        {
            if (value == Settings.RawAccelSettingsDirectory) return;
            Settings.RawAccelSettingsDirectory = value ?? string.Empty;
            OnPropertyChanged(nameof(RawAccelSettingsDirectory));
        }
    }

    public bool EnableRegistryBridge
    {
        get => Settings.EnableRegistryBridge;
        set
        {
            if (value == Settings.EnableRegistryBridge) return;
            Settings.EnableRegistryBridge = value;
            OnPropertyChanged(nameof(EnableRegistryBridge));
        }
    }

    public bool RunAtStartup
    {
        get => Settings.RunAtStartup;
        set
        {
            if (value == Settings.RunAtStartup) return;
            Settings.RunAtStartup = value;
            OnPropertyChanged(nameof(RunAtStartup));
        }
    }

    public bool StartMinimized
    {
        get => Settings.StartMinimized;
        set
        {
            if (value == Settings.StartMinimized) return;
            Settings.StartMinimized = value;
            OnPropertyChanged(nameof(StartMinimized));
        }
    }

    public bool CloseToTray
    {
        get => Settings.CloseToTray;
        set
        {
            if (value == Settings.CloseToTray) return;
            Settings.CloseToTray = value;
            OnPropertyChanged(nameof(CloseToTray));
        }
    }

    public IAsyncRelayCommand BrowseRawAccelDirCommand  { get; }
    public IAsyncRelayCommand BrowseRawAccelSettingsDirCommand { get; }
    public IRelayCommand      SaveCommand               { get; }
    public IRelayCommand      CancelCommand             { get; }

    private void ExecuteSave()
    {
        var target = _profileService.Settings;
        target.RawAccelDirectory    = Settings.RawAccelDirectory;
        target.RawAccelSettingsDirectory   = Settings.RawAccelSettingsDirectory;
        target.EnableRegistryBridge = Settings.EnableRegistryBridge;
        target.RunAtStartup         = Settings.RunAtStartup;
        target.StartMinimized       = Settings.StartMinimized;
        target.CloseToTray          = Settings.CloseToTray;
        _profileService.Save();

        if (OperatingSystem.IsWindows())
        {
            try { ApplyRunAtStartup(Settings.RunAtStartup); } catch { }
        }

        RequestClose?.Invoke();
    }

    private void ApplyRunAtStartup(bool enable)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            const string subKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
            using var key = Registry.CurrentUser.OpenSubKey(subKey, true) ?? Registry.CurrentUser.CreateSubKey(subKey);
            const string valueName = "MouseApp";
            if (enable)
            {
                var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                if (!string.IsNullOrEmpty(exe))
                {
                    var quoted = $"\"{exe}\"";
                    key.SetValue(valueName, quoted);
                }
            }
            else
            {
                try { key.DeleteValue(valueName, false); } catch { }
            }
        }
        catch
        {
            // best-effort
        }
    }
}
