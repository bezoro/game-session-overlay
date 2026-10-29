namespace ApolloOverlay;

public readonly record struct SidebarBounds(int X, int Y, int Width, int Height);
public readonly record struct SidebarPresentation(SidebarBounds Bounds, double DpiScale, double ContentScale);

/// <summary>Combines the destination monitor's work area, Windows DPI and user size preference.</summary>
public static class SidebarLayout
{
    public const double DesignWidth = 440, DesignHeight = 684;
    private static double DpiScale(double dpi) => double.IsFinite(dpi) && dpi is >= 48 and <= 960 ? dpi / 96 : 1;
    public static double Density(DisplayRef display, double dpi = 96) => Math.Clamp(display.Height / (1080d * DpiScale(dpi)), 1, 1.5);
    public static double EffectiveScale(DisplayRef display, double dpi, int userPercent) => Density(display, dpi) * (userPercent is 100 or 115 or 130 or 150 ? userPercent : 100) / 100d;
    public static SidebarBounds Place(DisplayRef display, double dpi = 96, int userPercent = 100, string side = "Left") => Calculate(display, dpi, userPercent, side).Bounds;

    public static SidebarPresentation Calculate(DisplayRef display, double dpi, int userPercent, string side, bool desktop = false)
    {
        var scale = DpiScale(dpi);
        var density = EffectiveScale(display, dpi, userPercent);
        var margin = (int)Math.Min(Math.Round(16 * scale), Math.Max(0, (Math.Min(display.Width, display.Height) - 1) / 2d));
        var availableWidth = Math.Max(1, display.Width - 2 * margin);
        var availableHeight = Math.Max(1, display.Height - 2 * margin);
        // A percentage cap must not squeeze the content below its supported 360-DIP layout.
        var widthLimit = display.Width / scale < 1000 ? availableWidth : Math.Max(display.Width * .38, 360 * scale * density);
        var width = Math.Max(1, (int)Math.Min(Math.Min((desktop ? 480 : 572) * scale * density, widthLimit), availableWidth));
        var height = Math.Max(1, (int)Math.Min((desktop ? 760 : 980) * scale * density, availableHeight));
        var bounds = new SidebarBounds(side == "Right" ? display.X + display.Width - margin - width : display.X + margin, display.Y + margin, width, height);
        return new(bounds, scale, density);
    }
}
