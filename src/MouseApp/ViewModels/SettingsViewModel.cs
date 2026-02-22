using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Win32;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MouseApp.Models;
using MouseApp.Services;

namespace MouseApp.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService? _settingsService = null;
    private readonly IStorageService? _storageService = null;

    public SettingsViewModel(SettingsService settingsService, IStorageService storageService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _storageService = storageService ?? throw new ArgumentNullException(nameof(storageService));
        Settings = _settingsService.Settings ?? new AppSettings();

        // Ensure RunAtStartup reflects system state on startup (HKCU Run)
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
                        // Compare to current process path
                        var procPath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                        if (!string.IsNullOrEmpty(procPath) && string.Equals(Path.GetFullPath(procPath), Path.GetFullPath(value), StringComparison.OrdinalIgnoreCase))
                        {
                            Settings.RunAtStartup = true;
                        }
                        else
                        {
                            Settings.RunAtStartup = false;
                        }
                    }
                }
            }
            catch
            {
                Settings.RunAtStartup = false;
            }
        }

        RefreshAdminStatus();
        // Commands
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        BrowseRawAccelDirCommand = new AsyncRelayCommand(async () =>
        {
            var path = await (_storageService?.PickFolderAsync() ?? Task.FromResult<string?>(null));
            if (!string.IsNullOrEmpty(path))
            {
                RawAccelDirectory = path;
                _settingsService?.Save();
            }
        });

        BrowseWriterExeCommand = new AsyncRelayCommand(async () =>
        {
            var path = await (_storageService?.PickFileAsync("Select writer.exe") ?? Task.FromResult<string?>(null));
            if (!string.IsNullOrEmpty(path))
            {
                WriterExePath = path;
                _settingsService?.Save();
            }
        });
        RefreshAdminStatusCommand = new RelayCommand(() => RefreshAdminStatus());

        // Listen for external changes
        _settingsService.SettingsChanged += (_, __) => OnExternalSettingsChanged();
    }

    public AppSettings Settings { get; } = new AppSettings();

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

    public string WriterExePath
    {
        get => Settings.WriterExePath ?? string.Empty;
        set
        {
            if (value == Settings.WriterExePath) return;
            Settings.WriterExePath = value ?? string.Empty;
            OnPropertyChanged(nameof(WriterExePath));
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

    public bool IsAdmin { get; private set; }

    public string AdminStatus => IsAdmin ? "Running as Admin: Yes" : "Running as Admin: No";

    public IAsyncRelayCommand BrowseRawAccelDirCommand { get; } = new AsyncRelayCommand(() => Task.CompletedTask);

    public IAsyncRelayCommand BrowseWriterExeCommand { get; } = new AsyncRelayCommand(() => Task.CompletedTask);

    public IAsyncRelayCommand SaveCommand { get; } = new AsyncRelayCommand(() => Task.CompletedTask);

    private Task SaveAsync()
    {
        _settingsService?.Save();

        if (OperatingSystem.IsWindows())
        {
            try { ApplyRunAtStartup(Settings.RunAtStartup); } catch { }
        }

        return Task.CompletedTask;
    }

    public IRelayCommand RefreshAdminStatusCommand { get; } = new RelayCommand(() => { });
    
    private Task RefreshAdminStatusAsync()
    {
        RefreshAdminStatus();
        return Task.CompletedTask;
    }

    private void RefreshAdminStatus()
    {
        if (!OperatingSystem.IsWindows())
        {
            IsAdmin = false;
        }
        else
        {
            try
            {
                var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                IsAdmin = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                IsAdmin = false;
            }
        }
        OnPropertyChanged(nameof(IsAdmin));
        OnPropertyChanged(nameof(AdminStatus));
    }

    private void OnExternalSettingsChanged()
    {
        var s = _settingsService?.Settings;
        if (s == null) return;
        Settings.RawAccelDirectory = s.RawAccelDirectory;
        Settings.WriterExePath = s.WriterExePath;
        Settings.EnableRegistryBridge = s.EnableRegistryBridge;
        Settings.RunAtStartup = s.RunAtStartup;
        Settings.StartMinimized = s.StartMinimized;
        OnPropertyChanged(nameof(RawAccelDirectory));
        OnPropertyChanged(nameof(WriterExePath));
        OnPropertyChanged(nameof(EnableRegistryBridge));
        OnPropertyChanged(nameof(RunAtStartup));
        OnPropertyChanged(nameof(StartMinimized));
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

    private Window? GetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow;
        return null;
    }

    public SettingsViewModel()
    {
        // parameterless for designer support
    }

}
