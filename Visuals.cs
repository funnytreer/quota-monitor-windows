using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace QuotaMonitor;

internal static class Visuals
{
    // The existing code-built windows share these brushes; XAML controls use DynamicResource directly.
    private static readonly Dictionary<string, SolidColorBrush> ThemeBrushes = new();
    public static Brush ThemeBrush(string key)
    {
        if (!ThemeBrushes.TryGetValue(key, out var brush))
            ThemeBrushes[key] = brush = ((SolidColorBrush)Application.Current.FindResource(key)).Clone();
        return brush;
    }

    public static void RefreshThemeBrushes()
    {
        foreach (var (key, brush) in ThemeBrushes)
            brush.Color = ((SolidColorBrush)Application.Current.FindResource(key)).Color;
    }

    public static Brush Ink => ThemeBrush("Text.Primary");
    public static Brush Gray => ThemeBrush("Text.Secondary");
    public static Brush Blue => ThemeBrush("Accent.Primary");
    public static Brush Amber => ThemeBrush("Low.Text");
    public static Brush Outline => ThemeBrush("Border.Hairline");
    public static readonly FontFamily Typeface = new("Segoe UI Variable Text, Microsoft YaHei UI");
    internal static readonly FontFamily NumericTypeface = new("Cascadia Mono, Consolas, Microsoft YaHei UI");
    private static readonly FontFamily Symbols = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    internal static T Frozen<T>(T resource) where T : Freezable
    {
        resource.Freeze();
        return resource;
    }

    public static Brush ColorBrush(string hex) => Frozen(new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)));
    public static Brush Alpha(Color color, double opacity) =>
        Frozen(new SolidColorBrush(Color.FromArgb((byte)Math.Round(opacity * 255), color.R, color.G, color.B)));

    internal static void SetText(TextBlock block, string value)
    {
        if (block.Text != value) block.Text = value;
    }

    public static TextBlock Text(string text, double size, Brush? color = null, bool bold = false)
    {
        var label = new TextBlock
        {
            Text = text,
            FontFamily = Typeface,
            FontSize = size,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = color ?? Ink,
            VerticalAlignment = VerticalAlignment.Center
        };
        Typography.SetNumeralAlignment(label, FontNumeralAlignment.Tabular);
        TextOptions.SetTextHintingMode(label, TextHintingMode.Fixed);
        return label;
    }

    public static TextBlock Symbol(string glyph, double size, Brush? color = null) => new()
    {
        Text = glyph, FontFamily = Symbols, FontSize = size, Foreground = color ?? Ink,
        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
    };

    public static Button Button(object content, string name, double width, double height, Action action)
    {
        var button = new Button
        {
            Content = content, Width = width, Height = height,
            Style = (Style)Application.Current.FindResource("ControlButton")
        };
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => action();
        return button;
    }

    public static Grid Columns(params double[] widths)
    {
        var row = new Grid();
        foreach (double width in widths)
            row.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = width switch
                {
                    0 => GridLength.Auto,
                    < 0 => new GridLength(-width, GridUnitType.Star),
                    _ => new GridLength(width)
                }
            });
        return row;
    }

    public static Border Card(UIElement child) => new()
    {
        Child = child, Background = ThemeBrush("Surface.Card"), BorderBrush = Outline,
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 9, 12, 9)
    };

    public static Button ActionButton(string name, Action run)
    {
        var b = Button(Text(name, 11, Blue), name, double.NaN, 25, run);
        b.Padding = new Thickness(7, 2, 7, 2);
        return b;
    }

    public static void Place(Grid row, UIElement content, int column)
    {
        Grid.SetColumn(content, column);
        row.Children.Add(content);
    }

    public static Border Rule(double above, double below) => new()
    {
        Height = 1, Background = Outline, Margin = new Thickness(0, above, 0, below)
    };

}
