using System.Windows;
using System.Windows.Controls;

namespace ApolloOverlay;
/// <summary>Named parts for an action row; consumers never depend on visual child indexes.</summary>
internal sealed class ActionButtonContent : Grid
{
    internal FrameworkElement Icon
    {
        get;
        private set;
    }
    internal StackPanel Texts
    {
        get;
    } = new()
    {
        VerticalAlignment = VerticalAlignment.Center
    };
    internal TextBlock Title
    {
        get;
    }
    internal TextBlock Description
    {
        get;
    }

    internal ActionButtonContent(FrameworkElement icon, string title, string description, bool showIcon)
    {
        Icon = icon;
        ColumnDefinitions.Add(new()
        {
            Width = new GridLength(showIcon ? 38 : 0)
        });
        ColumnDefinitions.Add(new());
        ColumnDefinitions.Add(new()
        {
            Width = GridLength.Auto
        });
        icon.Visibility = showIcon ? Visibility.Visible : Visibility.Collapsed;
        Children.Add(icon);
        Title = new()
        {
            Text = title,
            FontSize = 18,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold
        };
        Description = new()
        {
            Text = description,
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap,
            Margin = new(0, 4, 0, 0),
            Visibility = description.Length > 0 ? Visibility.Visible : Visibility.Collapsed
        };
        Description.SetResourceReference(TextBlock.ForegroundProperty, ThemeSupport.SecondaryText);
        Texts.Children.Add(Title);
        Texts.Children.Add(Description);
        SetColumn(Texts, 1);
        Children.Add(Texts);
    }

    internal void ReplaceIcon(FrameworkElement icon)
    {
        icon.Visibility = Icon.Visibility;
        Children.Remove(Icon);
        Children.Insert(0, icon);
        Icon = icon;
    }
}
