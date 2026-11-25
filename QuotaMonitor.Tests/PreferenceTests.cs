using System.IO;
using Xunit;

namespace QuotaMonitor.Tests;

public sealed class PreferenceTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory();
    private string FilePath => Path.Combine(_dir.FullName, "settings.json");
    public void Dispose() => _dir.Delete(true);

    [Theory]
    [InlineData(.9, .9)]
    [InlineData(1, 1)]
    [InlineData(1.15, 1.15)]
    [InlineData(1.3, 1.3)]
    [InlineData(.25, 1)]
    public void WidgetScaleRoundTripsIndependentlyOfCustomSize(double saved, double expected)
    {
        new Preferences { WidgetScale = saved, WidgetWidth = 312 }.Save(FilePath);
        var restored = Preferences.Load(FilePath);
        Assert.Equal(expected, restored.WidgetScale);
        Assert.Equal(312, restored.WidgetWidth);
    }

    [Fact]
    public void V4MigratesOnceAndPreservesPlacementConnectionsAndChoices()
    {
        File.WriteAllText(FilePath, """
        {"AppearanceVersion":4,"Mode":1,"Background":"#173052","Transparency":35,"WidgetX":-1810,"WidgetY":0,
         "WidgetWidth":216,"WidgetHeight":138,"WidgetVisible":false,"GrokConnected":true,"GeminiEnabled":true,
         "GeminiHasReadUsage":true,"HiddenWidgetItems":[0,1,2,3,4],"LowEnabled":false,"LowThreshold":37,
         "FastEnabled":false,"FastThreshold":9}
        """);
        var p = Preferences.Load(FilePath);
        Assert.Equal(5, p.AppearanceVersion);
        Assert.Equal((DisplayMode.Rings, WidgetSurfaceStyle.CustomColor, "#173052", 35),
            (p.Mode, p.SurfaceStyle, p.CustomBackground, p.Transparency));
        Assert.Equal((-1810, 0, 216, 138), (p.WidgetX, p.WidgetY, p.WidgetWidth, p.WidgetHeight));
        Assert.True(p.GrokConnected);
        Assert.False(p.WidgetVisible || p.LowEnabled || p.FastEnabled);
        Assert.Equal((37, 9), (p.LowThreshold, p.FastThreshold));
        Assert.True(p.HiddenBuckets.SetEquals(p.WidgetBucketOrder));
        Assert.Equal((ThemePreference.System, WidgetLayerMode.DesktopPinned), (p.ThemeMode, p.LayerMode));
        Assert.Contains("\"AppearanceVersion\": 5", File.ReadAllText(FilePath));
        p.HiddenBuckets.Remove("chatgpt.5h");
        p.Save(FilePath);
        Assert.DoesNotContain("chatgpt.5h", Preferences.Load(FilePath).HiddenBuckets);
    }

    [Fact]
    public void MissingPropertiesAndLegacyDefaultColorUseTheme()
    {
        File.WriteAllText(FilePath, """{"Background":"#dfeafc","Transparency":35,"WidgetY":-12}""");
        var p = Preferences.Load(FilePath);
        Assert.Equal((WidgetSurfaceStyle.Themed, 35, -12), (p.SurfaceStyle, p.Transparency, p.WidgetY));
        Assert.True(p.HiddenBuckets.SetEquals(["chatgpt.5h"]));
        Assert.Equal(WidgetTextContrast.Auto, p.TextContrast);
        Assert.Equal(1d, p.WidgetScale);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 2)]
    [InlineData(97, 0)]
    public void TextContrastSurvivesLegacyRingFill(int choice, int expected)
    {
        File.WriteAllText(FilePath, $$"""{"AppearanceVersion":5,"RingFillColor":"#164466","CustomBackground":"#CCDDEE","TextContrast":{{choice}}}""");
        var loaded = Preferences.Load(FilePath);
        Assert.Equal(expected, (int)loaded.TextContrast);
        Assert.Equal("#CCDDEE", loaded.CustomBackground);
        loaded.Save(FilePath);
        Assert.DoesNotContain("RingFillColor", File.ReadAllText(FilePath));
        Assert.Equal(loaded.TextContrast, Preferences.Load(FilePath).TextContrast);
    }

    [Fact]
    public void InvalidValuesAreBoundedAndBucketOrderSurvivesReload()
    {
        File.WriteAllText(FilePath, """
        {"AppearanceVersion":5,"WidgetWidth":0,"WidgetHeight":1200,"Transparency":150,"CriticalThreshold":-1,
         "LowThreshold":-5,"FastThreshold":500,"PollingIntervalMinutes":8,"CustomBackground":"invalid",
         "SurfaceStyle":700,"QuietStart":"28:15","QuietEnd":null,"WidgetBucketOrder":["grok.7d","grok.7d"],
         "PerBucketLowThresholds":{"chatgpt.5h":-2,"grok.7d":103}}
        """);
        var p = Preferences.Load(FilePath);
        Assert.Equal((174,800,100,1,1,100,5), (p.WidgetWidth,p.WidgetHeight,p.Transparency,p.CriticalThreshold,p.LowThreshold,p.FastThreshold,p.PollingIntervalMinutes));
        Assert.Equal(("23:00","08:00","#DFEAFC", WidgetSurfaceStyle.Themed), (p.QuietStart,p.QuietEnd,p.CustomBackground,p.SurfaceStyle));
        Assert.Equal((1,100), (p.PerBucketLowThresholds["chatgpt.5h"],p.PerBucketLowThresholds["grok.7d"]));
        Assert.Equal("grok.7d", p.WidgetBucketOrder[0]);
        Assert.Equal(5,p.WidgetBucketOrder.Distinct().Count());
    }

    [Fact]
    public void ExistingProfileDropsOnlyRetiredWebQuotaChoices()
    {
        File.WriteAllText(FilePath, """
            {"AppearanceVersion":5,"GeminiEnabled":true,"GeminiHasReadUsage":true,"GrokConnected":true,
             "WidgetBucketOrder":["gemini.5h","ag.claude.7d","grok.7d","gemini.7d","ag.gemini.7d"],
             "HiddenBuckets":["gemini.7d","ag.claude.7d"],"PerBucketLowThresholds":{"gemini.5h":10,"ag.gemini.7d":19}}
            """);
        var p = Preferences.Load(FilePath);
        Assert.Equal(new[] { "ag.claude.7d", "grok.7d", "ag.gemini.7d", "chatgpt.7d", "chatgpt.5h" }, p.WidgetBucketOrder);
        Assert.True(p.HiddenBuckets.SetEquals(["ag.claude.7d"]));
        Assert.Equal(19, Assert.Single(p.PerBucketLowThresholds).Value);
        Assert.True(p.GrokConnected);
        var saved = File.ReadAllText(FilePath);
        Assert.DoesNotContain("GeminiEnabled", saved);
        Assert.DoesNotContain("GeminiHasReadUsage", saved);
        Assert.DoesNotContain("\"gemini.", saved);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{")]
    [InlineData("null")]
    public void FirstRunAndUnreadableConfigHaveUsableDefaults(string? content)
    {
        if (content != null) File.WriteAllText(FilePath, content);
        var p = Preferences.Load(FilePath);
        Assert.Equal((228,116,5), (p.WidgetWidth,p.WidgetHeight,p.AppearanceVersion));
        Assert.True(p.WidgetVisible);
    }
}
