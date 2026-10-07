using System;

namespace RawAccelProfiles.Models;

public class AppSettings
{
    public string RawAccelDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Directory where RawAccelProfiles stores its Raw Accel settings files (.json).
    /// When empty, defaults to the app executable folder (portable).
    /// </summary>
    public string RawAccelSettingsDirectory { get; set; } = string.Empty;

    public bool RunAtStartup { get; set; } = false;
    public bool StartMinimized { get; set; } = false;
    public bool CloseToTray { get; set; } = false;

    /// <summary>
    /// Re-invokes Raw Accel's writer.exe against the existing settings.json when RawAccelProfiles
    /// launches. Raw Accel's kernel driver holds its configuration in memory only -- a PC
    /// restart wipes it even though Windows Pointer Speed and the registry curve (which live
    /// in the registry) survive -- so without this, Raw Accel silently reverts to its
    /// unconfigured state after every reboot until some other action re-triggers writer.exe.
    /// Defaults to true since that reliability is the entire point of the setting.
    /// </summary>
    public bool ApplyRawAccelSettingsOnStartup { get; set; } = true;

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

    /// <summary>Same idea as <see cref="ShowAutoMatchEnvironment"/>, for the base EPP
    /// (Skip/Enable/Disable) dropdown.</summary>
    public bool ShowEpp { get; set; } = false;

    /// <summary>Same idea as <see cref="ShowEppOverride"/>, for the Y/X Ratio row -- an
    /// additional, independent gate on top of <see cref="ShowAutoMatchEnvironment"/> (both
    /// must be true for the row to actually show/take effect), since the ratio is causally
    /// derived from Screen Width/Height and is meaningless with them hidden. Lets a user who
    /// wants Screen Width/Height visible keep the more niche Y/X Ratio field hidden, the same
    /// way ShowEppOverride is a separate, more specific toggle than ShowAutoMatchEnvironment.</summary>
    public bool ShowYXRatio { get; set; } = false;

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
