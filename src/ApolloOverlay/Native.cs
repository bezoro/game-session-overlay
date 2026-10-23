using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

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
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindow callback,nint parameter);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint MonitorFromWindow(nint hwnd,uint flags);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint hwnd,uint attribute,out int value,int bytes);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] internal static extern nint GetWindowLongPtr(nint hwnd,int index);
    [DllImport("user32.dll",EntryPoint="GetClassLongPtrW")] internal static extern nint GetClassLongPtr(nint hwnd,int index);
    [DllImport("user32.dll")] internal static extern nint SendMessageTimeout(nint hwnd,uint message,nuint wparam,nint lparam,uint flags,uint timeout,out nuint result);
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
    internal static PadFrame ReadPad(uint slot){var result=XInputGetState(slot,out var state);return result==0?new(true,(PadButtons)state.Pad.Buttons,state.Pad.LX,state.Pad.LY,state.Pad.RX,state.Pad.RY,state.Pad.LeftTrigger,state.Pad.RightTrigger):new(false,0,ReadFailed:result!=1167);}
    internal static Input[] KeyInputs(string keys)
    {
        if(keys=="F1")return [new(){Type=1,Key=0x70},new(){Type=1,Key=0x70,Flags=2}];
        var pair=keys switch{"Win+Tab"=>(0x5B,0x09),"Win+G"=>(0x5B,0x47),"Ctrl+Tab"=>(0x11,0x09),"Shift+Tab"=>(0x10,0x09),_=>(0,0)};
        if(pair.Item1==0)return [];return [new(){Type=1,Key=(ushort)pair.Item1},new(){Type=1,Key=(ushort)pair.Item2},new(){Type=1,Key=(ushort)pair.Item2,Flags=2},new(){Type=1,Key=(ushort)pair.Item1,Flags=2}];
    }
}

internal sealed class NativeTray : IDisposable
{
    internal const int Callback=0x8001;private NotifyData data;
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct NotifyData{internal uint Size;internal nint Hwnd;internal uint Id,Flags,Message;internal nint Icon;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]internal string Tip;internal uint State,StateMask;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]internal string Info;internal uint Timeout;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)]internal string InfoTitle;internal uint InfoFlags;internal Guid Guid;internal nint BalloonIcon;}
    [StructLayout(LayoutKind.Sequential)]private struct Point{internal int X,Y;}
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern bool Shell_NotifyIcon(uint operation,ref NotifyData data);
    [DllImport("user32.dll")]private static extern nint LoadIcon(nint instance,nint resource);
    [DllImport("user32.dll")]private static extern nint CreatePopupMenu();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern bool AppendMenu(nint menu,uint flags,nuint id,string text);
    [DllImport("user32.dll")]private static extern uint TrackPopupMenu(nint menu,uint flags,int x,int y,int reserved,nint owner,nint exclude);
    [DllImport("user32.dll")]private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")]private static extern bool GetCursorPos(out Point point);
    internal NativeTray(nint hwnd){data=new(){Size=(uint)Marshal.SizeOf<NotifyData>(),Hwnd=hwnd,Id=1,Flags=7,Message=Callback,Icon=LoadIcon(0,(nint)32512),Tip="Bezoro · Controle da sessão · Ctrl+Alt+Espaço",Info="",InfoTitle=""};if(!Shell_NotifyIcon(0,ref data))MessageBox.Show("Ícone da bandeja indisponível. Use Ctrl+Alt+Espaço para abrir a overlay.","Bezoro");}
    internal uint Menu(){var menu=CreatePopupMenu();try{AppendMenu(menu,0,1,"Abrir overlay");AppendMenu(menu,0,2,"Sistema / ajustes");AppendMenu(menu,0,3,"Encerrar overlay");GetCursorPos(out var point);Native.SetForegroundWindow(data.Hwnd);return TrackPopupMenu(menu,0x102,point.X,point.Y,0,data.Hwnd,0);}finally{DestroyMenu(menu);}}
    public void Dispose()=>Shell_NotifyIcon(2,ref data);
}
