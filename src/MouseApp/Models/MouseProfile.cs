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

/// <summary>
/// Sync behaviour for a hardware setting (raw sens, Win Pointer Speed, Registry Bridge).
/// Auto   = value is computed automatically from environment compensation math.
/// Manual = value is set explicitly by the user.
/// Skip   = field is left null; the Global Default's value is inherited at apply-time.
/// </summary>
public enum SyncMode { Auto, Manual, Skip }

public partial class MouseProfile : ObservableObject
{
    [ObservableProperty]
    public partial string ProfileName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? RawAccelTemplate { get; set; }

    [ObservableProperty]
    public partial double? SensMultiplier { get; set; }

    [ObservableProperty]
    public partial SyncMode RawSyncMode { get; set; }

    [ObservableProperty]
    public partial SyncMode OSSyncMode { get; set; }

    [ObservableProperty]
    public partial int? WinPointerSpeed { get; set; }

    [ObservableProperty]
    public partial SyncMode RegSyncMode { get; set; }

    [ObservableProperty]
    public partial double? RegistryCurveOverride { get; set; }

    [ObservableProperty]
    public partial int? TargetScreenWidth { get; set; }

    [ObservableProperty]
    public partial int? TargetLogicalDpi { get; set; }

    [ObservableProperty]
    public partial bool? UseEnhancePointerPrecision { get; set; }

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
