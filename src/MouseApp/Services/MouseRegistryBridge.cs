using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MouseApp.Services;

public static class MouseRegistryBridge
{
    // MarkC Windows 7/8/10 no-acceleration curve — X-axis speed breakpoints.
    //
    // The SmoothMouseXCurve and SmoothMouseYCurve registry values define a 5-node piecewise-linear
    // lookup table. When EPP (Enhance Pointer Precision) is enabled, Windows maps raw mouse input
    // through this curve to produce cursor movement. Each node is stored as a 64-bit little-endian
    // value in 16.16 fixed-point format (i.e. multiply by 65536 to get the integer representation).
    //
    // Windows applies three multipliers on top of the curve output:
    //   1. The Y/X slope of the curve itself (what we control)
    //   2. DPI/150  — Windows internally scales by the logical DPI divided by 150
    //   3. WinSpeedMult — the Windows Pointer Speed slider (Control Panel → Mouse)
    //
    // Full formula:
    //   CursorMovement = RawInput × (Y/X) × (DPI/150) × WinSpeedMult
    //
    // MarkC fix (Mark Cranness, 2010): set Y/X = 150/DPI so factors 1 and 2 cancel:
    //   CursorMovement = RawInput × (150/DPI) × (DPI/150) × WinSpeedMult
    //                  = RawInput × WinSpeedMult
    // Source: https://donewmouseaccel.blogspot.com/2010/03/markc-windows-7-mouse-acceleration-fix.html
    //
    // Our extension: also cancel WinSpeedMult by dividing it out of Y/X:
    //   CursorMovement = RawInput × (150/DPI/WinSpeedMult) × (DPI/150) × WinSpeedMult × regResult
    //                  = RawInput × regResult
    //
    // So regResult=1.0 is exact 1:1 raw input regardless of DPI or pointer speed setting.
    // regResult=2.0 is exactly 2× raw input. WinSpeedMult is read live at apply-time.
    //
    // XCurve is the fixed MarkC breakpoint table (DPI-independent, always written as-is).
    // YCurve is computed at runtime: Y[i] = X[i] × (150.0 / DPI) / WinSpeedMult × regResult
    private static readonly double[] MarkCXCurve =
    {
        0.0,
        0.375,   // 24576  / 65536  (exact in 16.16 fixed-point)
        1.25,    // 81920  / 65536  (exact in 16.16 fixed-point)
        2.5,     // 163840 / 65536  (exact in 16.16 fixed-point)
        8.0      // 524288 / 65536  (exact in 16.16 fixed-point)
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
    public static void ApplyRegistryBridge(double regResult, int? profileDpi = null)
    {
        if (double.IsNaN(regResult) || double.IsInfinity(regResult))
            throw new ArgumentException("regResult must be a finite number", nameof(regResult));

        if (!OperatingSystem.IsWindows())
            return; // No-op on non-Windows platforms

        int dpi = profileDpi ?? GetCurrentLogicalDpi();
        Debug.WriteLine($"[RegistryBridge] ApplyRegistryBridge regResult={regResult:G10} dpi={dpi} (source={(profileDpi.HasValue ? "profile" : "live")})");

        // Win7/8/10 formula (Mark Cranness): Windows internally multiplies cursor movement by
        // (DPI/150) and also by the pointer speed multiplier (WinSpeedMult).
        // To get exact 1:1 output for regResult=1.0, both must be cancelled:
        //   PointerMovement = MouseInput × (Y/X) × (DPI/150) × WinSpeedMult
        //   Set Y/X = (150/DPI) / WinSpeedMult  →  PointerMovement = MouseInput × regResult
        // WinSpeedMult is read live from the registry — MouseService writes the profile's pointer
        // speed BEFORE calling this, so the value here is already correct for this profile.
        // No artificial floor on regResult here: QuantizeToFixedPoint1616 (below, per curve node)
        // already guarantees a nonzero regResult never produces a literal-zero curve node — it
        // computes the exact smallest representable step instead of guessing a "safe" constant,
        // so it doesn't need help from a cruder pre-floor that would just destroy real
        // distinguishability between small regResult values for no mathematical reason.
        double winSpeedMult = ReadWindowsPointerSpeedMultiplier();
        double scale = regResult * (150.0 / dpi) / winSpeedMult;
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
                long fixedVal = QuantizeToFixedPoint1616(curve[i]);
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

    // Windows' own stock EPP curve, HKCU\Control Panel\Mouse\SmoothMouseXCurve/YCurve
    // (X: 00,00,00,00,00,00,00,00,15,6e,00,00,00,00,00,00,00,40,01,00,00,00,00,00,29,dc,03,00,00,00,00,00,00,00,28,00,00,00,00,00
    //  Y: 00,00,00,00,00,00,00,00,fd,11,01,00,00,00,00,00,00,24,04,00,00,00,00,00,00,fc,12,00,00,00,00,00,00,c0,bb,01,00,00,00,00).
    // Community-documented and confirmed byte-for-byte identical to a live read of
    // HKEY_USERS\.DEFAULT\Control Panel\Mouse during development. Used only as a fallback --
    // RevertToWindowsDefaultCurve() below reads the live .DEFAULT values first, since that
    // reflects this machine's actual shipped default rather than a value baked into the app.
    private static readonly byte[] DefaultXCurveFallback =
    {
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x15, 0x6e, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x40, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x29, 0xdc, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x28, 0x00, 0x00, 0x00, 0x00, 0x00,
    };

    private static readonly byte[] DefaultYCurveFallback =
    {
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0xfd, 0x11, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x24, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0xfc, 0x12, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0xc0, 0xbb, 0x01, 0x00, 0x00, 0x00, 0x00,
    };

    /// <summary>
    /// Restores Windows' own stock EPP acceleration curve, undoing whatever
    /// <see cref="ApplyRegistryBridge"/> has written. Only the curve is touched -- EPP on/off
    /// state (MouseSpeed/thresholds) is left exactly as the user has it.
    /// </summary>
    public static void RevertToWindowsDefaultCurve()
    {
        if (!OperatingSystem.IsWindows())
            return;

        byte[]? xBytes;
        byte[]? yBytes;
        using (var defaultKey = Registry.Users.OpenSubKey(@".DEFAULT\Control Panel\Mouse", writable: false))
        {
            xBytes = defaultKey?.GetValue("SmoothMouseXCurve") as byte[];
            yBytes = defaultKey?.GetValue("SmoothMouseYCurve") as byte[];
        }
        xBytes ??= DefaultXCurveFallback;
        yBytes ??= DefaultYCurveFallback;

        using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Mouse", writable: true)
                       ?? Registry.CurrentUser.CreateSubKey(@"Control Panel\Mouse", writable: true);

        if (key == null)
            throw new InvalidOperationException("Unable to open or create registry key HKCU\\Control Panel\\Mouse");

        key.SetValue("SmoothMouseXCurve", xBytes, RegistryValueKind.Binary);
        key.SetValue("SmoothMouseYCurve", yBytes, RegistryValueKind.Binary);
        Debug.WriteLine("[RegistryBridge] Reverted to Windows default curve.");

        // Broadcast to make Windows re-read the curves live -- same tail as ApplyRegistryBridge.
        // Read current EPP state so this call doesn't accidentally toggle it.
        bool currentEpp = ReadEnhancePointerPrecision() ?? false;
        int accel = currentEpp ? 1 : 0;
        bool ok = SystemParametersInfo(SPI_SETMOUSE, 0, new int[] { 6, 10, accel }, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
        int err = Marshal.GetLastWin32Error();
        Debug.WriteLine($"[RegistryBridge] SPI_SETMOUSE(accel={accel}) result={ok} Win32Error={err}");
        if (!ok)
            throw new System.ComponentModel.Win32Exception(err, "SystemParametersInfo(SPI_SETMOUSE) failed");
    }

    /// <summary>
    /// Rounds a value to its 16.16 fixed-point long representation, matching exactly what gets
    /// written to the registry for a single curve node. Never lets a genuinely nonzero value
    /// round down to a literal 0 (a zero Y curve stops the pointer dead when EPP is on).
    /// Shared by CurveToBinary (the real write) and EncodeRegCurve (display prediction) so this
    /// floor has exactly one implementation instead of two independently-maintained copies that
    /// could drift apart — see CLAUDE.md's "formula-identical" requirement for this file.
    /// </summary>
    private static long QuantizeToFixedPoint1616(double value)
    {
        // 16.16 fixed-point: value * 65536
        double scaled = value * 65536.0;
        // An unchecked (long) cast doesn't throw on overflow in C# — it silently wraps, which
        // would write a corrupted, wildly wrong curve value to the registry. Fail loudly instead
        // so an extreme regResult never produces garbage hardware state.
        if (scaled > long.MaxValue || scaled < long.MinValue)
            throw new ArgumentOutOfRangeException(nameof(value), $"Curve value {value:G10} would overflow 64-bit fixed-point storage.");
        long fixedVal = (long)Math.Round(scaled);
        if (fixedVal == 0 && value != 0)
            fixedVal = value > 0 ? 1 : -1;
        return fixedVal;
    }

    /// <summary>
    /// Returns the regResult value that Windows will actually apply after 16.16 fixed-point
    /// encoding and decoding, accounting for the current DPI and pointer speed.
    /// Use this in match-accuracy display so it reflects real-world precision rather
    /// than the idealised double value.
    /// </summary>
    public static double EncodeRegCurve(double regResult) => EncodeRegCurve(regResult, out _);

    /// <summary>
    /// Same as <see cref="EncodeRegCurve(double)"/>, but also returns the smallest
    /// distinguishable step in regResult units — the real-world precision floor callers
    /// can use to decide how many digits of a derived display value are actually meaningful.
    /// </summary>
    public static double EncodeRegCurve(double regResult, out double step)
    {
        // The smallest distinguishable step in regResult is determined by the precision of Y[1]
        // in 16.16 fixed-point. Y[1] is the tightest anchor (smallest non-zero node):
        //   Y[1] = MarkCXCurve[1] × (150.0 / DPI) / WinSpeedMult × regResult
        // Stored as fixedY1 = round(Y[1] × 65536), so the step in regResult units is:
        //   step = 1 / (MarkCXCurve[1] × (150.0 / DPI) / WinSpeedMult × 65536)
        //
        // Example at DPI=96, WinSpeedMult=1.0 (speed 10):
        //   Y[1] = 0.375 × (150/96) / 1.0 = 0.58594  →  step ≈ 2.61e-5
        // Example at DPI=96, WinSpeedMult=0.5 (speed 6):
        //   Y[1] = 0.375 × (150/96) / 0.5 = 1.17188  →  step ≈ 1.30e-5
        int dpi = GetCurrentLogicalDpi();
        double winSpeedMult = ReadWindowsPointerSpeedMultiplier();
        double yBase1 = MarkCXCurve[1] * (150.0 / dpi) / winSpeedMult;
        step = 1.0 / (yBase1 * 65536.0);

        // No artificial floor on regResult here — matches ApplyRegistryBridge, which no longer
        // has one either (see its comment). Same shared quantize-and-clamp helper CurveToBinary
        // uses per curve node, applied here to node[1] (the tightest anchor — see comment above),
        // then converted back to regResult units. One implementation for both, so the "never
        // round down to a literal 0" floor can't drift between the display and the real write.
        long fixedVal = QuantizeToFixedPoint1616(yBase1 * regResult);
        return fixedVal / 65536.0 / yBase1;
    }

    // ── Environment Awareness helpers ────────────────────────────────────────

    /// <summary>
    /// Reads the current Windows Pointer Speed multiplier from HKCU\Control Panel\Mouse\MouseSensitivity.
    /// The stored value is 1–20 (default 10 = 1.0×). Falls back to 1.0 if the key is missing or invalid.
    /// Uses <see cref="ProfileService.WinMultipliers"/> — the single source of truth for this table
    /// (see CLAUDE.md) — instead of a second, separately-maintained copy; the two used to be
    /// independent hand-typed arrays that happened to still agree, a real drift risk even though
    /// they were never actually found to have diverged.
    /// </summary>
    public static double ReadWindowsPointerSpeedMultiplier()
    {
        if (!OperatingSystem.IsWindows()) return 1.0;
        using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Mouse", writable: false);
        if (key == null) return 1.0;
        var val = key.GetValue("MouseSensitivity") as string;
        if (val == null || !int.TryParse(val, out int speed)) return 1.0;
        int index = Math.Clamp(speed - 1, 0, ProfileService.WinMultipliers.Length - 1);
        return ProfileService.WinMultipliers[index];
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
