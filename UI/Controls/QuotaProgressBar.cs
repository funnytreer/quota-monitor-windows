using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace QuotaMonitor;

internal static class QuotaProgressBar
{
    public static Border Create(int? remaining, double height, Brush track, Brush fill, int? previous = null)
    {
        var radius = new CornerRadius(height / 2);
        var host = new Border { Height = height, CornerRadius = radius, Background = track };
        if (remaining is not int value) return host;
        double fraction = Math.Clamp(value, 0, 100) / 100d;
        var scale = new ScaleTransform(fraction, 1);
        host.Child = new Border { Background = fill, CornerRadius = radius, RenderTransform = scale };
        if (previous is int from && from != value && SystemParameters.ClientAreaAnimation)
        {
            host.Loaded += Animate;
            void Animate(object sender, RoutedEventArgs args)
            {
                host.Loaded -= Animate;
                var animation = new DoubleAnimation(Math.Clamp(from, 0, 100) / 100d, fraction,
                    TimeSpan.FromMilliseconds(180)) { FillBehavior = FillBehavior.Stop };
                DisplayMetrics.MatchDisplay(animation, host);
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
            }
        }
        return host;
    }
}
