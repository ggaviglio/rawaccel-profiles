using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Threading;
using MouseApp.Models;

namespace MouseApp.Services;

/// <summary>
/// Foreground-window hook that maps running processes to mouse profiles.
/// </summary>
public interface IProcessWatcher : IDisposable { }

/// <summary>
/// Unified Win32 hook that monitors foreground changes and window destruction.
/// Switches to a matching profile on foreground focus, and performs a "Last Man Standing"
/// desktop scan on window destroy to revert to the Global Default when no matching window
/// remains for the currently active profile.
/// </summary>
public sealed class ProcessWatcher : IProcessWatcher
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_OBJECT_DESTROY    = 0x8001;
    private const uint WINEVENT_OUTOFCONTEXT   = 0x0000;

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private readonly ProfileService _profileService;
    private readonly Action<MouseProfile> _applyProfile;
    private readonly WinEventDelegate _winEventDelegate;
    private IntPtr _hook = IntPtr.Zero;

    private MouseProfile? _currentProfile;
    /// <summary>The HWND that triggered the most recent profile switch.</summary>
    private IntPtr _currentHwnd = IntPtr.Zero;

    public ProcessWatcher(ProfileService profileService, Action<MouseProfile> applyProfile)
    {
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        _applyProfile   = applyProfile   ?? throw new ArgumentNullException(nameof(applyProfile));

        _winEventDelegate = new WinEventDelegate(WinEventProc);
        if (OperatingSystem.IsWindows())
        {
            _hook = SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND, EVENT_OBJECT_DESTROY,
                IntPtr.Zero, _winEventDelegate,
                0, 0, WINEVENT_OUTOFCONTEXT);
        }
    }

    // -------------------------------------------------------------------------
    // Central dispatch
    // -------------------------------------------------------------------------

    private void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        try
        {
            if (hwnd == IntPtr.Zero)
                return;

            if (eventType == EVENT_SYSTEM_FOREGROUND)
                HandleForeground(hwnd);
            else if (eventType == EVENT_OBJECT_DESTROY)
                HandleDestroy(hwnd);
        }
        catch
        {
            // swallow — must never crash the hook thread
        }
    }

    // -------------------------------------------------------------------------
    // EVENT_SYSTEM_FOREGROUND
    // -------------------------------------------------------------------------

    private void HandleForeground(IntPtr hwnd)
    {
        var matched = _profileService.Profiles.FirstOrDefault(pr => GetWindowMatchesProfile(hwnd, pr));

        if (matched == null)
        {
            // Sticky rule: no match → maintain current active context
            Debug.WriteLine("[ProcessWatcher] FOREGROUND — no match; retaining current profile.");
            return;
        }

        Debug.WriteLine($"[ProcessWatcher] FOREGROUND — match found: \"{matched.ProfileName}\"");

        if (!ReferenceEquals(matched, _currentProfile))
        {
            _currentProfile = matched;
            _currentHwnd    = hwnd;
            var profile = matched;
            Dispatcher.UIThread.Post(() => _applyProfile(profile));
        }
    }

    // -------------------------------------------------------------------------
    // EVENT_OBJECT_DESTROY  — "Last Man Standing"
    // -------------------------------------------------------------------------

    private void HandleDestroy(IntPtr hwnd)
    {
        if (_currentProfile == null || hwnd != _currentHwnd)
            return;

        Debug.WriteLine($"[ProcessWatcher] DESTROY — active-profile HWND destroyed; scanning desktop…");

        // Scan all remaining visible windows for any match with the active profile
        var activeProfile = _currentProfile;
        bool anyMatch = false;

        EnumWindows((w, _) =>
        {
            if (w != hwnd && IsWindowVisible(w) && GetWindowMatchesProfile(w, activeProfile))
            {
                anyMatch = true;
                return false; // stop enumeration early
            }
            return true;
        }, IntPtr.Zero);

        if (anyMatch)
        {
            Debug.WriteLine("[ProcessWatcher] DESTROY — other matching window still alive; no revert.");
            return;
        }

        // No windows remain for the active profile → revert to Global Default
        var global = _profileService.Profiles.FirstOrDefault(p => p.IsGlobalDefault);
        if (global != null)
        {
            Debug.WriteLine($"[ProcessWatcher] DESTROY — last window closed; reverting to Global Default \"{global.ProfileName}\".");
            _currentProfile = global;
            _currentHwnd    = IntPtr.Zero;
            Dispatcher.UIThread.Post(() => _applyProfile(global));
        }
        else
        {
            Debug.WriteLine("[ProcessWatcher] DESTROY — no Global Default found; cannot revert.");
        }
    }

    // -------------------------------------------------------------------------
    // Central Matching Engine (Polymorphic)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns true if <paramref name="hwnd"/> satisfies at least one
    /// <see cref="TargetAssociation"/> in <paramref name="profile"/>.
    /// Checks ExeName, FullPath, WindowClass, and WindowTitle.
    /// </summary>
    private static bool GetWindowMatchesProfile(IntPtr hwnd, MouseProfile profile)
    {
        if (profile.TargetAssociations == null || profile.TargetAssociations.Count == 0)
            return false;

        // --- resolve process-based attributes ---
        string exeName   = string.Empty;
        string fullPath  = string.Empty;

        GetWindowThreadProcessId(hwnd, out var pidUint);
        if (pidUint != 0)
        {
            try
            {
                using var p = Process.GetProcessById((int)pidUint);
                try
                {
                    var full = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(full))
                    {
                        fullPath = full;
                        exeName  = Path.GetFileName(full).ToLowerInvariant();
                    }
                }
                catch
                {
                    exeName = (p.ProcessName + ".exe").ToLowerInvariant();
                }
            }
            catch { /* process exited or inaccessible */ }
        }

        // --- resolve window-based attributes ---
        var classSb = new StringBuilder(256);
        GetClassName(hwnd, classSb, classSb.Capacity);
        var windowClass = classSb.ToString();

        var titleSb = new StringBuilder(512);
        GetWindowText(hwnd, titleSb, titleSb.Capacity);
        var windowTitle = titleSb.ToString();

        // --- polymorphic match ---
        return profile.TargetAssociations.Any(ta => ta.MatchType switch
        {
            TargetMatchType.ExeName     => string.Equals(ta.Value, exeName,     StringComparison.OrdinalIgnoreCase),
            TargetMatchType.FullPath    => string.Equals(ta.Value, fullPath,    StringComparison.OrdinalIgnoreCase),
            TargetMatchType.WindowClass => string.Equals(ta.Value, windowClass, StringComparison.OrdinalIgnoreCase),
            TargetMatchType.WindowTitle => windowTitle.Contains(ta.Value,       StringComparison.OrdinalIgnoreCase),
            _ => false
        });
    }

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------

    public void Dispose()
    {
        try
        {
            if (_hook != IntPtr.Zero)
            {
                UnhookWinEvent(_hook);
                _hook = IntPtr.Zero;
            }
        }
        catch
        {
            // swallow
        }
    }
}
