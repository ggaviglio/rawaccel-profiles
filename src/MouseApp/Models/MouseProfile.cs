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
/// Skip   = field is left null; the Default profile's value is inherited at apply-time.
/// </summary>
public enum SyncMode { Auto, Manual, Skip }

public partial class MouseProfile : ObservableObject
{
    [ObservableProperty]
    public partial string ProfileName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? RawAccelSettingsFile { get; set; }

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
    public partial int? TargetScreenHeight { get; set; }

    // Explicit default (unlike RawSyncMode/OSSyncMode/RegSyncMode, which have none): those three
    // have existed since this app's very first commit, so every real profiles.json already has
    // explicit values for them. YXRatioSyncMode is new -- every existing profiles.json predates
    // it, so a missing field must deserialize to Skip, not SyncMode's implicit first-member
    // default (Auto). Caught live: a batch of pre-existing profiles all silently came back as
    // YXRatioSyncMode=Auto after one save, purely from the enum default, before this fix.
    [ObservableProperty]
    public partial SyncMode YXRatioSyncMode { get; set; } = SyncMode.Skip;

    [ObservableProperty]
    public partial double? YXRatioOverride { get; set; }

    [ObservableProperty]
    public partial bool? UseEnhancePointerPrecision { get; set; }

    [ObservableProperty]
    public partial bool IsDefault { get; set; }

    /// <summary>
    /// When false, this profile is excluded from automatic foreground-window switching.
    /// The profile can still be applied manually from the GUI.
    /// Defaults to true so existing profiles are unaffected.
    /// </summary>
    [ObservableProperty]
    public partial bool AutoSwitchEnabled { get; set; } = true;

    [ObservableProperty]
    public partial List<TargetAssociation> TargetAssociations { get; set; } = new();

    /// <summary>Runtime-only: true when this profile is currently applied to hardware.</summary>
    [ObservableProperty]
    [property: System.Text.Json.Serialization.JsonIgnore]
    public partial bool IsActive { get; set; }

    /// <summary>Runtime-only: true when this profile's name is being edited inline.</summary>
    [ObservableProperty]
    [property: System.Text.Json.Serialization.JsonIgnore]
    public partial bool IsEditing { get; set; }
}
