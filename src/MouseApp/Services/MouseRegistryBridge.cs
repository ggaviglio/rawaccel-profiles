using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MouseApp.Services;

public static class MouseRegistryBridge
{
    // MarkC Windows 7/8/10 no-acceleration curve — X-axis speed breakpoints.
    // Input-speed nodes (mouse counts/polling-tick) for the 5-segment piecewise-linear
    // SmoothMouse curve. Values are exact in 16.16 fixed-point and span the typical range 0–8.
    //
    // For exact 1:1 pointer response at 6/11 speed on Windows 7/8/10 (confirmed by Mark Cranness):
    //   Windows multiplies output by (DPI/150), so to cancel it and get 1:1:
    //   SmoothMouseY[i] / SmoothMouseX[i]  =  150 / DesktopDPI
    // Source: https://donewmouseaccel.blogspot.com/2010/03/markc-windows-7-mouse-acceleration-fix.html
    //
    // YCurve is derived at runtime:  Y[i] = X[i] * (150.0 / dpi) * regResult
    //   regResult = 1.0  →  exact 1:1 (identical to the standard MarkC fix)
    //   regResult = 2.0  →  2× the MarkC baseline sensitivity
    // XCurve is DPI-independent and written unchanged to the registry.
    private static readonly double[] MarkCXCurve =
    {
        0.0,
        0.375,   // 24576  / 65536  (exact in 16.16 fixed-point)
        1.25,    // 81920  / 65536  (exact in 16.16 fixed-point)
        2.5,     // 163840 / 65536  (exact in 16.16 fixed-point)
        8.0      // 524288 / 65536  (exact in 16.16 fixed-point)
    };

    // Windows Pointer Speed multiplier table — index 0 = speed 1, index 9 = speed 10 (1.0×), index 19 = speed 20.
    private static readonly double[] WinSpeedMultipliers =
    {
        0.03125, 0.0625, 0.125, 0.25, 0.375, 0.5, 0.625, 0.75, 0.875, 1.0,
        1.25, 1.5, 1.75, 2.0, 2.25, 2.5, 2.75, 3.0, 3.25, 3.5
    };

    // Win32 constants
    private const uint SPI_SETMOUSE = 0x0004;
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDCHANGE = 0x02;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, int[] pvParam, uint fWinIni);

    /// <summary>
    /// Applies the fine-tune multiplier to the registry curves.
    /// - SmoothMouseXCurve: MarkC fix values (always forced — removes Windows accel, guarantees linear)
    /// - SmoothMouseYCurve: MarkC baseline × regResult (pure sensitivity scaling)
    ///   At regResult=1.0 this is identical to the MarkC fix (exact 1:1 movement).
    ///   At regResult=2.0 the output is 2× the MarkC baseline — genuinely 2× faster.
    /// Values are written as 64-bit fixed-point (16.16) little-endian binary.
    /// </summary>
    public static void ApplyRegistryBridge(double regResult)
    {
        if (double.IsNaN(regResult) || double.IsInfinity(regResult))
            throw new ArgumentException("regResult must be a finite number", nameof(regResult));

        if (!OperatingSystem.IsWindows())
            return; // No-op on non-Windows platforms

        int dpi = GetCurrentLogicalDpi();
        Debug.WriteLine($"[RegistryBridge] ApplyRegistryBridge regResult={regResult:G10} dpi={dpi}");

        // Win7/8/10 formula (Mark Cranness): Windows internally multiplies cursor movement by
        // (DPI/150) and also by the pointer speed multiplier (WinSpeedMult).
        // To get exact 1:1 output for regResult=1.0, both must be cancelled:
        //   PointerMovement = MouseInput × (Y/X) × (DPI/150) × WinSpeedMult
        //   Set Y/X = (150/DPI) / WinSpeedMult  →  PointerMovement = MouseInput × regResult
        // WinSpeedMult is read live from the registry — MouseService writes the profile's pointer
        // speed BEFORE calling this, so the value here is already correct for this profile.
        // Clamp regResult to a small positive floor so the curve is never zeroed (a zero Y curve
        // stops the pointer dead when EPP is ON).
        double winSpeedMult = ReadWindowsPointerSpeedMultiplier();
        double scale = Math.Max(regResult, 0.01) * (150.0 / dpi) / winSpeedMult;
        Debug.WriteLine($"[RegistryBridge] winSpeedMult={winSpeedMult:G6} scale={scale:G10}");
        double[] xCurve = MarkCXCurve;
        var yCurve = new double[MarkCXCurve.Length];
        for (int i = 0; i < MarkCXCurve.Length; i++)
            yCurve[i] = MarkCXCurve[i] * scale;

        // Convert curve to registry binary (64-bit 16.16 fixed-point per entry)
        static byte[] CurveToBinary(double[] curve)
        {
            var buf = new byte[curve.Length * 8];
            for (int i = 0; i < curve.Length; i++)
            {
                // 16.16 fixed-point: value * 65536
                long fixedVal = (long)Math.Round(curve[i] * 65536.0);
                var bytes = BitConverter.GetBytes(fixedVal); // little-endian
                Buffer.BlockCopy(bytes, 0, buf, i * 8, 8);
            }
            return buf;
        }

        var xBytes = CurveToBinary(xCurve);
        var yBytes = CurveToBinary(yCurve);

        // Write to registry
        using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Mouse", writable: true)
                       ?? Registry.CurrentUser.CreateSubKey(@"Control Panel\Mouse", writable: true);

        if (key == null)
            throw new InvalidOperationException("Unable to open or create registry key HKCU\\Control Panel\\Mouse");

        key.SetValue("SmoothMouseXCurve", xBytes, RegistryValueKind.Binary);
        key.SetValue("SmoothMouseYCurve", yBytes, RegistryValueKind.Binary);
        Debug.WriteLine($"[RegistryBridge] Curves written (regResult={regResult:G10})");

        // Broadcast to make Windows re-read the curves live.
        // Read current EPP state from registry so we don't accidentally toggle it.
        bool currentEpp = ReadEnhancePointerPrecision() ?? false;
        int accel = currentEpp ? 1 : 0;
        bool ok = SystemParametersInfo(SPI_SETMOUSE, 0, new int[] { 6, 10, accel }, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
        int err = Marshal.GetLastWin32Error();
        Debug.WriteLine($"[RegistryBridge] SPI_SETMOUSE(accel={accel}) result={ok} Win32Error={err}");
        if (!ok)
            throw new System.ComponentModel.Win32Exception(err, "SystemParametersInfo(SPI_SETMOUSE) failed");
    }

    /// <summary>
    /// Returns the regResult value that Windows will actually apply after 16.16 fixed-point
    /// encoding and decoding.  The tightest anchor is 2.5 (step = 1/163840 ≈ 6.1e-6).
    /// Use this in the proof-of-match display so it reflects real-world precision rather
    /// than the idealised double value.
    /// </summary>
    public static double EncodeRegCurve(double regResult)
    {
        // Quantise regResult to the smallest step representable in 16.16 fixed-point for Y[1].
        // Y[1] base (at regResult=1.0) = MarkCXCurve[1] * (dpi / 150.0).
        //   DPI=96  (100%): Y[1] = 0.375 × (150/96)  = 0.58594  → step ≈ 2.61e-5
        //   DPI=120 (125%): Y[1] = 0.375 × (150/120) = 0.46875  → step ≈ 3.26e-5
        //   DPI=144 (150%): Y[1] = 0.375 × (150/144) = 0.39063  → step ≈ 3.91e-5
        // GetCurrentLogicalDpi() is called so this always matches what ApplyRegistryBridge writes.
        int dpi = GetCurrentLogicalDpi();
        double yBase1 = MarkCXCurve[1] * (150.0 / dpi);
        double step = 1.0 / (yBase1 * 65536.0);
        return Math.Round(regResult / step) * step;
    }

    // ── Environment Awareness helpers ────────────────────────────────────────

    /// <summary>
    /// Reads the current Windows Pointer Speed multiplier from HKCU\Control Panel\Mouse\MouseSensitivity.
    /// The stored value is 1–20 (default 10 = 1.0×). Falls back to 1.0 if the key is missing or invalid.
    /// </summary>
    public static double ReadWindowsPointerSpeedMultiplier()
    {
        if (!OperatingSystem.IsWindows()) return 1.0;
        using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Mouse", writable: false);
        if (key == null) return 1.0;
        var val = key.GetValue("MouseSensitivity") as string;
        if (val == null || !int.TryParse(val, out int speed)) return 1.0;
        int index = Math.Clamp(speed - 1, 0, WinSpeedMultipliers.Length - 1);
        return WinSpeedMultipliers[index];
    }

    private const int LOGPIXELSX = 88;

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    /// <summary>
    /// Returns the current system logical DPI (LOGPIXELSX) via GDI GetDeviceCaps.
    /// Falls back to 96 on non-Windows platforms.
    /// </summary>
    public static int GetCurrentLogicalDpi()
    {
        if (!OperatingSystem.IsWindows()) return 96;
        var hdc = GetDC(IntPtr.Zero);
        try   { return GetDeviceCaps(hdc, LOGPIXELSX); }
        finally { ReleaseDC(IntPtr.Zero, hdc); }
    }

    /// <summary>
    /// Reads HKCU\Control Panel\Mouse\MouseSpeed.
    /// Returns true if MouseSpeed != "0", false if "0", null if the key is missing.
    /// </summary>
    public static bool? ReadEnhancePointerPrecision()
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Mouse", writable: false);
        if (key == null) return null;
        var val = key.GetValue("MouseSpeed") as string;
        if (val == null) return null;
        return val != "0";
    }

    /// <summary>
    /// Writes EPP state to the registry and broadcasts SPI_SETMOUSE to apply immediately.
    /// SPI_SETMOUSE pvParam = { threshold1, threshold2, acceleration }
    /// where acceleration 1 = EPP on, 0 = EPP off.
    /// The registry curve is always written before this is called (ApplyProfileAsync call order),
    /// so there is no transient bad-curve window for either direction.
    /// </summary>
    public static void WriteEnhancePointerPrecision(bool enable)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Mouse", writable: true)
                       ?? Registry.CurrentUser.CreateSubKey(@"Control Panel\Mouse", writable: true);
        if (key == null) return;

        key.SetValue("MouseSpeed",      enable ? "1" : "0",  RegistryValueKind.String);
        key.SetValue("MouseThreshold1", "6",                  RegistryValueKind.String);
        key.SetValue("MouseThreshold2", "10",                 RegistryValueKind.String);

        // Read back to confirm registry write landed.
        var readback = key.GetValue("MouseSpeed") as string;
        Debug.WriteLine($"[EPP] Registry write: enable={enable}, MouseSpeed readback='{readback}'");

        // Broadcast live — acceleration 1 = EPP on, 0 = EPP off.
        int accel = enable ? 1 : 0;
        bool ok = SystemParametersInfo(SPI_SETMOUSE, 0, new int[] { 6, 10, accel }, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
        int err = Marshal.GetLastWin32Error();
        Debug.WriteLine($"[EPP] SPI_SETMOUSE(accel={accel}) result={ok}, Win32Error={err}");
    }
}
