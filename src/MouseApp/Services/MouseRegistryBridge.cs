using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MouseApp.Services;

public static class MouseRegistryBridge
{
    // Linear base anchor points
    private static readonly double[] LinearBase = { 0.0, 2.5, 5.0, 10.0, 20.0 };

    // Win32 constants
    private const uint SPI_SETMOUSE = 0x0004;
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDCHANGE = 0x02;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, int[] pvParam, uint fWinIni);

    /// <summary>
    /// Applies the fine-tune multiplier to the registry curves.
    /// - SmoothMouseXCurve: LinearBase (unchanged)
    /// - SmoothMouseYCurve: LinearBase * regResult
    /// Values are written as 64-bit fixed-point (16.16) little-endian binary.
    /// </summary>
    public static void ApplyRegistryBridge(double regResult)
    {
        if (double.IsNaN(regResult) || double.IsInfinity(regResult))
            throw new ArgumentException("regResult must be a finite number", nameof(regResult));

        if (!OperatingSystem.IsWindows())
            return; // No-op on non-Windows platforms

        // Build X and Y curves
        var xCurve = LinearBase;
        var yCurve = new double[LinearBase.Length];
        for (int i = 0; i < LinearBase.Length; i++)
            yCurve[i] = LinearBase[i] * regResult;

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

        // Broadcast system parameter change — pass mouse threshold array {threshold1, threshold2, acceleration}
        bool ok = SystemParametersInfo(SPI_SETMOUSE, 0, new int[] { 6, 10, 0 }, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
        if (!ok)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SystemParametersInfo(SPI_SETMOUSE) failed");
    }

    /// <summary>
    /// Returns the regResult value that Windows will actually apply after 16.16 fixed-point
    /// encoding and decoding.  The tightest anchor is 2.5 (step = 1/163840 ≈ 6.1e-6).
    /// Use this in the proof-of-match display so it reflects real-world precision rather
    /// than the idealised double value.
    /// </summary>
    public static double EncodeRegCurve(double regResult)
    {
        // Tightest non-zero anchor: 2.5
        const double tightestAnchor = 2.5;
        long fixedVal = (long)Math.Round(tightestAnchor * regResult * 65536.0);
        return fixedVal / (tightestAnchor * 65536.0);
    }
}
