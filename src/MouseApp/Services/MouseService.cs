using System;
using System.Linq;
using System.Threading.Tasks;
using MouseApp.Models;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MouseApp.Services;

public interface IMouseService
{
    Task ApplyProfileAsync(MouseProfile profile);
}

public class MouseService : IMouseService
{
    private readonly ProfileService _profileService;
    private readonly RawAccelDriverProvider _rawAccelProvider;
    /// <summary>Last hardware state written — prevents redundant registry/SPI writes when nothing has changed.</summary>
    private (int? winSpeed, double? regResult, string? template, double? sens, bool? epp) _lastAppliedState;

    public MouseService(ProfileService profileService, RawAccelDriverProvider rawAccelProvider)
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

    private static bool IsRunningAsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static void RestartElevated()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var exePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exePath))
            return;

        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = "",
            WorkingDirectory = AppContext.BaseDirectory
        };

        try
        {
            Process.Start(psi);
            Environment.Exit(0);
        }
        catch (Win32Exception)
        {
            // user cancelled UAC
        }
    }

    public async Task ApplyProfileAsync(MouseProfile profile)
    {
        Debug.WriteLine($"[MouseService] ApplyProfileAsync entered for '{profile?.ProfileName}'.");
        if (profile == null) throw new ArgumentNullException(nameof(profile));

        var (winSpeed, regResult) = _profileService.CalculateCompensations(profile);
        var eppValue = profile.UseEnhancePointerPrecision
            ?? _profileService.Profiles.FirstOrDefault(p => p.IsGlobalDefault)?.UseEnhancePointerPrecision;
        var newState = (winSpeed, regResult, profile.RawAccelTemplate, profile.SensMultiplier, eppValue);

        // Dedup: skip hardware writes if effective values haven't changed
        if (newState == _lastAppliedState)
        {
            Debug.WriteLine($"[MouseService] ApplyProfileAsync — skipping, hardware state unchanged for '{profile.ProfileName}'.");
            return;
        }
        _lastAppliedState = newState;
        Debug.WriteLine($"[MouseService] Applying profile '{profile.ProfileName}' to hardware.");

        // Apply registry bridge (fine-tune) if available
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

        // Apply discrete Windows pointer speed (coarse) if available
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

        // Apply Raw Accel template and sensitivity
        await _rawAccelProvider.ApplyTemplateAsync(profile.RawAccelTemplate, profile.SensMultiplier).ConfigureAwait(false);

        // Apply EPP toggle if explicitly set on this profile.
        // Null = do not touch EPP — respect the user's current system setting.
        if (eppValue.HasValue && OperatingSystem.IsWindows())
        {
            try { MouseRegistryBridge.WriteEnhancePointerPrecision(eppValue.Value); }
            catch { }
        }
    }
}
