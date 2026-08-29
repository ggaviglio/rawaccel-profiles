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

    public bool RunAtStartup { get; set; } = false;
    public bool StartMinimized { get; set; } = false;
    public bool CloseToTray { get; set; } = false;

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

    /// <summary>
    /// Last remembered main-window size (Normal state only -- never the maximized size).
    /// Null means "no saved size yet," in which case MainWindow.axaml's own Width/Height
    /// apply. Position is deliberately not persisted: it's what most invites going stale
    /// after a monitor is unplugged/reconfigured, whereas size degrades gracefully (it's
    /// clamped to the current primary screen's working area on load).
    /// </summary>
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }

    /// <summary>
    /// Whether the main window was maximized (vs. a resizable Normal window) last time it was
    /// closed. Null means "no saved state yet." Deliberately only ever Normal/Maximized --
    /// Minimized is never persisted or restored (see App.axaml.cs), since opening straight into
    /// a minimized window would be confusing.
    /// </summary>
    public bool? WindowMaximized { get; set; }
}
