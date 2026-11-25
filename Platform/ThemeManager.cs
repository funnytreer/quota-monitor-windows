using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace QuotaMonitor;

internal enum QuotaHealth { Unknown, Normal, Low, Critical }
internal readonly record struct WidgetSurface(Color Color, double Alpha, bool LightInk);

internal sealed class ThemeManager : IDisposable
{
    private readonly Application _app;
    private readonly ResourceDictionary _light, _dark;
    private readonly Action _changed;
    private bool _disposed;
    public ThemePreference Preference { get; private set; }
    public bool IsDark { get; private set; }

    public ThemeManager(Application app, ThemePreference preference, Action changed)
    {
        _app = app;
        _changed = changed;
        _light = app.Resources.MergedDictionaries[0];
        _dark = new ResourceDictionary { Source = new Uri("/QuotaMonitor;component/UI/Themes/ThemeDark.xaml", UriKind.Relative) };
        foreach (var palette in new[] { _light, _dark })
            foreach (var key in palette.Keys)
                if (palette[key] is Brush { IsFrozen: false } brush) brush.Freeze();
        Apply(preference);
        SystemEvents.UserPreferenceChanged += OnSystemChanged;
    }

    public void Apply(ThemePreference preference)
    {
        Preference = preference;
        bool dark = preference == ThemePreference.Dark || (preference == ThemePreference.System && !UsesLightTheme("AppsUseLightTheme"));
        if (dark == IsDark) return;
        IsDark = dark;
        _app.Resources.MergedDictionaries[0] = dark ? _dark : _light;
        Visuals.RefreshThemeBrushes();
        foreach (Window window in _app.Windows) NativeChrome.ApplyTheme(window);
        _changed();
    }

    private void OnSystemChanged(object sender, UserPreferenceChangedEventArgs e) =>
        _app.Dispatcher.InvokeAsync(() =>
        {
            if (!_disposed && Preference == ThemePreference.System) Apply(Preference);
        });

    internal static bool UsesLightTheme(string setting)
    {
        try
        {
            return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                setting, 1) is not 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return true; }
    }

    public Brush Brush(string key, bool dark) => (Brush)(dark ? _dark : _light)[key];
    public Color Color(string key) => ((SolidColorBrush)Brush(key, IsDark)).Color;

    public static WidgetSurface Surface(WidgetSurfaceStyle style, Color themed, Color custom, int transparency, bool dark,
        WidgetTextContrast contrast = WidgetTextContrast.Auto)
    {
        transparency = Math.Clamp(transparency, 0, 100);
        var color = style == WidgetSurfaceStyle.CustomColor ? custom : themed;
        bool lightInk = ResolveLightInk(style, custom, transparency, dark, contrast);
        double alpha = (100 - transparency) / 100.0;
        if (style == WidgetSurfaceStyle.HudGlass) alpha *= 0.28;
        return new(color, alpha, lightInk);
    }

    internal static bool ResolveLightInk(WidgetSurfaceStyle style, Color customBg, int transparency,
        bool systemDark, WidgetTextContrast contrast) => contrast switch
    {
        WidgetTextContrast.Light => true,
        WidgetTextContrast.Dark => false,
        _ when transparency >= 45 || style == WidgetSurfaceStyle.HudGlass => true,
        _ when style == WidgetSurfaceStyle.CustomColor => .2126 * customBg.R + .7152 * customBg.G + .0722 * customBg.B < 128,
        _ => systemDark
    };

    internal static Brush InkOnSolidColor(Color color)
    {
        static double Linear(byte channel)
        {
            double s = channel / 255d;
            return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4);
        }
        double luminance = .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        return (luminance + .05) / .05 >= 1.05 / (luminance + .05) ? Brushes.Black : Brushes.White;
    }

    public static QuotaHealth Health(int? remaining, int low, int critical) => !remaining.HasValue ? QuotaHealth.Unknown
        : remaining <= critical ? QuotaHealth.Critical : remaining < low ? QuotaHealth.Low : QuotaHealth.Normal;

    public void Dispose()
    {
        _disposed = true;
        SystemEvents.UserPreferenceChanged -= OnSystemChanged;
    }
}
