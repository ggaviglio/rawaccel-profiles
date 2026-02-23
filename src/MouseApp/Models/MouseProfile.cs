using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MouseApp.Models;

public enum TargetMatchType
{
    ExeName,
    FullPath,
    WindowClass,
    WindowTitle
}

public sealed class TargetAssociation
{
    public TargetMatchType MatchType { get; set; }
    public string Value { get; set; } = string.Empty;

    public string TypeLabel => MatchType switch
    {
        TargetMatchType.ExeName     => "EXE",
        TargetMatchType.FullPath    => "PATH",
        TargetMatchType.WindowClass => "CLASS",
        TargetMatchType.WindowTitle => "TITLE",
        _                           => "?"
    };
}

public partial class MouseProfile : ObservableObject
{
    [ObservableProperty]
    public partial string ProfileName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? RawAccelTemplate { get; set; }

    [ObservableProperty]
    public partial double? SensMultiplier { get; set; }

    [ObservableProperty]
    public partial bool UseAutoOSSync { get; set; }

    [ObservableProperty]
    public partial int? WinPointerSpeed { get; set; }

    [ObservableProperty]
    public partial bool UseAutoRegSync { get; set; }

    [ObservableProperty]
    public partial double? RegistryCurveOverride { get; set; }

    [ObservableProperty]
    public partial bool IsGlobalDefault { get; set; }

    /// <summary>
    /// When false, this profile is excluded from automatic foreground-window switching.
    /// The profile can still be applied manually from the GUI.
    /// Defaults to true so existing profiles are unaffected.
    /// </summary>
    [ObservableProperty]
    public partial bool AutoSwitchEnabled { get; set; } = true;

    [ObservableProperty]
    public partial List<TargetAssociation> TargetAssociations { get; set; } = new();
}
