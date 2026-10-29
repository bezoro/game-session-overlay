using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ApolloOverlay;
/// <summary>Renders a source vector in its original coordinate frame, preserving optical padding.</summary>
public sealed class VectorIcon : Viewbox
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(nameof(Data), typeof(Geometry), typeof(VectorIcon), new PropertyMetadata(null, RefreshDrawing));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(VectorIcon), new PropertyMetadata(null, RefreshDrawing));
    public static readonly DependencyProperty SourceSizeProperty = DependencyProperty.Register(nameof(SourceSize), typeof(Size), typeof(VectorIcon), new PropertyMetadata(new Size(24, 24), RefreshDrawing));
    private readonly Canvas frame = new();
    private readonly Path drawing = new();
    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }
    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }
    public Size SourceSize
    {
        get => (Size)GetValue(SourceSizeProperty);
        set => SetValue(SourceSizeProperty, value);
    }

    public VectorIcon()
    {
        Stretch = Stretch.Uniform;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;
        Focusable = false;
        frame.Children.Add(drawing);
        Child = frame;
        SetResourceReference(FillProperty, ThemeSupport.Text);
        RefreshDrawing();
    }

    private static void RefreshDrawing(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((VectorIcon)sender).RefreshDrawing();
    private void RefreshDrawing()
    {
        drawing.Data = Data;
        drawing.Fill = Fill;
        frame.Width = SourceSize.Width;
        frame.Height = SourceSize.Height;
    }
}
