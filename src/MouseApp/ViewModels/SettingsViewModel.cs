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

        BrowseTemplatesDirCommand = new AsyncRelayCommand(async () =>
        {
            var path = await (_storageService?.PickFolderAsync() ?? Task.FromResult<string?>(null));
            if (!string.IsNullOrEmpty(path))
            {
                TemplatesDirectory = path;
                _settingsService?.Save();
            }
        });
        

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

    public string TemplatesDirectory
    {
        get => Settings.TemplatesDirectory ?? string.Empty;
        set
        {
            if (value == Settings.TemplatesDirectory) return;
            Settings.TemplatesDirectory = value ?? string.Empty;
            OnPropertyChanged(nameof(TemplatesDirectory));
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

    public IAsyncRelayCommand BrowseRawAccelDirCommand { get; } = new AsyncRelayCommand(() => Task.CompletedTask);

    public IAsyncRelayCommand BrowseTemplatesDirCommand { get; } = new AsyncRelayCommand(() => Task.CompletedTask);

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

    

    private void OnExternalSettingsChanged()
    {
        var s = _settingsService?.Settings;
        if (s == null) return;
        Settings.RawAccelDirectory = s.RawAccelDirectory;
        Settings.TemplatesDirectory = s.TemplatesDirectory;
        Settings.EnableRegistryBridge = s.EnableRegistryBridge;
        Settings.RunAtStartup = s.RunAtStartup;
        Settings.StartMinimized = s.StartMinimized;
        OnPropertyChanged(nameof(RawAccelDirectory));
        OnPropertyChanged(nameof(TemplatesDirectory));
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
