using System;

namespace MouseApp.Models;

public class AppSettings
{
    public string RawAccelDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Directory where MouseApp stores its Raw Accel settings files (.json).
    /// When empty, defaults to the app executable folder (portable).
    /// </summary>
    public string RawAccelSettingsDirectory { get; set; } = string.Empty;

    public bool EnableRegistryBridge { get; set; } = false;
    public bool RunAtStartup { get; set; } = false;
    public bool StartMinimized { get; set; } = false;
    public bool CloseToTray { get; set; } = true;
}
