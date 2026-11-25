using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace QuotaMonitor.Tests;

public partial class WindowThemeTests
{
    [Fact]
    public void ExistingWindowsLoadAndWidgetRendersAcrossThemeAndTransparencyChanges()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { ExerciseWindows(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void ExerciseWindows()
    {
        // Load the shipped dictionaries without launching the tray app or contacting an account.
        var app = new Application();
        foreach (string name in new[] { "ThemeLight", "SharedStyles" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri($"/QuotaMonitor;component/UI/Themes/{name}.xaml", UriKind.Relative) });
        var prefs = new Preferences();
        using var theme = new ThemeManager(app, ThemePreference.Light, () => { });
        var widget = new WidgetWindow(prefs, theme);
        var details = new DashboardPopover(prefs, null);
        var settings = new SettingsWindow(prefs, () => { }, theme);
        var reading = DesktopItemTests.Sample();
        var liveInk = (SolidColorBrush)Visuals.Ink;
        foreach (var mode in new[] { ThemePreference.Dark, ThemePreference.Light })
        {
            theme.Apply(mode);
            Assert.Equal(theme.Color("Text.Primary"), liveInk.Color);
            foreach (int transparency in new[] { 0, 1, 50, 100 })
            {
                prefs.Transparency = transparency;
                widget.Present(reading, prefs);
                var content = (Grid)widget.Content;
                content.Measure(new Size(244,132));
                content.Arrange(new Rect(0,0,244,132));
                content.UpdateLayout();
                var bitmap = new RenderTargetBitmap(488,264,192,192,PixelFormats.Pbgra32);
                bitmap.Render(content);
                var frame = (Border)content.Children[^1];
                var plate = (Border)content.Children[^2];
                var color = ((SolidColorBrush)(transparency == 0 ? frame.Background : plate.Background)).Color;
                var expected = theme.Color("Widget.PlateDefault");
                Assert.Equal((expected.R, expected.G, expected.B), (color.R, color.G, color.B));
                Assert.Equal((byte)Math.Round((100 - transparency) / 100d * 255), color.A);
                Assert.Equal(transparency == 0 ? ClearTypeHint.Enabled : ClearTypeHint.Auto, RenderOptions.GetClearTypeHint(frame));
                if (transparency == 100) Assert.Equal(1, ((SolidColorBrush)frame.Background).Color.A);
            }
        }
        CheckStageTwoWindows(theme);
        CheckLayerSelector(settings, prefs, theme);
        CheckConnectionGuides(theme);
        CheckGlassOnWallpaper(widget, prefs, theme);
        details.Close();
        widget.Close();
        app.Shutdown();
    }

    private static void CheckGlassOnWallpaper(WidgetWindow widget, Preferences prefs, ThemeManager theme)
    {
        var now = DateTimeOffset.Now;
        var amounts = new Dictionary<string, int?> { ["chatgpt.7d"] = 77, ["grok.7d"] = 18, ["ag.gemini.7d"] = 8, ["ag.claude.7d"] = null };
        var services = DesktopItems.Catalog.Where(b => amounts.ContainsKey(b.BucketKey))
            .Select(b => b with { Remaining = amounts[b.BucketKey], LastSuccessAt = amounts[b.BucketKey].HasValue ? now : null })
            .GroupBy(b => b.Service).Select(g => ServiceSnapshot.Create(g.Key, true, null, g.ToArray())).ToArray();
        var reading = new QuotaReading(now, services);
        var widgetContent = (Grid)widget.Content;
        widget.Content = null;
        var scene = new Grid { Width = 308, Height = 228 };
        scene.Children.Add(widgetContent);
        foreach (var display in Enum.GetValues<DisplayMode>())
        foreach (var contrast in Enum.GetValues<WidgetTextContrast>())
        {
            prefs.Transparency = 72;
            prefs.Mode = display;
            prefs.TextContrast = contrast;
            widget.Present(reading, prefs);
            scene.Height = widget.MinHeight;
            scene.Background = contrast == WidgetTextContrast.Dark
                ? new LinearGradientBrush(Colors.AliceBlue, Colors.LightSteelBlue, 30)
                : new LinearGradientBrush(Color.FromRgb(23, 49, 90), Color.FromRgb(68, 115, 175), 30);
            scene.Measure(new Size(scene.Width, scene.Height));
            scene.Arrange(new Rect(0, 0, scene.Width, scene.Height));
            scene.UpdateLayout();
            if (contrast == WidgetTextContrast.Auto)
                Capture(scene, "glass-" + display.ToString().ToLowerInvariant());
            var visuals = Descendants(scene).ToArray();
            Assert.DoesNotContain(visuals, v => v is Button);
            Assert.DoesNotContain(visuals.OfType<FrameworkElement>(), v => v.ToolTip != null);
            var value = Assert.Single(visuals.OfType<TextBlock>(), t => t.Text == "77%");
            Assert.Equal(((SolidColorBrush)theme.Brush("Text.Primary", contrast != WidgetTextContrast.Dark)).Color,
                ((SolidColorBrush)value.Foreground).Color);
            if (display == DisplayMode.Rings)
            {
                var rings = visuals.OfType<System.Windows.Shapes.Ellipse>().Where(e => e.StrokeThickness == 4.5).ToList();
                Assert.Equal(4, rings.Count(e => e.Fill == Brushes.Transparent));
                var name = Assert.Single(visuals.OfType<TextBlock>(), t => t.Text == "ChatGPT");
                var caption = name.TransformToAncestor(scene).TransformBounds(new Rect(name.RenderSize));
                var number = value.TransformToAncestor(scene).TransformBounds(new Rect(value.RenderSize));
                Assert.True(caption.Top > number.Bottom);
                Assert.IsType<System.Windows.Media.Effects.DropShadowEffect>(value.Effect);
            }
            if (display == DisplayMode.CompactPill)
            {
                Assert.DoesNotContain(visuals.OfType<TextBlock>(), t => t.Text is "G" or "C");
                foreach (var (model, asset) in new[] { ("ChatGPT", "openai"), ("Gemini", "gemini"), ("Claude", "claude"), ("Grok", "grok") })
                {
                    var cell = Assert.Single(visuals.OfType<Grid>(), g => AutomationProperties.GetName(g).StartsWith(model + "，"));
                    var mark = Assert.Single(Descendants(cell).OfType<System.Windows.Shapes.Path>());
                    using var svg = typeof(ServiceMark).Assembly.GetManifestResourceStream($"QuotaMonitor.Brands.{asset}.svg");
                    var root = System.Xml.Linq.XElement.Load(Assert.IsAssignableFrom<System.IO.Stream>(svg));
                    Assert.Equal("0 0 24 24", (string?)root.Attribute("viewBox"));
                    Assert.Equal("evenodd", (string?)root.Attribute("fill-rule"));
                    var source = root.Elements(root.Name.Namespace + "path").Single();
                    Assert.Equal(Geometry.Parse((string)source.Attribute("d")!).ToString(), mark.Data.ToString());
                    Assert.True(mark.Data.IsFrozen);
                }
                Assert.Equal(3, visuals.OfType<Border>().Count(b => b.Width == 1 && b.Height == 12));
            }
        }
        var settings = new SettingsWindow(prefs, () => {}, theme);
        var controls = (FrameworkElement)settings.Content;
        controls.Measure(new Size(640, 500));
        controls.Arrange(new Rect(0, 0, 640, 500));
        controls.UpdateLayout();
        var buttons = Descendants(controls).OfType<Button>().ToArray();
        var dark = Assert.Single(buttons, b => AutomationProperties.GetName(b) == "深墨");
        dark.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(WidgetTextContrast.Dark, prefs.TextContrast);
        var stage = Assert.Single(Descendants(controls).OfType<Border>(), b => b.Height == 152);
        var wallpaper = stage.Background;
        Assert.Single(buttons, b => AutomationProperties.GetName(b) == "切换预览壁纸").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.NotSame(wallpaper, stage.Background);
        settings.Close();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is UIElement { Visibility: not Visibility.Visible }) continue;
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

}
