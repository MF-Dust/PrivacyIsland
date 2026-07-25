using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ClassIsland.Core.Controls;
using FluentAvalonia.UI.Controls;

namespace PrivacyIsland.Settings;

internal static class SettingsUi
{
    public static TabItem CategoryTab(string glyph, string header, params Control[] sections)
    {
        var content = new StackPanel { Spacing = 8, Margin = new Thickness(0, 12, 0, 4) };
        foreach (var section in sections) content.Children.Add(section);
        return new TabItem
        {
            Header = TabHeader(glyph, header),
            Content = content,
        };
    }

    public static Control TabHeader(string glyph, string title) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        Children =
        {
            new FontIcon { Glyph = glyph, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 16 },
            new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
        },
    };

    public static SettingsExpander Expander(
        string glyph,
        string header,
        string description,
        params Control[] items)
    {
        var expander = new SettingsExpander
        {
            Header = header,
            Description = description,
            IconSource = new FluentIconSource(glyph),
            Margin = new Thickness(0, 0, 0, 4),
        };
        foreach (var item in items) expander.Items.Add(item);
        return expander;
    }

    public static SettingsExpanderItem Item(string glyph, string content, string description, Control footer)
        => new()
        {
            Content = content,
            Description = description,
            IconSource = new FluentIconSource(glyph),
            Footer = footer,
        };

    public static Button ActionButton(string glyph, string text) => new()
    {
        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new FontIcon { Glyph = glyph, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 14 },
                new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center },
            },
        },
        HorizontalAlignment = HorizontalAlignment.Left,
        Margin = new Thickness(0, 2, 0, 0),
    };

    public static Control Row(string label, Control control, double labelWidth) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 10,
        Children =
        {
            new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Width = labelWidth },
            control,
        },
    };
}
