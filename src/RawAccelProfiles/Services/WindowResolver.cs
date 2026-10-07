using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using RawAccelProfiles.Models;

namespace RawAccelProfiles.Services;

/// <summary>
/// Centralised Win32 window-resolution utilities shared by
/// <see cref="ProcessWatcher"/> and <c>TargetManagerViewModel</c>.
/// Single source of truth for: noise-class filter, attribute resolution, profile matching.
/// </summary>
public static class WindowResolver
{
    // ── P/Invoke ──────────────────────────────────────────────────────────────

    public  delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public  static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public  static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public  static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, EntryPoint = "GetClassName")]
    private static extern int GetClassNameNative(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Auto, EntryPoint = "GetWindowText")]
    private static extern int GetWindowTextNative(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    // ── Convenience wrappers ──────────────────────────────────────────────────

    public static string GetClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassNameNative(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string GetWindowTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        GetWindowTextNative(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    // ── Noise filter ──────────────────────────────────────────────────────────

    /// <summary>
    /// Windows shell / compositor internals that will never match a user profile.
    /// Checked with a cheap <c>GetClassName</c> call before any process-open cost.
    /// Kept in one place so ProcessWatcher and TargetManagerViewModel are always in sync.
    /// </summary>
    public static readonly HashSet<string> SystemNoiseClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ForegroundStaging",                   // focus-transition staging window
        "XamlExplorerHostIslandWindow",         // alt-tab / Task View XAML island
        "Shell_TrayWnd",                        // taskbar
        "Shell_SecondaryTrayWnd",               // taskbar on secondary monitor
        "TopLevelWindowForOverflowXamlIsland",  // system-tray overflow
        "NotifyIconOverflowWindow",             // legacy system-tray overflow
        "WorkerW",                              // desktop worker window
        "Progman",                              // program manager / desktop
        "DummyDWMListenerWindow",               // DWM internal
        "IME",                                  // input method editor
        "MSCTFIME UI",                          // text-framework IME
        "ThumbnailDeviceHelperWnd",             // Explorer thumbnail cache helper
        "EdgeUiInputTopWndClass",               // Explorer edge-swipe / input host helper
    };

    // ── Resolved window snapshot ──────────────────────────────────────────────

    /// <summary>All attributes needed to evaluate a window against profile associations.</summary>
    public readonly struct WindowAttributes
    {
        public string Title    { get; init; }
        public string Class    { get; init; }
        public string ExeName  { get; init; }
        public string FullPath { get; init; }
    }

    /// <summary>
    /// Resolves all window / process attributes for a given HWND.
    /// </summary>
    /// <param name="hwnd">Window handle to inspect.</param>
    /// <param name="resolveFullPath">
    ///   When <c>true</c> (default), opens the process module to obtain the full executable path.
    ///   When <c>false</c>, uses <c>ProcessName</c> only — cheaper, suitable for scans when
    ///   no <see cref="TargetMatchType.FullPath"/> rules are defined.
    /// </param>
    public static WindowAttributes Resolve(IntPtr hwnd, bool resolveFullPath = true)
    {
        string exeName  = string.Empty;
        string fullPath = string.Empty;

        GetWindowThreadProcessId(hwnd, out var pidUint);
        if (pidUint != 0 && pidUint <= (uint)int.MaxValue)
        {
            try
            {
                using var p = Process.GetProcessById((int)pidUint);

                if (resolveFullPath)
                {
                    try
                    {
                        var full = p.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(full))
                        {
                            fullPath = full;
                            exeName  = Path.GetFileName(full).ToLowerInvariant();
                        }
                    }
                    catch (System.ComponentModel.Win32Exception) { /* access denied — fall through */ }
                    catch { /* process exited between open and MainModule access */ }
                }

                if (string.IsNullOrEmpty(exeName))
                    exeName = (p.ProcessName + ".exe").ToLowerInvariant();
            }
            catch { /* process exited before GetProcessById completed */ }
        }

        return new WindowAttributes
        {
            FullPath = fullPath,
            ExeName  = exeName,
            Class    = GetClassName(hwnd),
            Title    = GetWindowTitle(hwnd),
        };
    }

    // ── Matching ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> if <paramref name="attrs"/> satisfies at least one association
    /// in <paramref name="associations"/>. Mirrors <c>ProcessWatcher</c>'s index matching
    /// exactly so the TargetManager's Test-Match result is always consistent with runtime
    /// profile switching. Priority: FullPath → ExeName → WindowClass → WindowTitle.
    /// </summary>
    public static bool MatchesAssociations(in WindowAttributes attrs, IList<TargetAssociation>? associations)
    {
        if (associations == null || associations.Count == 0)
            return false;

        foreach (var ta in associations)
        {
            bool hit = ta.MatchType switch
            {
                TargetMatchType.FullPath    => string.Equals(ta.Value, attrs.FullPath, StringComparison.OrdinalIgnoreCase),
                TargetMatchType.ExeName     => string.Equals(ta.Value, attrs.ExeName,  StringComparison.OrdinalIgnoreCase),
                TargetMatchType.WindowClass => string.Equals(ta.Value, attrs.Class,    StringComparison.OrdinalIgnoreCase),
                TargetMatchType.WindowTitle => attrs.Title.Contains(ta.Value,          StringComparison.OrdinalIgnoreCase),
                _ => false
            };
            if (hit) return true;
        }

        return false;
    }
}
