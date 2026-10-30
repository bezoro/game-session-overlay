using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ApolloOverlay;

/// <summary>A local screenshot with stable title and metadata, independent of loading state.</summary>
internal sealed class CaptureThumbnail : Grid
{
    internal CapturePreview Preview
    {
        get;
    }

    internal CaptureThumbnail(FrameworkElement owner, ScreenCaptureItem capture, bool compact)
    {
        RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
        RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
        Preview = new(owner, 180);
        Children.Add(Preview);
        var labels = new StackPanel { Margin = new(0, Number(owner, "SessionSpace.8", 8), 0, 0) };
        var title = new TextBlock
        {
            Text = capture.Title.Length > 0 ? capture.Title : "Display capture",
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            LineHeight = Number(owner, compact ? "SessionType.CompactBody" : "SessionType.Body", compact ? 14 : 15) * 1.35,
            MaxHeight = Number(owner, compact ? "SessionType.CompactBody" : "SessionType.Body", compact ? 14 : 15) * 2.7
        };
        title.SetResourceReference(TextBlock.FontSizeProperty, compact ? "SessionType.CompactBody" : "SessionType.Body");
        title.SetResourceReference(TextBlock.ForegroundProperty, ThemeSupport.Text);
        labels.Children.Add(title);
        foreach (var value in new[] { capture.Created.ToLocalTime().ToString("MMM d, yyyy"), $"{capture.Width} × {capture.Height}" })
        {
            var text = new TextBlock
            {
                Text = value,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new(0, 2, 0, 0)
            };
            text.SetResourceReference(TextBlock.FontSizeProperty, "SessionType.Caption");
            text.SetResourceReference(TextBlock.ForegroundProperty, ThemeSupport.SecondaryText);
            labels.Children.Add(text);
        }
        SetRow(labels, 1);
        Children.Add(labels);
    }

    private static double Number(FrameworkElement owner, string key, double fallback) => owner.TryFindResource(key) is double value ? value : fallback;
}

/// <summary>Reserves the image's aspect ratio while a bounded local decode is in flight.</summary>
internal sealed class CapturePreview : Border
{
    private readonly FrameworkElement owner;
    private readonly double maximumHeight;
    private readonly bool crop;
    internal BitmapSource? Source
    {
        get; private set;
    }
    internal bool Unavailable
    {
        get; private set;
    }

    internal CapturePreview(FrameworkElement owner, double maximumHeight, bool crop = true)
    {
        this.owner = owner;
        this.maximumHeight = maximumHeight;
        this.crop = crop;
        SetResourceReference(CornerRadiusProperty, "SessionRadius.Control");
        SetPlaceholder("Loading preview…");
    }

    internal void ShowImage(BitmapSource source)
    {
        Source = source;
        Unavailable = false;
        var brush = new ImageBrush(source) { Stretch = crop ? Stretch.UniformToFill : Stretch.Uniform };
        brush.Freeze();
        Background = brush;
        Child = null;
        AutomationProperties.SetName(this, "Screenshot preview");
    }

    internal void ShowUnavailable()
    {
        Source = null;
        Unavailable = true;
        SetPlaceholder("Preview unavailable");
    }

    private void SetPlaceholder(string text)
    {
        SetResourceReference(BackgroundProperty, ThemeSupport.Control);
        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new(8)
        };
        var icon = ActionIconCatalog.Create(owner, "session-gallery", 20);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        content.Children.Add(icon);
        var label = new TextBlock
        {
            Text = text == "Preview unavailable" ? "Unavailable" : "Loading…",
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            Margin = new(0, 4, 0, 0)
        };
        label.SetResourceReference(TextBlock.FontSizeProperty, "SessionType.Caption");
        label.SetResourceReference(TextBlock.ForegroundProperty, ThemeSupport.SecondaryText);
        content.Children.Add(label);
        Child = content;
        AutomationProperties.SetName(this, text);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        var width = double.IsFinite(constraint.Width) ? Math.Max(0, constraint.Width) : 320;
        var height = Math.Min(maximumHeight, width * 9 / 16);
        base.MeasureOverride(new(width, height));
        return new(width, height);
    }
}

/// <summary>Bounds active decodes across page changes, including work already running when cancelled.</summary>
internal static class CaptureImageLoader
{
    // Process-lifetime managed budget. No wait handle or per-row timer is created.
    private static readonly SemaphoreSlim Decoders = new(2, 2);

    internal static async Task<BitmapSource> ReadAsync(Func<ScreenCaptureItem, int, CancellationToken, Task<BitmapSource>> read,
        ScreenCaptureItem capture, int maximumWidth, CancellationToken cancellation)
    {
        await Decoders.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            return await read(capture, maximumWidth, cancellation).ConfigureAwait(false);
        }
        finally
        {
            Decoders.Release();
        }
    }
}
