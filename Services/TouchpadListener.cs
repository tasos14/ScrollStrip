using System.Diagnostics;
using System.Runtime.InteropServices;
using static ScrollStrip.NativeMethods;

namespace ScrollStrip;

/// <summary>Something that happened to the input stream rather than a touch frame.</summary>
public enum ListenerEvent
{
    /// <summary>The system is about to sleep.</summary>
    Suspending,
    /// <summary>The system woke; raw input was re-registered and device state rebuilt.</summary>
    Resumed,
    /// <summary>The health check found our raw input registration missing and restored it.</summary>
    Reregistered,
    /// <summary>A contact went silent past <see cref="FrameWatchdog.StallTimeoutSec"/>: its lift report was lost.</summary>
    ContactLost,
}

/// <summary>
/// Hidden message-only window that receives precision touchpad raw input and fires
/// FrameReceived once per assembled touch frame.
///
/// The window belongs to the thread that calls <see cref="Start"/> — the WPF UI thread — and the
/// WPF dispatcher pumps its messages, so every event here fires on that thread.
/// </summary>
public sealed class TouchpadListener : IDisposable
{
    public event Action<TouchFrame>? FrameReceived;
    public event Action<ListenerEvent>? EventOccurred;

    private static readonly UIntPtr HealthTimerId = 1;
    private static readonly UIntPtr StallTimerId  = 2;
    private const uint HealthIntervalMs = 5000;
    private const uint StallPollMs      = 50;

    private sealed class Device
    {
        public required HidContactParser Parser;
        public required TouchFrameAssembler Assembler;
    }

    private IntPtr _hwnd;
    private IntPtr _powerNotify;
    private WndProcDelegate? _wndProcDelegate;
    private readonly Dictionary<IntPtr, Device> _devices = new();
    private readonly FrameWatchdog _watchdog = new();
    private bool _stallTimerArmed;

    private static double WallSeconds() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public void Start()
    {
        var hInstance = GetModuleHandle(null);
        var className = "ScrollStripMsgWnd_" + Guid.NewGuid().ToString("N")[..8];

        _wndProcDelegate = WndProc;

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _wndProcDelegate,
            hInstance = hInstance,
            lpszClassName = className,
        };
        RegisterClassEx(ref wc);

        _hwnd = CreateWindowEx(0, className, "", 0,
            0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");

        if (!RegisterRawInput())
            throw new InvalidOperationException($"RegisterRawInputDevices failed: {Marshal.GetLastWin32Error()}");

        // Best effort: without it we lose only the resume hook; the health check still recovers.
        _powerNotify = RegisterSuspendResumeNotification(_hwnd, DEVICE_NOTIFY_WINDOW_HANDLE);
        SetTimer(_hwnd, HealthTimerId, HealthIntervalMs, IntPtr.Zero);
    }

    private bool RegisterRawInput()
    {
        var rid = new RAWINPUTDEVICE[]
        {
            new()
            {
                usUsagePage = 0x000D,
                usUsage     = 0x0005,
                dwFlags     = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY,
                hwndTarget  = _hwnd,
            }
        };
        return RegisterRawInputDevices(rid, (uint)rid.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    /// <summary>Whether this process's touchpad registration still targets our window.</summary>
    private bool IsRegistered()
    {
        uint size = (uint)Marshal.SizeOf<RAWINPUTDEVICE>();
        uint count = 0;
        GetRegisteredRawInputDevices(null, ref count, size);
        if (count == 0) return false;

        var devices = new RAWINPUTDEVICE[count];
        if (GetRegisteredRawInputDevices(devices, ref count, size) == uint.MaxValue) return true; // can't tell; don't churn

        return devices.Any(d => d.usUsagePage == 0x000D && d.usUsage == 0x0005 && d.hwndTarget == _hwnd);
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch ((int)msg)
        {
            case WM_INPUT:
                HandleRawInput(lParam);
                return IntPtr.Zero;

            case WM_INPUT_DEVICE_CHANGE:
                ClearDevices();
                return IntPtr.Zero;

            case WM_TIMER:
                OnTimer(wParam);
                return IntPtr.Zero;

            case WM_POWERBROADCAST:
                OnPower((int)wParam);
                return new IntPtr(1);   // TRUE: allow the transition
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void OnTimer(IntPtr id)
    {
        if ((UIntPtr)(ulong)id == StallTimerId)
        {
            if (_watchdog.CheckStalled(WallSeconds()))
            {
                DisarmStallTimer();
                EventOccurred?.Invoke(ListenerEvent.ContactLost);
            }
        }
        else if ((UIntPtr)(ulong)id == HealthTimerId)
        {
            if (!IsRegistered() && RegisterRawInput())
                EventOccurred?.Invoke(ListenerEvent.Reregistered);
        }
    }

    private void OnPower(int pbt)
    {
        switch (pbt)
        {
            case PBT_APMSUSPEND:
                _watchdog.Reset();
                DisarmStallTimer();
                EventOccurred?.Invoke(ListenerEvent.Suspending);
                break;

            // Both can arrive for one wake (AUTOMATIC always, RESUMESUSPEND when a user is
            // present); handling either is idempotent.
            case PBT_APMRESUMEAUTOMATIC:
            case PBT_APMRESUMESUSPEND:
                RegisterRawInput();
                ClearDevices();         // device handles and half-assembled frames may be stale
                _watchdog.Reset();
                DisarmStallTimer();
                EventOccurred?.Invoke(ListenerEvent.Resumed);
                break;
        }
    }

    private void ArmStallTimer()
    {
        if (_stallTimerArmed) return;
        SetTimer(_hwnd, StallTimerId, StallPollMs, IntPtr.Zero);
        _stallTimerArmed = true;
    }

    private void DisarmStallTimer()
    {
        if (!_stallTimerArmed) return;
        KillTimer(_hwnd, StallTimerId);
        _stallTimerArmed = false;
    }

    private unsafe void HandleRawInput(IntPtr lParam)
    {
        uint size = 0;
        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0) return;

        byte[] buf = new byte[size];
        fixed (byte* p = buf)
        {
            uint read = GetRawInputData(lParam, RID_INPUT, (IntPtr)p, ref size, headerSize);
            if (read != size) return;

            var header = Marshal.PtrToStructure<RAWINPUTHEADER>((IntPtr)p);
            if (header.dwType != RIM_TYPEHID) return;

            int rawhidOffset = (int)headerSize;
            uint dwSizeHid = (uint)Marshal.ReadInt32((IntPtr)p, rawhidOffset);
            uint dwCount   = (uint)Marshal.ReadInt32((IntPtr)p, rawhidOffset + 4);
            if (dwCount == 0 || dwSizeHid == 0) return;

            var device = GetOrCreateDevice(header.hDevice);
            if (device == null) return;

            int dataOffset = rawhidOffset + 8;

            // A single WM_INPUT may batch several reports back to back.
            for (uint i = 0; i < dwCount; i++)
            {
                long offset = dataOffset + (long)i * dwSizeHid;
                if (offset + dwSizeHid > size) break;

                byte[] report = new byte[dwSizeHid];
                Marshal.Copy((IntPtr)(p + offset), report, 0, (int)dwSizeHid);

                var parse = device.Parser.Parse(report);
                var frame = device.Assembler.Add(parse, device.Parser.PadWidthMm, device.Parser.PadHeightMm);
                if (frame == null) continue;

                // Any tip-down contact counts, confident or not: a resting palm reports too.
                bool down = frame.Contacts.Any(c => c.TipDown);
                _watchdog.OnFrame(WallSeconds(), down);
                if (down) ArmStallTimer(); else DisarmStallTimer();

                FrameReceived?.Invoke(frame);
            }
        }
    }

    private Device? GetOrCreateDevice(IntPtr hDevice)
    {
        if (hDevice == IntPtr.Zero) return null;

        if (_devices.TryGetValue(hDevice, out var existing) && existing.Parser.IsValid)
            return existing;

        var parser = new HidContactParser();
        if (!parser.Init(hDevice))
        {
            parser.Dispose();
            return null;
        }

        var device = new Device { Parser = parser, Assembler = new TouchFrameAssembler() };
        _devices[hDevice] = device;
        return device;
    }

    private void ClearDevices()
    {
        foreach (var d in _devices.Values) d.Parser.Dispose();
        _devices.Clear();
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            KillTimer(_hwnd, HealthTimerId);
            DisarmStallTimer();
            if (_powerNotify != IntPtr.Zero) UnregisterSuspendResumeNotification(_powerNotify);
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        ClearDevices();
        _wndProcDelegate = null;
    }
}
