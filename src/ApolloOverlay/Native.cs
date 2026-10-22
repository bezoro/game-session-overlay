using System.Runtime.InteropServices;
using System.Text;

namespace ApolloOverlay;

internal static class Native
{
    internal delegate bool EnumWindow(nint hwnd,nint parameter);
    internal delegate bool EnumMonitor(nint monitor,nint dc,nint rect,nint parameter);
    internal delegate void WinEvent(nint hook,uint evt,nint hwnd,int objectId,int childId,uint thread,uint time);
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] internal struct MonitorInfo { public int Size; public Rect Monitor,Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string Device; }
    [StructLayout(LayoutKind.Sequential)] internal struct Gamepad { public ushort Buttons; public byte LeftTrigger,RightTrigger; public short LX,LY,RX,RY; }
    [StructLayout(LayoutKind.Sequential)] internal struct XState { public uint Packet; public Gamepad Pad; }
    // INPUT is 40 bytes on x64: union starts at offset 8 and is 32 bytes.
    [StructLayout(LayoutKind.Explicit,Size=40)] internal struct Input { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public ushort Key; [FieldOffset(12)] public uint Flags; }
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindow callback,nint parameter);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint GetWindow(nint hwnd,uint command);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd,out uint pid);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern int GetWindowText(nint hwnd,StringBuilder text,int count);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern int GetClassName(nint hwnd,StringBuilder text,int count);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint hwnd,int command);
    [DllImport("user32.dll",SetLastError=true)] internal static extern bool PostMessage(nint hwnd,uint message,nint wparam,nint lparam);
    [DllImport("user32.dll",SetLastError=true)] internal static extern uint SendInput(uint count,Input[] inputs,int size);
    [DllImport("user32.dll",SetLastError=true)] internal static extern bool RegisterHotKey(nint hwnd,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint hwnd,int id);
    [DllImport("user32.dll")] internal static extern bool EnumDisplayMonitors(nint dc,nint rect,EnumMonitor callback,nint data);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern bool GetMonitorInfo(nint monitor,ref MonitorInfo info);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint hwnd,nint after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] internal static extern nint SetWinEventHook(uint min,uint max,nint module,WinEvent callback,uint pid,uint thread,uint flags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(nint hook);
    [DllImport("xinput1_4.dll")] internal static extern uint XInputGetState(uint slot,out XState state);
    internal static PadFrame ReadPad(uint slot) => XInputGetState(slot,out var state)==0 ? new(true,(PadButtons)state.Pad.Buttons,state.Pad.LX,state.Pad.LY) : default;
}
