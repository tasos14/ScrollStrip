using System.Runtime.InteropServices;
using static ScrollStrip.NativeMethods;

namespace ScrollStrip;

/// <summary>
/// Hidden message-only window that receives precision touchpad raw input
/// and fires ContactsReceived with parsed finger contacts.
/// </summary>
public sealed class TouchpadListener : IDisposable
{
    public event Action<List<TouchContact>, int>? ContactsReceived;

    private IntPtr _hwnd;
    private WndProcDelegate? _wndProcDelegate;
    private readonly Dictionary<IntPtr, HidContactParser> _parsers = new();

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
        if (msg == WM_INPUT_DEVICE_CHANGE) { _parsers.Clear(); return IntPtr.Zero; }
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

            int dataOffset = rawhidOffset + 8;
            if (dataOffset + dwSizeHid > size) return;

            byte[] report = new byte[dwSizeHid];
            Marshal.Copy((IntPtr)(p + dataOffset), report, 0, (int)dwSizeHid);

            var parser = GetOrCreateParser(header.hDevice);
            if (parser == null) return;

            int contactCount = parser.GetContactCount(report);
            var contacts = parser.Parse(report);

            if (contacts.Count > 0 || contactCount >= 0)
                ContactsReceived?.Invoke(contacts, contactCount);
        }
    }

    private HidContactParser? GetOrCreateParser(IntPtr hDevice)
    {
        if (hDevice == IntPtr.Zero) return null;

        if (_parsers.TryGetValue(hDevice, out var existing) && existing.IsValid)
            return existing;

        var parser = new HidContactParser();
        if (!parser.Init(hDevice))
        {
            parser.Dispose();
            return null;
        }

        _parsers[hDevice] = parser;
        return parser;
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            PostQuitMessage(0);
            _hwnd = IntPtr.Zero;
        }
        foreach (var p in _parsers.Values) p.Dispose();
        _parsers.Clear();
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
