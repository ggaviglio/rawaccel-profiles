using System;
using System.Linq;
using System.Threading.Tasks;
using RawAccelProfiles.Models;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RawAccelProfiles.Services;

public class MouseService : IMouseService
{
    private readonly ProfileService _profileService;
    private readonly RawAccelService _rawAccelProvider;
    /// <summary>Last hardware state written — prevents redundant registry/SPI writes when nothing has changed.</summary>
    private (int? winSpeed, double? regResult, string? settingsFile, double? sens, bool? epp) _lastAppliedState;

    public MouseService(ProfileService profileService, RawAccelService rawAccelProvider)
    {
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        _rawAccelProvider = rawAccelProvider ?? throw new ArgumentNullException(nameof(rawAccelProvider));
    }

    // P/Invoke for setting mouse speed
    private const uint SPI_SETMOUSESPEED = 0x0071;
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDCHANGE = 0x02;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    public void InvalidateAppliedState() => _lastAppliedState = default;

    public async Task ApplyProfileAsync(MouseProfile profile)
    {
        Debug.WriteLine($"[MouseService] ApplyProfileAsync entered for '{profile?.ProfileName}'.");
        if (profile == null) throw new ArgumentNullException(nameof(profile));

        var (winSpeed, regResult) = _profileService.CalculateCompensations(profile);
        // Routed through GetEffectiveUseEnhancePointerPrecision (gated on Settings.ShowEpp)
        // instead of reading UseEnhancePointerPrecision directly -- found live via audit: this
        // was the one field of the four Show*-gated options (Width/Height/RegistryCurveOverride/
        // EPP) that had its Settings toggle wired to the ViewModel's display only, never to the
        // actual hardware-apply path, so hiding EPP from Settings never stopped a previously-set
        // value from still being read and written to the registry on every apply.
        var eppValue = _profileService.GetEffectiveUseEnhancePointerPrecision(profile)
            ?? _profileService.GetEffectiveUseEnhancePointerPrecision(_profileService.Profiles.FirstOrDefault(p => p.IsDefault));
        var newState = (winSpeed, regResult, profile.RawAccelSettingsFile, profile.SensMultiplier, eppValue);

        // Dedup: skip hardware writes if effective values haven't changed
        if (newState == _lastAppliedState)
        {
            Debug.WriteLine($"[MouseService] ApplyProfileAsync — skipping, hardware state unchanged for '{profile.ProfileName}'.");
            return;
        }
        _lastAppliedState = newState;
        Debug.WriteLine($"[MouseService] Applying profile '{profile.ProfileName}' to hardware.");

        // Apply the Raw Accel settings file and sensitivity first.
        await _rawAccelProvider.ApplySettingsFileAsync(profile.RawAccelSettingsFile, profile.SensMultiplier).ConfigureAwait(false);

        // Apply discrete Windows pointer speed (coarse) FIRST so that ApplyRegistryBridge reads
        // the correct live value from the registry when it divides out the pointer speed multiplier.
        // If winSpeed is null (profile skips it), the user's existing registry value is preserved
        // and the bridge will correctly cancel whatever speed is currently active.
        if (winSpeed.HasValue)
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    // SPI_SETMOUSESPEED: speed goes in pvParam as an integer value, uiParam is ignored.
                    var speed = Math.Clamp(winSpeed.Value, 1, 20);
                    Debug.WriteLine($"[MouseService] Setting WinPointerSpeed = {speed}");
                    bool ok = SystemParametersInfo(SPI_SETMOUSESPEED, 0, new IntPtr(speed), SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
                    Debug.WriteLine($"[MouseService] SPI_SETMOUSESPEED result = {ok}");
                }
                catch
                {
                    // ignore
                }
            }
        }

        // Apply registry bridge (fine-tune) — runs after pointer speed is committed so the live
        // registry value it reads is already the correct one for this profile.
        if (regResult.HasValue && !double.IsNaN(regResult.Value) && !double.IsInfinity(regResult.Value))
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    Debug.WriteLine($"[MouseService] Calling ApplyRegistryBridge regResult={regResult.Value:G10}");
                    MouseRegistryBridge.ApplyRegistryBridge(regResult.Value);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MouseService] ApplyRegistryBridge threw: {ex.Message}");
                }
            }
        }
        else
        {
            Debug.WriteLine($"[MouseService] Registry bridge skipped — regResult={regResult}");
        }

        // Apply EPP toggle if explicitly set on this profile.
        // Null = do not touch EPP — respect the user's current system setting.
        if (eppValue.HasValue && OperatingSystem.IsWindows())
        {
            try { MouseRegistryBridge.WriteEnhancePointerPrecision(eppValue.Value); }
            catch { }
        }
    }
}
