using System.Runtime.InteropServices;
using System.Text;

namespace ApolloOverlay;

internal static class Native
{
    internal delegate bool EnumWindow(nint hwnd, nint parameter);
    internal delegate bool EnumMonitor(nint monitor, nint dc, nint rect, nint parameter);
    internal delegate void WinEvent(nint hook, uint evt, nint hwnd, int objectId, int childId, uint thread, uint time);
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfo
    {
        public int Size;
        public Rect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Gamepad
    {
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short LX, LY, RX, RY;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XState
    {
        public uint Packet;
        public Gamepad Pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XBattery
    {
        public byte Type, Level;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XVibration
    {
        public ushort Left, Right;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XCapabilities
    {
        public byte Type, SubType;
        public ushort Flags;
        public Gamepad Pad;
        public XVibration Vibration;
    }

    // INPUT is 40 bytes on x64: union starts at offset 8 and is 32 bytes.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    internal struct Input
    {
        [FieldOffset(0)]
        public uint Type;
        [FieldOffset(8)]
        public ushort Key;
        [FieldOffset(10)]
        public ushort Scan;
        [FieldOffset(12)]
        public uint Flags;
    }

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumWindow callback, nint parameter);
    [DllImport("user32.dll")]
    internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")]
    internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")]
    internal static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out int value, int bytes);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    internal static extern nint GetClassLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SendMessageTimeout(nint hwnd, uint message, nuint wparam, nint lparam, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll")]
    internal static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(nint hwnd, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(nint hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AllowSetForegroundWindow(uint pid);
    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    [DllImport("user32.dll")]
    internal static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);
    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool AttachThreadInput(uint current, uint foreground, bool attach);
    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")]
    internal static extern bool ShowWindowAsync(nint hwnd, int command);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool PostMessage(nint hwnd, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")]
    internal static extern bool EnumDisplayMonitors(nint dc, nint rect, EnumMonitor callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    internal static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    internal static extern nint SetWinEventHook(uint min, uint max, nint module, WinEvent callback, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")]
    internal static extern bool UnhookWinEvent(nint hook);
    [DllImport("xinput1_4.dll")]
    internal static extern uint XInputGetState(uint slot, out XState state);
    [DllImport("xinput1_4.dll")]
    internal static extern uint XInputGetBatteryInformation(uint slot, byte deviceType, out XBattery battery);
    [DllImport("xinput1_4.dll")]
    internal static extern uint XInputGetCapabilities(uint slot, uint flags, out XCapabilities capabilities);
    [DllImport("xinput1_4.dll")]
    internal static extern uint XInputSetState(uint slot, ref XVibration vibration);
    internal static PadFrame ReadPad(uint slot)
    {
        var result = XInputGetState(slot, out var state);
        return result == 0 ? new(true, (PadButtons)state.Pad.Buttons, state.Pad.LX, state.Pad.LY, state.Pad.RX, state.Pad.RY, state.Pad.LeftTrigger, state.Pad.RightTrigger) : new(false, 0, ReadFailed: result != 1167);
    }

    internal static Input[] KeyInputs(string keys)
    {
        if (keys == "F1")
            return [new()
            {
                Type = 1,
                Key = 0x70
            }, new()
            {
                Type = 1,
                Key = 0x70,
                Flags = 2
            }

            ];
        var pair = keys switch
        {
            "Win+Tab" => (0x5B, 0x09),
            "Win+G" => (0x5B, 0x47),
            "Ctrl+Tab" => (0x11, 0x09),
            "Shift+Tab" => (0x10, 0x09),
            _ => (0, 0)
        };
        if (pair.Item1 == 0)
            return [];
        return [new()
        {
            Type = 1,
            Key = (ushort)pair.Item1
        }, new()
        {
            Type = 1,
            Key = (ushort)pair.Item2
        }, new()
        {
            Type = 1,
            Key = (ushort)pair.Item2,
            Flags = 2
        }, new()
        {
            Type = 1,
            Key = (ushort)pair.Item1,
            Flags = 2
        }

        ];
    }
}
