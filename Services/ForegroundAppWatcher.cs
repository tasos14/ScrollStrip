using System.Diagnostics;
using static ScrollStrip.NativeMethods;

namespace ScrollStrip;

/// <summary>
/// Reports the foreground app's process name whenever it changes, via
/// SetWinEventHook(EVENT_SYSTEM_FOREGROUND). The hook is out-of-context, so callbacks arrive on the
/// thread that created this object, through its message loop — create it on the WPF UI thread.
///
/// UWP apps report as their frame host ("ApplicationFrameHost"), not the app itself.
/// </summary>
public sealed class ForegroundAppWatcher : IDisposable
{
    /// <summary>Process name without ".exe", or null when it cannot be read.</summary>
    public event Action<string?>? ForegroundChanged;

    public string? Current { get; private set; }

    private readonly WinEventDelegate _callback;   // kept alive for as long as the hook exists
    private IntPtr _hook;

    public ForegroundAppWatcher()
    {
        _callback = OnWinEvent;
        // Skipping our own process keeps the settings window from counting as "the app in front".
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            _callback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        Current = ProcessNameOf(GetForegroundWindow());
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd,
                            int idObject, int idChild, uint idEventThread, uint time)
    {
        string? name = ProcessNameOf(hwnd);
        if (string.Equals(name, Current, StringComparison.OrdinalIgnoreCase)) return;
        Current = name;
        ForegroundChanged?.Invoke(name);
    }

    public static string? ProcessNameOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return null;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return null;   // exited between the event and the lookup
        }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
