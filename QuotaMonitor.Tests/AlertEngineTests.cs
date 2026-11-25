using Xunit;

namespace QuotaMonitor.Tests;

public class AlertEngineTests
{
    private readonly DateTimeOffset _start = new(new DateTime(2026, 11, 25, 16, 0, 0, DateTimeKind.Local));
    private readonly Preferences _p = new();
    private readonly AlertEngine _engine = new();

    private static ServiceSnapshot Sample(string key, DateTimeOffset time, int value, DateTimeOffset? reset = null, string? resetText = null)
    {
        var kind = key.Split('.')[0] switch { "chatgpt" => ServiceKind.ChatGPT, "ag" => ServiceKind.Antigravity,
            "grok" => ServiceKind.Grok, _ => throw new ArgumentOutOfRangeException(nameof(key)) };
        var b = new QuotaBucket(key, kind, key, key, "每周", value, reset, 10080, resetText, LastSuccessAt: time);
        return ServiceSnapshot.Create(kind, true, null, [b]);
    }

    private IReadOnlyList<BucketAlert> Read(int minute, int value, DateTimeOffset? reset = null,
        string? resetText = null, string key = "chatgpt.5h")
    {
        var t = _start.AddMinutes(minute);
        return _engine.Observe([Sample(key, t, value, reset, resetText)], _p, t);
    }

    [Theory]
    [InlineData("chatgpt.5h")]
    [InlineData("chatgpt.7d")]
    [InlineData("ag.claude.5h")]
    [InlineData("ag.gemini.7d")]
    [InlineData("ag.gemini-4-argon.7d")]
    [InlineData("grok.7d")]
    [InlineData("ag.gemini.5h")]
    [InlineData("ag.claude.7d")]
    public void EveryServiceAndWindowHasIndependentThresholdAndCooldown(string key)
    {
        _p.PerBucketLowThresholds[key] = 40;
        _p.HiddenBuckets.Add(key);
        Assert.Empty(Read(0, 90, key: key));
        Assert.Empty(Read(1, 90, key: "chatgpt.other"));
        var alert = Assert.Single(Read(3, 39, key: key));
        Assert.True(alert.EnteredLow);
        Assert.Equal(51, alert.Drop);
        Assert.Equal(key, alert.Bucket.BucketKey);
        Assert.Empty(Read(3, 39, key: key));
        Assert.Empty(Read(5, 21, key: key));
        Assert.Empty(Read(16, 31, key: key));
        Assert.Equal(29, Assert.Single(Read(19, 2, key: key)).Drop);
        Assert.True(Assert.Single(Read(20, 17, key: "chatgpt.other")).EnteredLow);
    }

    [Fact]
    public void RecoveryRequiresBothAPeriodChangeAndAPreviouslyLowReading()
    {
        var reset = _start.AddHours(1);
        Assert.Empty(Read(0, 3, reset));
        Assert.Empty(Read(1, 90, reset));
        Assert.Empty(Read(2, 100, reset.AddDays(1)));
        Assert.True(Assert.Single(Read(3, 4, reset.AddDays(1))).EnteredLow);
        Assert.True(Assert.Single(Read(4, 92, reset.AddDays(2))).Recovered);
        Assert.Empty(Read(5, 91, reset.AddDays(2)));
        _p.ResetRecoveryEnabled = false;
        Assert.Single(Read(6, 0, reset.AddDays(2)));
        Assert.Empty(Read(7, 100, reset.AddDays(3)));
    }

    [Fact]
    public void ResetTextWorksForWebQuotaAndResetsTheFastDropWindow()
    {
        Assert.Empty(Read(0, 90, resetText: "周三"));
        Assert.Single(Read(1, 10, resetText: "周三"));
        Assert.True(Assert.Single(Read(2, 40, resetText: "周四")).Recovered);
        Assert.Empty(Read(3, 38, resetText: "周四"));
    }

    [Theory]
    [InlineData("23:00", true)]
    [InlineData("07:59", true)]
    [InlineData("08:00", false)]
    [InlineData("22:59", false)]
    public void QuietHoursRespectCrossMidnightBoundaries(string time, bool expected)
    {
        _p.QuietHoursEnabled = true;
        Assert.Equal(expected, AlertEngine.IsQuiet(_p, TimeOnly.Parse(time)));
    }

    [Fact]
    public void DropsDuringQuietHoursAdvanceStateAndCooldownWithoutReplay()
    {
        _p.QuietHoursEnabled = true;
        var t = new DateTimeOffset(new DateTime(2026, 11, 25, 7, 55, 0, DateTimeKind.Local));
        IReadOnlyList<BucketAlert> At(int minute, int n) =>
            _engine.Observe([Sample("grok.7d", t.AddMinutes(minute), n)], _p, t.AddMinutes(minute));
        Assert.Empty(At(0, 70));
        Assert.Empty(At(2, 10));
        Assert.Empty(At(6, 9));
    }

    [Fact]
    public void LogoutForgetsBaselineAndFailedReadsNeverCauseRecoveryOrDrops()
    {
        var reset = _start.AddDays(1);
        Read(0, 5, reset);
        var failed = Sample("chatgpt.5h", _start.AddMinutes(1), 100, reset.AddDays(1)) with { State = ServiceConnectionState.Unavailable };
        Assert.Empty(_engine.Observe([failed], _p, _start.AddMinutes(1)));
        Assert.Empty(_engine.Observe([failed with { State = ServiceConnectionState.LoginRequired }], _p, _start.AddMinutes(2)));
        Assert.Empty(Read(3, 99, reset.AddDays(1)));
        Assert.Empty(Read(2, 0, reset.AddDays(1)));
    }
}
