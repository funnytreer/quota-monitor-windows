using Xunit;

namespace QuotaMonitor.Tests;

public class DesktopItemTests
{
    internal static QuotaReading Sample()
    {
        var now = DateTimeOffset.Now;
        var catalog = DesktopItems.Catalog.Select((b, i) => b with
        {
            Remaining = new[] { 77, 93, 83, 9, 18 }[i], ResetsAt = now.AddHours(2 + i * 15),
            LastSuccessAt = now
        }).ToList();
        var models = new AntigravityReading([
            new("Gemini Models", [new("5 小时", 93, now.AddHours(3), Window: "5h"), new("每周", 83, now.AddDays(4), Window: "weekly")]),
            new("Claude and GPT models", [new("每周", 9, now.AddDays(2), Window: "weekly")]),
            new("Gemini 4 Argon", [new("每周", 100, now.AddDays(5), Window: "weekly")])]) { LastSuccessAt = now };
        var services = Enum.GetValues<ServiceKind>().Select(kind =>
                ServiceSnapshot.Create(kind, true, null, kind == ServiceKind.Antigravity
                    ? models.ToBuckets() : catalog.Where(b => b.Service == kind).ToArray(),
                    resets: kind == ServiceKind.ChatGPT ? new(2, [now.AddDays(1), now.AddDays(3)], "") : null)).ToArray();
        return new QuotaReading(now, services);
    }

    [Fact]
    public void SelectionRespectsSavedOrderAndKeepsWeekSeparateFromFiveHours()
    {
        var p = new Preferences();
        p.WidgetBucketOrder.Remove("grok.7d");
        p.WidgetBucketOrder.Insert(0, "grok.7d");
        Assert.Equal(new[] { "Grok", "ChatGPT", "Gemini", "Claude" }, DesktopItems.Select(Sample(), p).Select(q => q.Name));
        p.HiddenBuckets.Remove("chatgpt.5h");
        var result = DesktopItems.Select(Sample(), p);
        Assert.Equal(77, result.Single(q => q.BucketKey == "chatgpt.7d").Remaining);
        Assert.Equal(93, result.Single(q => q.BucketKey == "chatgpt.5h").Remaining);
        Assert.Equal(5, result.Count);
    }

    [Fact]
    public void LegacyWebSelectionDoesNotCreateADesktopItem()
    {
        var p = new Preferences();
        p.WidgetBucketOrder.Insert(0, "gemini.5h");
        Assert.Equal(new[] { "ChatGPT", "Gemini", "Claude", "Grok" }, DesktopItems.Select(Sample(), p).Select(q => q.Name));
    }

    [Theory]
    [InlineData(0, "待重置")]
    [InlineData(45, "45m")]
    [InlineData(134, "2h14m")]
    [InlineData(3180, "2d5h")]
    public void WidgetCountdownFitsInOneToken(int minutes, string expected)
    {
        var now = DateTimeOffset.Now;
        Assert.Equal(expected, CountdownFormatter.Compact(now.AddMinutes(minutes), now));
    }
}
