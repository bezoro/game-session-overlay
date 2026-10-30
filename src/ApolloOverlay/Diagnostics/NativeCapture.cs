using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ApolloOverlay;

internal static class NativeCapture
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left, Top, Right, Bottom;
        internal int Width => Right - Left;
        internal int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader
    {
        internal uint Size;
        internal int Width, Height;
        internal ushort Planes, BitCount;
        internal uint Compression, SizeImage;
        internal int XPels, YPels;
        internal uint Used, Important;
    }

    internal delegate bool EnumWindow(nint hwnd, nint data);
    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")]
    internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    internal static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindow callback, nint data);
    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint dc, ref BitmapHeader info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(nint dest, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint rop);
    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(nint dc, int x, int y);
    [DllImport("dwmapi.dll")]
    internal static extern int DwmIsCompositionEnabled(out bool enabled);
    internal static long StartTicks => Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;

    internal static nint Handle(Window window) => new WindowInteropHelper(window).Handle;
    internal static bool IsOwned(nint hwnd, long start)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return hwnd != 0 && IsWindow(hwnd) && pid == Environment.ProcessId && StartTicks == start;
    }

    private static bool Intersects(Rect a, Rect b) => a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;
    internal static void Guard(nint underlay, nint overlay, Rect capture, long start)
    {
        if (!IsOwned(underlay, start)
            || !IsWindowVisible(underlay)
            || !GetWindowRect(underlay, out var bounds)
            || capture.Left < bounds.Left
            || capture.Top < bounds.Top
            || capture.Right > bounds.Right
            || capture.Bottom > bounds.Bottom)
            throw new InvalidOperationException("Capture aborted: owned coverage not established.");
        var foreground = GetForegroundWindow();
        if (foreground != underlay && foreground != overlay)
            throw new InvalidOperationException("Capture aborted: another foreground window owns attention.");
        var covered = false;
        var foreign = false;
        nint obstruction = 0;
        uint obstructionPid = 0;
        EnumWindows((hwnd, _) =>
        {
            if (hwnd == underlay)
            {
                covered = true;
                return false;
            }

            if (hwnd == overlay || !IsWindowVisible(hwnd))
                return true;
            if (GetWindowRect(hwnd, out var rect) && Intersects(rect, capture))
            {
                foreign = true;
                obstruction = hwnd;
                GetWindowThreadProcessId(hwnd, out obstructionPid);
            }

            return !foreign;
        }, 0);
        if (!covered || foreign)
            throw new InvalidOperationException($"Capture aborted: foreign window above owned underlay; hwnd={obstruction}, pid={obstructionPid}, underlayFound={covered}.");
    }

    internal static byte[] Capture(nint underlay, nint overlay, Rect capture, long start, string path)
    {
        Guard(underlay, overlay, capture, start);
        var screen = GetDC(0);
        var memory = CreateCompatibleDC(screen);
        nint bitmap = 0, old = 0;
        try
        {
            var header = new BitmapHeader
            {
                Size = 40,
                Width = capture.Width,
                Height = -capture.Height,
                Planes = 1,
                BitCount = 32
            };
            bitmap = CreateDIBSection(screen, ref header, 0, out var bits, 0, 0);
            if (bitmap == 0 || bits == 0)
                throw new InvalidOperationException("CreateDIBSection failed.");
            old = SelectObject(memory, bitmap);
            if (!BitBlt(memory, 0, 0, capture.Width, capture.Height, screen, capture.Left, capture.Top, 0x40CC0020))
                throw new InvalidOperationException("Owned BitBlt failed.");
            var pixels = new byte[capture.Width * capture.Height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            Guard(underlay, overlay, capture, start);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(capture.Width, capture.Height, 96, 96, PixelFormats.Bgr32, null, pixels, capture.Width * 4)));
            using var stream = File.Create(path);
            encoder.Save(stream);
            return pixels;
        }
        finally
        {
            if (old != 0)
                SelectObject(memory, old);
            if (bitmap != 0)
                DeleteObject(bitmap);
            if (memory != 0)
                DeleteDC(memory);
            if (screen != 0)
                ReleaseDC(0, screen);
        }
    }

    internal static Color Pixel(nint underlay, nint overlay, int x, int y, long start)
    {
        var rect = new Rect
        {
            Left = x,
            Top = y,
            Right = x + 1,
            Bottom = y + 1
        };
        Guard(underlay, overlay, rect, start);
        var dc = GetDC(0);
        try
        {
            var rgb = GetPixel(dc, x, y);
            if (rgb == uint.MaxValue)
                throw new InvalidOperationException("GetPixel failed.");
            return Color.FromRgb((byte)rgb, (byte)(rgb >> 8), (byte)(rgb >> 16));
        }
        finally
        {
            ReleaseDC(0, dc);
        }
    }
}
