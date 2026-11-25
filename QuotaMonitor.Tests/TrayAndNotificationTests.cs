using System.Drawing;
using System.Runtime.InteropServices;
using Xunit;

namespace QuotaMonitor.Tests;

public class TrayAndNotificationTests
{
    [Theory]
    [InlineData(0, -135)]
    [InlineData(50, 0)]
    [InlineData(100, 135)]
    [InlineData(null, 0)]
    public void NeedleSpansTheDial(int? minimum, int expected) =>
        Assert.Equal((float)expected, new TraySummary(minimum, QuotaHealth.Unknown, "").Angle, 3);

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(48)]
    public void DownsampledDialKeepsTransparentGapAndAntialiasedEdges(int size)
    {
        using var icon = TrayIconRenderer.Draw(new TraySummary(50, QuotaHealth.Normal, ""), size, Color.White);
        using var image = icon.ToBitmap();
        Assert.Equal(new Size(size, size), image.Size);
        Assert.Equal(0, image.GetPixel(size / 2, size - 2).A);
        Assert.Contains(Enumerable.Range(0, size * size), i => image.GetPixel(i % size, i / size).A is > 0 and < 255);
    }

    [Fact]
    public void TrayIncludesDetailOnlyBucketsAndChoosesBadgeAcrossServices()
    {
        var p = new Preferences { LowThreshold = 5, CriticalThreshold = 3 };
        var b = new QuotaBucket("ag.other.5h", ServiceKind.Antigravity, "AG·Other", "Other", "5h", 8, null, 300,
            LastSuccessAt: DateTimeOffset.Now, IsWidgetEligible: false);
        var a = b with { BucketKey = "ag.claude.7d", ShortName = "AG·Claude", Remaining = 43 };
        p.PerBucketLowThresholds[a.BucketKey] = 50;
        var ag = ServiceSnapshot.Create(b.Service, true, null, [a, b]);
        var grok = ServiceSnapshot.Create(ServiceKind.Grok, false, null, [b with { Remaining = 1 }]);
        var summary = TrayIconRenderer.Summarize([grok, ag], p);
        Assert.Equal((8, QuotaHealth.Low, "AG·Other 8%"), (summary.Minimum, summary.Badge, summary.Tooltip));
        b = b with { Remaining = 2 };
        var critical = TrayIconRenderer.Summarize([ag with { Buckets = [a, b] }], p);
        Assert.Equal(QuotaHealth.Critical, critical.Badge);
        foreach (var (value, expectedColor) in new[] { (summary, Color.FromArgb(245,158,11)), (critical, Color.FromArgb(239,68,68)) })
        {
            using var icon = TrayIconRenderer.Draw(value, 32, Color.White);
            using var image = icon.ToBitmap();
            Assert.Equal(expectedColor.ToArgb(), image.GetPixel(26,26).ToArgb());
        }
    }

    [Fact]
    public void GeminiWebDoesNotAffectTheTrayEvenWhenConnected()
    {
        var now = DateTimeOffset.Now;
        var gemini = DesktopItems.Catalog.Single(b => b.BucketKey == "ag.gemini.7d") with { Remaining = 72, LastSuccessAt = now };
        var web = QuotaBucket.FromQuota(new Quota(Service.Gemini, 1, null) { LastSuccessAt = now }, 10080);
        var ag = ServiceSnapshot.Create(gemini.Service, true, null, [gemini]);
        var browser = ServiceSnapshot.Create(web.Service, true, null, [web]);
        var summary = TrayIconRenderer.Summarize([ag, browser], new());
        Assert.Equal(72, summary.Minimum);
        Assert.Equal(QuotaHealth.Normal, summary.Badge);
        Assert.Equal($"{gemini.ShortName} 72%", summary.Tooltip);
        Assert.Equal(TrayIconRenderer.Summarize([], new()), TrayIconRenderer.Summarize([browser], new()));
    }

    [Fact]
    public void TooltipIsBoundedAndKeepsUnicodeTextIntact()
    {
        var b = DesktopItems.Catalog[0] with { ShortName = new string('A', 61) + "🚀🚀", Remaining = 19, LastSuccessAt = DateTimeOffset.Now };
        var connected = ServiceSnapshot.Create(b.Service, true, null, [b]);
        string text = TrayIconRenderer.Summarize([connected], new()).Tooltip;
        Assert.InRange(text.Length, 1, 63);
        Assert.EndsWith("…", text);
        Assert.DoesNotContain(text, char.IsSurrogate);
        var none = TrayIconRenderer.Summarize([connected with { State = ServiceConnectionState.Unavailable }], new());
        Assert.Null(none.Minimum);
        Assert.Equal(QuotaHealth.Unknown, none.Badge);
    }

    [Fact]
    public void MissingWindowsNotificationIdentityFallsBackExactlyOnce()
    {
        int count = 0;
        NotificationService.Deliver(() => {}, () => count++);
        Assert.Equal(0, count);
        NotificationService.Deliver(() => throw new COMException(), () => count++);
        Assert.Equal(1, count);
        NotificationService.Deliver(() => throw new COMException(), () => throw new InvalidOperationException());
    }
}
