using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using MouseApp.Models;

namespace MouseApp.Services;

/// <summary>
/// Foreground-window hook that maps running processes to mouse profiles.
/// </summary>
public interface IProcessWatcher : IDisposable { }

/// <summary>
/// Watches foreground window changes using SetWinEventHook and maps processes to profiles.
/// Calls the provided apply callback when a matching profile is activated and reverts to
/// the Global Default when the tracked process exits.
/// </summary>
public sealed class ProcessWatcher : IProcessWatcher
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private readonly ProfileService _profileService;
    private readonly Action<MouseProfile> _applyProfile;
    private readonly WinEventDelegate _winEventDelegate;
    private IntPtr _hook = IntPtr.Zero;

    private MouseProfile? _currentProfile;
    private Process? _trackedProcess;

    public ProcessWatcher(ProfileService profileService, Action<MouseProfile> applyProfile)
    {
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        _applyProfile = applyProfile ?? throw new ArgumentNullException(nameof(applyProfile));

        _winEventDelegate = new WinEventDelegate(WinEventProc);
        if (OperatingSystem.IsWindows())
        {
            _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _winEventDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);
        }
    }

    private void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        try
        {
            if (hwnd == IntPtr.Zero)
                return;

            _ = GetWindowThreadProcessId(hwnd, out var pidUint);
            if (pidUint == 0)
                return;

            var pid = (int)pidUint;

            string? exeName = null;
            try
            {
                using var p = Process.GetProcessById(pid);
                try
                {
                    var full = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(full)) exeName = Path.GetFileName(full).ToLowerInvariant();
                }
                catch
                {
                    // Accessing MainModule can fail (permissions); fall back to ProcessName
                    exeName = (p.ProcessName + ".exe").ToLowerInvariant();
                }
            }
            catch
            {
                // Process may have exited or be inaccessible
                exeName = null;
            }

            if (string.IsNullOrEmpty(exeName))
            {
                // Unmapped / unknown window: per Sticky Revert Rule, do not revert on focus change
                return;
            }

            // Find a matching profile by TargetExecutables
            var matched = _profileService.Profiles.FirstOrDefault(pr => pr.TargetExecutables != null && pr.TargetExecutables.Any(te => string.Equals(te, exeName, StringComparison.OrdinalIgnoreCase)));

            if (matched != null)
            {
                if (!ReferenceEquals(matched, _currentProfile))
                {
                    _currentProfile = matched;
                    _applyProfile(matched);
                    TrackProcess(pid);
                }
            }
            else
            {
                // No mapped profile for this window: do nothing (sticky rule)
            }
        }
        catch
        {
            // swallow exceptions to avoid crashing the hook thread
        }
    }

    private void TrackProcess(int pid)
    {
        try
        {
            // Unsubscribe previous
            if (_trackedProcess != null)
            {
                _trackedProcess.Exited -= TrackedProcess_Exited;
                _trackedProcess.EnableRaisingEvents = false;
                _trackedProcess.Dispose();
                _trackedProcess = null;
            }

            var proc = Process.GetProcessById(pid);
            proc.EnableRaisingEvents = true;
            proc.Exited += TrackedProcess_Exited;
            _trackedProcess = proc;
        }
        catch
        {
            // process may have exited already or insufficient permissions
            _trackedProcess = null;
        }
    }

    private void TrackedProcess_Exited(object? sender, EventArgs e)
    {
        try
        {
            // When the tracked process exits, revert to Global Default
            var global = _profileService.Profiles.FirstOrDefault(p => p.IsGlobalDefault);
            if (global != null)
            {
                _currentProfile = global;
                _applyProfile(global);
            }

            if (_trackedProcess != null)
            {
                _trackedProcess.Exited -= TrackedProcess_Exited;
                _trackedProcess.Dispose();
                _trackedProcess = null;
            }
        }
        catch
        {
            // ignore
        }
    }

    public void Dispose()
    {
        try
        {
            if (_hook != IntPtr.Zero)
            {
                UnhookWinEvent(_hook);
                _hook = IntPtr.Zero;
            }

            if (_trackedProcess != null)
            {
                _trackedProcess.Exited -= TrackedProcess_Exited;
                _trackedProcess.EnableRaisingEvents = false;
                _trackedProcess.Dispose();
                _trackedProcess = null;
            }
        }
        catch
        {
            // swallow
        }
    }
}
