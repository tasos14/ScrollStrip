using System.Runtime.InteropServices;
using static ScrollStrip.NativeMethods;

namespace ScrollStrip;

/// <summary>
/// Hidden message-only window that receives precision touchpad raw input
/// and fires FrameReceived once per assembled touch frame.
/// </summary>
public sealed class TouchpadListener : IDisposable
{
    public event Action<TouchFrame>? FrameReceived;

    private sealed class Device
    {
        public required HidContactParser Parser;
        public required TouchFrameAssembler Assembler;
    }

    private IntPtr _hwnd;
    private WndProcDelegate? _wndProcDelegate;
    private readonly Dictionary<IntPtr, Device> _devices = new();

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

        if (!RegisterRawInputDevices(rid, (uint)rid.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            throw new InvalidOperationException($"RegisterRawInputDevices failed: {Marshal.GetLastWin32Error()}");

        var thread = new Thread(MessageLoop) { IsBackground = true, Name = "ScrollStrip-MsgLoop" };
        thread.Start();
    }

    private void MessageLoop()
    {
        while (GetMessage(out var msg, _hwnd, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_INPUT)        { HandleRawInput(lParam); return IntPtr.Zero; }
        if (msg == WM_INPUT_DEVICE_CHANGE) { ClearDevices(); return IntPtr.Zero; }
        return DefWindowProc(hwnd, msg, wParam, lParam);
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
                if (frame != null) FrameReceived?.Invoke(frame);
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
            PostQuitMessage(0);
            _hwnd = IntPtr.Zero;
        }
        ClearDevices();
        _wndProcDelegate = null;
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
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int nExitCode);
}
