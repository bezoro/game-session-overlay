using System.Windows;

namespace ApolloOverlay;

/// <summary>Typed access to the same presentation resources used by XAML controls.</summary>
internal static class PresentationTokens
{
    internal static double Number(FrameworkElement owner, string key, double fallback = 16) =>
        owner.TryFindResource(key) is double value && double.IsFinite(value) && value >= 0 ? value : fallback;

    internal static Duration Duration(FrameworkElement owner, string key, int fallbackMs = 160) =>
        owner.TryFindResource(key) is Duration { HasTimeSpan: true } value ? value : new(TimeSpan.FromMilliseconds(fallbackMs));
}

public partial class MainWindow
{
    private double ResourceNumber(string key, double fallback = 16) => PresentationTokens.Number(this, key, fallback);
    private Duration ResourceDuration(string key, int fallbackMs = 160) => PresentationTokens.Duration(this, key, fallbackMs);
}
