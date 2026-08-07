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

    /// <summary>
    /// Advanced Profile Editor options, hidden by default so a new user isn't confronted with
    /// power-user fields on first launch. The Auto-Match Environment card's own tooltip already
    /// describes itself as existing only to feed the auto-compensation buttons -- not something
    /// a casual user needs to see, let alone edit, unless they go looking for it.
    /// </summary>
    public bool ShowAutoMatchEnvironment { get; set; } = false;

    /// <summary>Same idea as <see cref="ShowAutoMatchEnvironment"/>, for the EPP Override
    /// (registry curve) field.</summary>
    public bool ShowEppOverride { get; set; } = false;
}
