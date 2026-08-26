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
    private readonly IMouseService   _mouseService;

    /// <summary>Raised after Save or Cancel so the window can close itself.</summary>
    public event Action? RequestClose;

    public SettingsViewModel(ProfileService profileService, IStorageService storageService, IMouseService mouseService)
    {
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        _storageService = storageService ?? throw new ArgumentNullException(nameof(storageService));
        _mouseService   = mouseService ?? throw new ArgumentNullException(nameof(mouseService));

        // Stage a copy so Cancel can discard edits without ever touching the committed settings.
        var source = _profileService.Settings ?? new AppSettings();
        Settings = new AppSettings
        {
            RawAccelDirectory    = source.RawAccelDirectory,
            RawAccelSettingsDirectory   = source.RawAccelSettingsDirectory,
            RunAtStartup         = source.RunAtStartup,
            StartMinimized       = source.StartMinimized,
            CloseToTray          = source.CloseToTray,
            ShowAutoMatchEnvironment = source.ShowAutoMatchEnvironment,
            ShowEppOverride          = source.ShowEppOverride,
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
        RevertCurveCommand = new AsyncRelayCommand(ExecuteRevertCurveAsync);

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

    public bool ShowAutoMatchEnvironment
    {
        get => Settings.ShowAutoMatchEnvironment;
        set
        {
            if (value == Settings.ShowAutoMatchEnvironment) return;
            Settings.ShowAutoMatchEnvironment = value;
            OnPropertyChanged(nameof(ShowAutoMatchEnvironment));
        }
    }

    public bool ShowEppOverride
    {
        get => Settings.ShowEppOverride;
        set
        {
            if (value == Settings.ShowEppOverride) return;
            Settings.ShowEppOverride = value;
            OnPropertyChanged(nameof(ShowEppOverride));
        }
    }

    public IAsyncRelayCommand BrowseRawAccelDirCommand  { get; }
    public IAsyncRelayCommand BrowseRawAccelSettingsDirCommand { get; }
    public IRelayCommand      SaveCommand               { get; }
    public IRelayCommand      CancelCommand             { get; }
    public IAsyncRelayCommand RevertCurveCommand        { get; }

    private string? _revertCurveStatusMessage;

    /// <summary>Transient confirmation text shown after Reset Mouse Curve; auto-clears itself.</summary>
    public string? RevertCurveStatusMessage
    {
        get => _revertCurveStatusMessage;
        private set
        {
            if (value == _revertCurveStatusMessage) return;
            _revertCurveStatusMessage = value;
            OnPropertyChanged(nameof(RevertCurveStatusMessage));
            OnPropertyChanged(nameof(IsRevertCurveStatusVisible));
        }
    }

    public bool IsRevertCurveStatusVisible => !string.IsNullOrEmpty(_revertCurveStatusMessage);

    private void ExecuteSave()
    {
        var target = _profileService.Settings;
        target.RawAccelDirectory    = Settings.RawAccelDirectory;
        target.RawAccelSettingsDirectory   = Settings.RawAccelSettingsDirectory;
        target.RunAtStartup         = Settings.RunAtStartup;
        target.StartMinimized       = Settings.StartMinimized;
        target.CloseToTray          = Settings.CloseToTray;
        target.ShowAutoMatchEnvironment = Settings.ShowAutoMatchEnvironment;
        target.ShowEppOverride          = Settings.ShowEppOverride;
        _profileService.Save();

        if (OperatingSystem.IsWindows())
        {
            try { ApplyRunAtStartup(Settings.RunAtStartup); } catch { }
        }

        RequestClose?.Invoke();
    }

    /// <summary>
    /// Restores Windows' own stock EPP curve, undoing whatever this app has written to
    /// SmoothMouseXCurve/YCurve. Immediate, unconfirmed hardware action -- same pattern as
    /// "Apply Now" in the Profile Editor -- not staged behind Save/Cancel like the rest of this
    /// window, since it isn't a persisted preference. Must also clear MouseService's cached
    /// last-applied state, or the next profile reselect could be wrongly skipped as a no-op by
    /// the dedup check and leave the reverted curve stuck in place -- see CLAUDE.md's
    /// MouseService dedup-check note for the same class of bug.
    /// Runs on a background thread via Task.Run: RevertToWindowsDefaultCurve ends in the same
    /// SystemParametersInfo(..., SPIF_SENDCHANGE) broadcast ApplyRegistryBridge uses, which
    /// blocks until every top-level window on the system has handled WM_SETTINGCHANGE -- doing
    /// that directly on the UI thread freezes the window (the button stays visually pressed)
    /// until it returns. ApplyProfileAsync already avoids this via ConfigureAwait(false) on an
    /// earlier await; this command has no such earlier await, so it needs Task.Run instead.
    /// No ConfigureAwait(false) on the Task.Run await itself, though -- the status-message
    /// updates below need to run back on the UI thread to be safely observed by bindings.
    /// </summary>
    private async Task ExecuteRevertCurveAsync()
    {
        if (!OperatingSystem.IsWindows())
            return;

        bool success = await Task.Run(() =>
        {
            try
            {
                MouseRegistryBridge.RevertToWindowsDefaultCurve();
                _mouseService.InvalidateAppliedState();
                return true;
            }
            catch
            {
                return false;
            }
        });

        RevertCurveStatusMessage = success ? "Reverted to Windows default." : "Reset failed.";
        await Task.Delay(2500);
        RevertCurveStatusMessage = null;
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
