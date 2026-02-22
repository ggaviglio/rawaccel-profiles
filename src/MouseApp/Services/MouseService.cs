using System;
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
        if (profile == null) throw new ArgumentNullException(nameof(profile));

        var (winSpeed, regResult) = _profileService.CalculateCompensations(profile);

        // Apply registry bridge (fine-tune) if available
        if (regResult.HasValue)
        {
            if (OperatingSystem.IsWindows())
            {
                // If registry bridge requires elevation, ensure we're elevated
                try
                {
                    MouseRegistryBridge.ApplyRegistryBridge(regResult.Value);
                }
                catch
                {
                }
            }
        }

        // Apply discrete Windows pointer speed (coarse) if available
        if (winSpeed.HasValue)
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    // Use SystemParametersInfo to set mouse speed (1..20)
                    var speed = (uint)Math.Clamp(winSpeed.Value, 1, 20);
                    // uiParam is the new mouse speed
                    SystemParametersInfo(SPI_SETMOUSESPEED, speed, IntPtr.Zero, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
                }
                catch
                {
                    // ignore
                }
            }
        }

        // Apply Raw Accel template and sensitivity
        await _rawAccelProvider.ApplyTemplateAsync(profile.RawAccelTemplate, profile.SensMultiplier).ConfigureAwait(false);
    }
}
