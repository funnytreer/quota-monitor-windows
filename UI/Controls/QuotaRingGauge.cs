using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace QuotaMonitor;

internal readonly record struct MeterColors(Brush Number, Brush Arc, Brush Track, Brush Fill);

internal static class QuotaRingGauge
{
    internal static FrameworkElement Create(Quota quota, bool low, Brush ink, MeterColors colors,
        int count, Effect? shadow, double scale = 1)
    {
        bool compact = count > 2;
        var entry = new Grid();
        entry.RowDefinitions.Add(new RowDefinition());
        entry.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var dial = new Dial(quota.Remaining, colors, scale);
        var value = Visuals.Text(WidgetWindow.DisplayedValue(quota), Math.Max(14, 15 * scale), colors.Number, bold: true);
        value.HorizontalAlignment = HorizontalAlignment.Center;
        value.Effect = shadow;
        dial.Children.Add(value);
        entry.Children.Add(dial);

        var name = Visuals.Text(quota.Name, Math.Max(12.5, 12.5 * scale), low && compact ? colors.Number : ink, bold: true);
        FrameworkElement label;
        if (compact)
        {
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            label = name;
        }
        else
        {
            var markedLabel = Visuals.Columns(15 * scale, 0, low ? 14 * scale : 0);
            Visuals.Place(markedLabel, ServiceMark.Create(quota.Service, ink, 12 * scale, quota.BucketKey), 0);
            Visuals.Place(markedLabel, name, 1);
            if (low) Visuals.Place(markedLabel, WidgetWindow.Warning(colors.Arc, size: 10), 2);
            label = markedLabel;
        }
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.Margin = new Thickness(0, 3, 0, 2);
        label.Effect = shadow;
        Grid.SetRow(label, 1);
        entry.Children.Add(label);
        WidgetWindow.DescribeForAccessibility(entry, quota, low);
        return entry;
    }

    // Arrange the geometry in final DIPs, including when the user freely resizes the widget.
    private sealed class Dial : Panel
    {
        private readonly double _maximum, _stroke;
        private readonly int _value;
        private readonly Path? _arc;
        private double _diameter = -1;

        internal Dial(int? remaining, MeterColors colors, double scale)
        {
            _maximum = 96 * scale;
            _stroke = 4.5 * scale;
            _value = remaining ?? 0;
            Children.Add(new Ellipse { Fill = Brushes.Transparent, Stroke = colors.Track, StrokeThickness = _stroke });
            if (_value == 100) Children.Add(new Ellipse { Stroke = colors.Arc, StrokeThickness = _stroke });
            else if (_value > 0)
            {
                _arc = new Path { Stroke = colors.Arc, StrokeThickness = _stroke,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                Children.Add(_arc);
            }
        }

        protected override Size MeasureOverride(Size available)
        {
            var side = Math.Min(_maximum, Math.Min(available.Width, available.Height));
            var size = new Size(side, side);
            foreach (UIElement child in Children) child.Measure(size);
            return size;
        }

        protected override Size ArrangeOverride(Size size)
        {
            double side = Math.Min(_maximum, Math.Min(size.Width, size.Height));
            if (_arc != null && side != _diameter)
            {
                double c = side / 2, radius = Math.Max(0, c - _stroke / 2);
                double angle = _value * 2 * Math.PI / 100;
                var arc = new StreamGeometry();
                using (var path = arc.Open())
                {
                    path.BeginFigure(new Point(c, c - radius), false, false);
                    path.ArcTo(new Point(c + radius * Math.Sin(angle), c - radius * Math.Cos(angle)),
                        new Size(radius, radius), 0, _value > 50, SweepDirection.Clockwise, true, false);
                }
                arc.Freeze();
                _arc.Data = arc;
            }
            _diameter = side;
            var rect = new Rect((size.Width - side) / 2, (size.Height - side) / 2, side, side);
            foreach (UIElement child in Children) child.Arrange(rect);
            return size;
        }
    }
}
