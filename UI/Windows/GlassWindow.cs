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

internal class GlassWindow : Window
{
    public const int Gutter = 18;
    // Keep the drag surface hit-testable even at 100% transparency.
    private static readonly Brush TransparentHitSurface = Visuals.ColorBrush("#01000000");
    protected readonly Border Plate;
    protected readonly Border Frame;
    protected readonly Border Shadow;
    private readonly bool _noActivate;

    protected GlassWindow(string title, double width, bool translucent = false, int gutter = Gutter, bool noActivate = false, bool native = false)
    {
        _noActivate = noActivate;
        Title = title;
        Width = width + gutter * 2;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        AllowsTransparency = !native;
        Background = native ? Visuals.ThemeBrush("Surface.Window") : Brushes.Transparent;
        if (native) NativeChrome.Attach(this);
        FontFamily = Visuals.Typeface;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        ApplyDpi(VisualTreeHelper.GetDpi(this));

        var root = new Grid { Margin = new Thickness(native ? 0 : gutter) };
        var radius = new CornerRadius(gutter == Gutter ? 13 : 10);
        var effect = new DropShadowEffect
        {
            Color = Color.FromRgb(23, 48, 76), BlurRadius = gutter + 5, ShadowDepth = gutter / 3d,
            Opacity = translucent ? 0.20 : 0.32, RenderingBias = RenderingBias.Performance
        };
        effect.Freeze();
        Shadow = new Border { CornerRadius = radius, IsHitTestVisible = false };
        if (translucent)
        {
            Shadow.BorderThickness = new Thickness(2);
            Shadow.Effect = effect;
            root.Children.Add(Shadow);
        }
        Plate = new Border
        {
            CornerRadius = radius,
            Background = Visuals.ThemeBrush("Surface.Window"),
            Effect = translucent || native ? null : effect
        };
        Frame = new Border
        {
            CornerRadius = radius,
            BorderThickness = new Thickness(1),
            BorderBrush = Visuals.Outline
        };
        root.Children.Add(Plate);
        root.Children.Add(Frame);
        Content = root;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Desktop.ConfigureUtilityWindow(this, _noActivate);
        ApplyDpi(VisualTreeHelper.GetDpi(this));
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ApplyDpi(newDpi);
    }

    private void ApplyDpi(DpiScale dpi) => TextOptions.SetTextFormattingMode(this,
        dpi.DpiScaleX >= 1.25 ? TextFormattingMode.Ideal : TextFormattingMode.Display);

    protected void SetTextSurface(Brush background, bool opaque)
    {
        Frame.Background = opaque ? background : TransparentHitSurface;
        Frame.SnapsToDevicePixels = true;
        TextOptions.SetTextRenderingMode(Frame, opaque ? TextRenderingMode.Auto : TextRenderingMode.Grayscale);
        // An opaque surface behind the text makes ClearType safe in a layered window.
        RenderOptions.SetClearTypeHint(Frame, opaque ? ClearTypeHint.Enabled : ClearTypeHint.Auto);
    }
}
