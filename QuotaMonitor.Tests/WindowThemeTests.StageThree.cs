using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Xunit;

namespace QuotaMonitor.Tests;

public partial class WindowThemeTests
{
    private static void CheckConnectionGuides(ThemeManager theme)
    {
        bool changed = false;
        var gemini = new GeminiWizardWindow(new GeminiConnection(), false, _ => changed = true);
        var ag = new AntigravityGuideWindow(() => Task.CompletedTask);
        var grok = new GrokConnectionWindow(new GrokWebClient(), true, _ => changed = true);
        ag.Present(new AntigravityReading([new("Gemini Models", [])]));
        static void Layout(Window window, double width, double height)
        {
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(width, height));
            content.Arrange(new Rect(0, 0, width, Math.Min(height, content.DesiredSize.Height)));
            content.UpdateLayout();
        }
        static bool HasButton(Window window, string name) => Descendants((FrameworkElement)window.Content)
            .OfType<Button>().Any(b => AutomationProperties.GetName(b) == name);
        foreach (var mode in new[] { ThemePreference.Dark, ThemePreference.Light })
        {
            theme.Apply(mode);
            Layout(gemini, gemini.Width, 580);
            Layout(ag, ag.Width, 400);
            Layout(grok, 880, 680);
            if (mode == ThemePreference.Light) Capture((FrameworkElement)gemini.Content, "gemini", "QUOTA_GUIDE_PREVIEW_DIR");
            if (mode == ThemePreference.Dark) Capture((FrameworkElement)ag.Content, "antigravity", "QUOTA_GUIDE_PREVIEW_DIR");
            Assert.True(HasButton(gemini, "注册并复制扩展目录") && HasButton(gemini, "复制 chrome://extensions"));
            Assert.True(HasButton(ag, "立即重新检测") && HasButton(ag, "复制登录命令"));
            Assert.True(HasButton(grok, "读取并开始监测") && HasButton(grok, "断开并清除登录"));
            Assert.Equal(theme.Color("Surface.Window"), ((SolidColorBrush)grok.Background).Color);
        }
        foreach (Window window in new Window[] { ag, grok, gemini })
        {
            Assert.False(window.AllowsTransparency);
            Assert.NotNull(System.Windows.Shell.WindowChrome.GetWindowChrome(window));
        }
        Assert.False(changed);
        gemini.Close();
        ag.Close();
        grok.Close();

        // Exercise the startup preferences and desktop UI together without replacing the user's configuration.
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            string path = Path.Combine(directory.FullName, "settings.json");
            File.WriteAllText(path, """
                {"AppearanceVersion":4,"Mode":1,"WidgetX":-400,"WidgetY":0,"WidgetWidth":296,"WidgetHeight":208,
                 "GrokConnected":true,"GeminiEnabled":true,"LowThreshold":37,"FastThreshold":12,"Transparency":72}
                """);
            var loaded = Preferences.Load(path);
            var desktop = new WidgetWindow(loaded, theme);
            desktop.Present(DesktopItemTests.Sample(), loaded);
            Layout(desktop, desktop.Width, desktop.Height);
            Assert.Equal(312, desktop.Width);
            Assert.Equal(224, desktop.Height);
            var saved = Preferences.Load(path);
            Assert.Equal((5,-400,0,37,12), (saved.AppearanceVersion,saved.WidgetX,saved.WidgetY,saved.LowThreshold,saved.FastThreshold));
            Assert.True(saved.GrokConnected);
            desktop.Close();
        }
        finally { directory.Delete(true); }
    }
}
