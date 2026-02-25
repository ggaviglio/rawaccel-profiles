using System;

namespace MouseApp.Models;

public class AppSettings
{
    public string RawAccelDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Directory where MouseApp stores its Raw Accel curve templates (.json).
    /// When empty, defaults to a "curves" folder next to the app executable (portable).
    /// </summary>
    public string TemplatesDirectory { get; set; } = string.Empty;

    public bool EnableRegistryBridge { get; set; } = false;
    public bool RunAtStartup { get; set; } = false;
    public bool StartMinimized { get; set; } = false;
}
