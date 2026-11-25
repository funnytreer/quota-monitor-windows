using System.Globalization;
using System.Windows.Media;
using Xunit;

namespace QuotaMonitor.Tests;

public class ThemeAndTimeTests
{
    [Theory]
    [InlineData(1, 14, 27, 36)]
    [InlineData(1.25, 18, 34, 45)]
    [InlineData(1.5, 21, 41, 54)]
    [InlineData(1.75, 25, 47, 63)]
    [InlineData(2, 28, 54, 72)]
    public void PlacementOffsetsFollowTheDestinationDpi(double scale, int x, int y, int tray) =>
        Assert.Equal((x, y, tray), (DisplayMetrics.Pixels(14, scale), DisplayMetrics.Pixels(27, scale), DisplayMetrics.Pixels(36, scale)));

    [Theory]
    [InlineData(0, 60)]
    [InlineData(50, 60)]
    [InlineData(144, 144)]
    [InlineData(240, 240)]
    [InlineData(360, 240)]
    public void AnimationRateUsesTheSupportedDisplayRange(int rate, int expected) =>
        Assert.Equal(expected, DisplayMetrics.ClampRefreshRate(rate));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0.99)]
    [InlineData(50, 0.50)]
    [InlineData(100, 0)]
    public void TransparencyChangesOnlyAlpha(int transparency, double expected)
    {
        var custom = Color.FromRgb(125, 193, 211);
        var surface = ThemeManager.Surface(WidgetSurfaceStyle.CustomColor, Colors.White, custom, transparency, false);
        Assert.Equal(custom, surface.Color);
        Assert.Equal(expected, surface.Alpha, 5);
        Assert.Equal(transparency >= 45, surface.LightInk);
        var themed = ThemeManager.Surface(WidgetSurfaceStyle.Themed, Colors.Ivory, custom, transparency, false);
        Assert.Equal(Colors.Ivory, themed.Color);
        Assert.Equal(expected, themed.Alpha, 5);
        var hud = ThemeManager.Surface(WidgetSurfaceStyle.HudGlass, Colors.Navy, custom, transparency, true);
        Assert.Equal(Colors.Navy, hud.Color);
        Assert.Equal(.28 * expected, hud.Alpha, 5);
        Assert.True(hud.LightInk);
    }

    [Theory]
    [InlineData(0, 0, 0, 0, false, true)]
    [InlineData(0, 0, 0, 65, false, true)]
    [InlineData(0, 0, 0, 66, false, true)]
    [InlineData(255, 255, 255, 44, true, false)]
    [InlineData(255, 255, 255, 45, true, true)]
    [InlineData(0, 255, 0, 20, true, false)]
    public void CustomInkUsesMaterialAndTransparencyUntilOverridden(byte r, byte g, byte b, int transparency, bool dark, bool lightInk)
    {
        var s = ThemeManager.Surface(WidgetSurfaceStyle.CustomColor, Colors.White, Color.FromRgb(r,g,b), transparency, dark);
        Assert.Equal(lightInk, s.LightInk);
        Assert.True(ThemeManager.ResolveLightInk(WidgetSurfaceStyle.CustomColor, s.Color, transparency, dark, WidgetTextContrast.Light));
        Assert.False(ThemeManager.ResolveLightInk(WidgetSurfaceStyle.HudGlass, s.Color, transparency, dark, WidgetTextContrast.Dark));
    }

    [Theory]
    [InlineData(null, "Unknown")]
    [InlineData(0, "Critical")]
    [InlineData(10, "Critical")]
    [InlineData(11, "Low")]
    [InlineData(19, "Low")]
    [InlineData(20, "Normal")]
    public void HealthBoundaries(int? remaining, string expected) =>
        Assert.Equal(expected, ThemeManager.Health(remaining, 20, 10).ToString());

    [Theory]
    [InlineData(0.1,"1m")]
    [InlineData(42,"42m")]
    [InlineData(60,"1h 0m")]
    [InlineData(134,"2h 14m")]
    [InlineData(1439,"23h 59m")]
    [InlineData(1440,"1d 0h")]
    [InlineData(4620,"3d 5h")]
    public void CountdownUsesLocalAbsoluteTime(double minutes, string relative)
    {
        var now = DateTimeOffset.Parse("2026-11-25T10:00:00Z");
        var reset = now.AddMinutes(minutes);
        var abs = reset.ToLocalTime().ToString(minutes < 1440 ? "HH:mm" : "MM/dd HH:mm", CultureInfo.InvariantCulture);
        Assert.Equal($"{relative} 后重置 ({abs})", CountdownFormatter.Format(reset, now));
    }

    [Fact]
    public void CountdownHandlesExpiredUnknownAndTextOnlySources()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal("重置时间已到，等待更新", CountdownFormatter.Format(now, now));
        Assert.Equal("重置时间已到，等待更新", CountdownFormatter.Format(now.AddMinutes(-1), now));
        Assert.Equal("暂无重置时间", CountdownFormatter.Format(null, now));
        Assert.Equal("周六", CountdownFormatter.Format(null, now, "周六"));
    }
}
