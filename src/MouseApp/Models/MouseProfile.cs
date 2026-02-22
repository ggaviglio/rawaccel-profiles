using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MouseApp.Models;

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

    [ObservableProperty]
    public partial List<string> TargetExecutables { get; set; } = new();
}
