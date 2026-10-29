using System.Windows;
using System.Windows.Controls;
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

    // Update only the image, so a background completion cannot rebuild the view,
    // steal focus, move the selection or interrupt an editor.
    private void ApplyAvailableIcon(WindowRef window)
    {
        if (exiting || !IsVisible || !Controller.Platform.Valid(window))
            return;
        var icon = Controller.Platform.Icon(window);
        if (icon is null)
            return;
        if (Controller.Session.Anchor?.Same(window) == true)
        {
            ContextIcon.Source = icon;
            ContextIcon.Visibility = Visibility.Visible;
            ContextGlyph.Visibility = Visibility.Collapsed;
        }

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
