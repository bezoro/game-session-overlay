using System.Windows;
using System.Windows.Controls;

internal static class WpfTestElements
{
    internal static void ClickNamed(FrameworkElement root, string name) =>
        ((Button)root.FindName(name)).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    internal static Button Button(DependencyObject root, string id) =>
        Descendants<Button>(root).First(button => Equals(button.Tag, id));

    internal static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match)
                yield return match;
            foreach (var nested in Descendants<T>(child))
                yield return nested;
        }
    }
}
