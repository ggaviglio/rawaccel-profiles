using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MouseApp.Services;

public static class MouseRegistryBridge
{
    // TODO: Replace with correct MarkC XCurve and YCurve values per DPI level.
    // Research required: decode the actual .reg files from the MarkC Windows 10+8+7 fix ZIP
    // (donewmouseaccel.blogspot.com) for 100% (96 DPI), 125% (120 DPI), 150% (144 DPI).
    // XCurve: the 5 X-axis breakpoint values (counts/tick) — always forced to MarkC values.
    // YCurve base: the 5 Y-axis values at regResult=1.0 — must match MarkC exactly so 1.0 = 1:1.
    // At runtime: YCurve[i] = MarkCYBase[i] * regResult.
    // Determine whether the MarkC builder uses a simple Y/X ratio, a DPI formula, or a lookup.
    //
    // PLACEHOLDER — values below are WRONG, do not ship:
    private static readonly double[] MarkCXCurve_TODO = { 0.0, 0.0, 0.0, 0.0, 0.0 }; // WRONG
    private static readonly double[] MarkCYBase_TODO  = { 0.0, 0.0, 0.0, 0.0, 0.0 }; // WRONG

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

        // TODO: Replace this block once correct MarkC values are researched.
        // 1. Look up the correct MarkCXCurve and MarkCYBase for the current DPI (GetCurrentLogicalDpi()).
        // 2. XCurve = MarkCXCurve (forced, unchanged).
        // 3. YCurve[i] = MarkCYBase[i] * regResult.
        //    regResult=1.0 → identical to MarkC fix (1:1 movement).
        //    regResult=2.0 → 2× faster than MarkC baseline.
        // PLACEHOLDER — throws so the feature is clearly broken rather than silently wrong:
        throw new NotImplementedException(
            "[RegistryBridge] MarkC curve values not yet implemented. See TODO in MouseRegistryBridge.cs.");
        #pragma warning disable CS0162
        int dpi = GetCurrentLogicalDpi();
        Debug.WriteLine($"[RegistryBridge] ApplyRegistryBridge regResult={regResult:G10} dpi={dpi}");
        double[] xCurve = MarkCXCurve_TODO;
        var yCurve = new double[MarkCXCurve_TODO.Length];
        for (int i = 0; i < MarkCXCurve_TODO.Length; i++)
            yCurve[i] = MarkCYBase_TODO[i] * regResult;
        #pragma warning restore CS0162

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
        // TODO: Update once correct MarkC YBase values are known.
        // Precision step = 1 / (MarkCYBase[1] × 65536).
        // PLACEHOLDER — returns regResult unchanged until real values are filled in.
        return regResult;
    }

    // ── Environment Awareness helpers ────────────────────────────────────────

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
