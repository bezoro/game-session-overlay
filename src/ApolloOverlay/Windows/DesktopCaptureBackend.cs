using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ApolloOverlay;

internal sealed class DesktopCaptureBackend : IScreenCaptureBackend
{
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, Bits;
        public uint Compression, ImageSize;
        public int XPels, YPels;
        public uint Used, Important;
    }

    public CapturePixels Capture(DisplayRef display)
    {
        if (display.Width is < 1 or > 16384 || display.Height is < 1 or > 16384 || (long)display.Width * display.Height > 40000000)
            throw new ArgumentException("Invalid monitor.");
        var screen = GetDC(0);
        if (screen == 0)
            throw new Win32Exception();
        nint memory = 0, bitmap = 0, previous = 0;
        try
        {
            memory = CreateCompatibleDC(screen);
            if (memory == 0)
                throw new Win32Exception();
            var info = new BitmapInfo
            {
                Size = 40,
                Width = display.Width,
                Height = -display.Height,
                Planes = 1,
                Bits = 32
            };
            bitmap = CreateDIBSection(screen, ref info, 0, out var bits, 0, 0);
            if (bitmap == 0 || bits == 0)
                throw new Win32Exception();
            previous = SelectObject(memory, bitmap);
            if (previous is 0 or -1)
                throw new Win32Exception();
            if (!BitBlt(memory, 0, 0, display.Width, display.Height, screen, display.X, display.Y, 0x40CC0020))
                throw new Win32Exception();
            var bytes = new byte[checked(display.Width * display.Height * 4)];
            Marshal.Copy(bits, bytes, 0, bytes.Length);
            for (var index = 3; index < bytes.Length; index += 4)
                bytes[index] = 255;
            return new(display.Width, display.Height, bytes);
        }
        finally
        {
            if (previous is not (0 or -1))
                SelectObject(memory, previous);
            if (bitmap != 0)
                DeleteObject(bitmap);
            if (memory != 0)
                DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetDC(nint window);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
}
