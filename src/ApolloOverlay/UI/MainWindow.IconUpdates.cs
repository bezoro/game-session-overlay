using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ApolloOverlay;

public partial class MainWindow
{
    private IWindowIconNotifications? iconNotifications;
    private void InitializeIconUpdates()
    {
        iconNotifications = Controller.Platform as IWindowIconNotifications;
        if (iconNotifications is not null)
            iconNotifications.IconAvailable += IconAvailable;
    }

    private void DisposeIconUpdates()
    {
        if (iconNotifications is not null)
            iconNotifications.IconAvailable -= IconAvailable;
        iconNotifications = null;
    }

    private void IconAvailable(WindowRef window)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;
        try
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => ApplyAvailableIcon(window));
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void UpdateContextIcon(ImageSource? source)
    {
        ContextIcon.Source = source;
        ApplyContextIconSize();
    }

    private void ApplyContextIconSize()
    {
        var dpi = VisualTreeHelper.GetDpi(ContextIcon);
        var scale = PanelRoot.LayoutTransform.Value;
        var size = ContextIconSize(ContextIcon.Source, compactChrome || narrow ? 32 : 42, Math.Max(dpi.DpiScaleX * scale.M11, dpi.DpiScaleY * scale.M22));
        ContextIcon.Width = ContextIcon.Height = size;
        ContextIcon.Visibility = size > 0 ? Visibility.Visible : Visibility.Collapsed;
        ContextGlyph.Visibility = size > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    internal static double ContextIconSize(ImageSource? source, double preferred, double pixelsPerDip)
    {
        if (source is null)
            return 0;
        if (source is not BitmapSource bitmap)
            return preferred;
        var nativeSize = Math.Floor(Math.Min(bitmap.PixelWidth, bitmap.PixelHeight) / pixelsPerDip);
        return nativeSize < 20 ? 0 : Math.Min(nativeSize, preferred);
    }

    // Update only the image, so a background completion cannot rebuild the view,
    // steal focus, move the selection or interrupt an editor.
    private void ApplyAvailableIcon(WindowRef window)
    {
        if (exiting || !IsVisible || !Controller.Platform.Valid(window))
            return;
        var icon = Controller.Platform.Icon(window);
        if (icon is null)
            return;
        if (Controller.Session.CardTarget?.Same(window) == true)
            UpdateContextIcon(icon);

        var id = WindowId(window);
        foreach (var entry in entries.Where(e => e.Id == id || e.Id == "recent:" + id))
        {
            if (entry.Button.Content is not ActionButtonContent grid)
                continue;
            var image = new Image
            {
                Source = icon,
                Width = 26,
                Height = 26,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = grid.Icon.Visibility
            };
            grid.ReplaceIcon(image);
        }
    }
}
