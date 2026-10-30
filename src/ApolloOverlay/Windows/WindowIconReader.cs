using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ApolloOverlay;
/// <summary>Copies a bounded native icon query on the cache worker, retaining original resolution.</summary>
internal static class WindowIconReader
{
    private const uint WmGetIcon = 0x007F, AbortIfHung = 2;
    private const nuint LargeIcon = 1, SmallIconFallback = 2;
    private const int ClassLargeIcon = -14, PreferredDpi = 192;
    internal static ImageSource? Load(WindowRef window, Func<WindowRef, bool> valid)
    {
        if (!valid(window))
            return null;
        nint owned = 0;
        try
        {
            // ICON_SMALL2 commonly returns 16 px. Prefer the application's large icon
            // and request a high-DPI variant before falling back to a class/small icon.
            Native.SendMessageTimeout(window.Handle, WmGetIcon, LargeIcon, PreferredDpi, AbortIfHung, 100, out var large);
            var borrowed = large == 0 ? Native.GetClassLongPtr(window.Handle, ClassLargeIcon) : (nint)large;
            if (borrowed == 0 && valid(window))
            {
                Native.SendMessageTimeout(window.Handle, WmGetIcon, SmallIconFallback, PreferredDpi, AbortIfHung, 100, out var small);
                borrowed = (nint)small;
            }

            if (borrowed == 0 || !valid(window))
                return null;
            owned = CopyIcon(borrowed);
            if (owned == 0)
                return null;
            var image = Imaging.CreateBitmapSourceFromHIcon(owned, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return valid(window) ? image : null;
        }
        catch (Exception error) when (error is Win32Exception or ArgumentException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            if (owned != 0)
                DestroyIcon(owned);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CopyIcon(nint icon);
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint icon);
}
