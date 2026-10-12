using System.Runtime.InteropServices;
using static ScrollStrip.NativeMethods;

namespace ScrollStrip;

/// <summary>
/// Optional: drops wheel events that ScrollStrip did not inject, while it is scrolling. Covers a
/// touchpad driver's own edge or wheel emulation fighting ours. Uses WH_MOUSE_LL, so it sees only
/// the legacy wheel path — not precision-touchpad gestures, which apps get as pointer input.
///
/// The hook runs on a dedicated thread with its own message loop. Windows removes a low-level hook
/// that answers too slowly, so it must never wait on the UI thread or on a lock.
/// </summary>
public sealed class NativeWheelSuppressor : IDisposable
{
    private readonly Func<bool> _isScrollActive;
    private readonly LowLevelMouseProc _proc;   // kept alive for as long as the hook exists
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;
    private readonly ManualResetEventSlim _started = new(false);

    /// <param name="isScrollActive">Lock-free check; called on the hook thread for every wheel event.</param>
    public NativeWheelSuppressor(Func<bool> isScrollActive)
    {
        _isScrollActive = isScrollActive;
        _proc = HookProc;
    }

    public bool IsRunning => _thread != null;

    /// <summary>
    /// The decision, separate from the hook for testing. Injected events are passed first and
    /// unconditionally: that includes our own SendInput output, and checking it before
    /// <paramref name="isScrollActive"/> keeps the hot path off any shared state.
    /// </summary>
    public static bool ShouldSuppress(int message, uint flags, Func<bool> isScrollActive)
    {
        if (message != WM_MOUSEWHEEL && message != WM_MOUSEHWHEEL) return false;
        if ((flags & LLMHF_INJECTED) != 0) return false;
        return isScrollActive();
    }

    public void Start()
    {
        if (_thread != null) return;
        _started.Reset();
        _thread = new Thread(Run) { IsBackground = true, Name = "ScrollStrip-WheelHook", Priority = ThreadPriority.Highest };
        _thread.Start();
        _started.Wait(TimeSpan.FromSeconds(2));
    }

    public void Stop()
    {
        if (_thread == null) return;
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        _started.Set();
        if (_hook == IntPtr.Zero) return;

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            DispatchMessage(ref msg);

        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if (ShouldSuppress((int)wParam, info.flags, _isScrollActive))
                return new IntPtr(1);   // swallow
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        Stop();
        _started.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public int ptX, ptY;
    }

    [DllImport("user32.dll")] private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG lpMsg);
}
