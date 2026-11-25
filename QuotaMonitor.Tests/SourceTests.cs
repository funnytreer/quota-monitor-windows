using System.Text.Json;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace QuotaMonitor.Tests;

public class SourceTests
{
    private const string Primary = """{"usedPercent":26,"windowDurationMins":300,"resetsAt":1800000000}""";
    private const string Weekly = """{"usedPercent":4,"windowDurationMins":10080,"resetsAt":1800050000}""";

    [Theory]
    [InlineData(true, true, 2, 96)]
    [InlineData(true, false, 1, 74)]
    [InlineData(false, true, 1, 96)]
    [InlineData(false, false, 0, null)]
    public void CodexKeepsBothActualWindowsAndCredits(bool primary, bool secondary, int count, int? summary)
    {
        const string template = """
        {"rateLimitsByLimitId":{"codex":{"primary":PRIMARY,"secondary":WEEKLY}},
         "rateLimitResetCredits":{"availableCount":1,"credits":[{"status":"available","expiresAt":1800000000}]}}
        """;
        string json = template.Replace("PRIMARY", primary ? Primary : "null").Replace("WEEKLY", secondary ? Weekly : "null");
        using var doc = JsonDocument.Parse(json);
        var reading = CodexAccountSource.ParseAccount(doc.RootElement);
        Assert.Equal(summary, reading.WorkQuota.Remaining);
        Assert.Equal(count, reading.Buckets.Count);
        if (primary) Assert.Contains(reading.Buckets, b => b is { BucketKey: "chatgpt.5h", Remaining: 74, WindowMinutes: 300 });
        if (secondary) Assert.Contains(reading.Buckets, b => b is { BucketKey: "chatgpt.7d", Remaining: 96, WindowMinutes: 10080 });
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1800000000), Assert.Single(reading.Resets.Expirations));
    }

    [Fact]
    public void CodexUsesLegacyEnvelopeAndDisregardsOtherServices()
    {
        using var valid = JsonDocument.Parse("""{"rateLimits":{"primary":{"usedPercent":103,"resetsAt":9223372036854775807}}}""");
        var bucket = Assert.Single(CodexAccountSource.ParseAccount(valid.RootElement).Buckets);
        Assert.Equal("chatgpt.5h", bucket.BucketKey);
        Assert.Equal(0, bucket.Remaining);
        Assert.Null(bucket.ResetsAt);
        using var foreign = JsonDocument.Parse("""{"rateLimits":{"limitId":"other","primary":{"usedPercent":20}}}""");
        Assert.Empty(CodexAccountSource.ParseAccount(foreign.RootElement).Buckets);
    }

    [Fact]
    public void AntigravityQuotaPollOptsOutOfConsoleLaunchingUpdater()
    {
        const string option = "AGY_CLI_DISABLE_AUTO_UPDATE";
        var inherited = Environment.GetEnvironmentVariable(option);
        var start = AntigravityQuotaSource.CreateUsageCommand(Environment.CurrentDirectory);
        Assert.Equal("true", start.Environment[option]);
        Assert.Equal(inherited, Environment.GetEnvironmentVariable(option));
        Assert.True(start.CreateNoWindow && start.RedirectStandardOutput && start.RedirectStandardError);
        Assert.False(start.UseShellExecute);
        Assert.Equal(new[] { "--print-timeout", "12s", "--output-format", "json", "-p", "/usage" }, start.ArgumentList.Skip(2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedUsagePollCleansUpAChildHoldingItsConsolePipes(bool cancel)
    {
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            var start = AntigravityQuotaSource.CreateUsageCommand(folder.FullName);
            start.FileName = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            start.ArgumentList.Clear();
            foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", """
                $s = New-Object Diagnostics.ProcessStartInfo
                $s.FileName = Join-Path $PSHOME 'powershell.exe'
                $s.Arguments = '-NoLogo -NoProfile -NonInteractive -Command Start-Sleep -Seconds 18'
                $s.UseShellExecute = $false
                $s.CreateNoWindow = $true
                $child = [Diagnostics.Process]::Start($s)
                [IO.File]::WriteAllText((Join-Path $pwd 'child.txt'), $child.Id.ToString())
                $child.WaitForExit()
                """ }) start.ArgumentList.Add(arg);
            using var source = new CancellationTokenSource();
            var result = AntigravityQuotaSource.ReadUsageAsync(start, TimeSpan.FromSeconds(cancel ? 20 : 6), source.Token);
            string path = Path.Combine(folder.FullName, "child.txt");
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(path) || new FileInfo(path).Length == 0) await Task.Delay(40, guard.Token);
            using var child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(path)));
            if (cancel)
            {
                source.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => result.WaitAsync(TimeSpan.FromSeconds(4)));
            }
            else Assert.Equal("读取超时", (await result.WaitAsync(TimeSpan.FromSeconds(9))).Status);
            Assert.True(child.WaitForExit(2000), "额度轮询停止后子进程仍在运行");
        }
        finally { folder.Delete(true); }
    }

    [Fact]
    public void AntigravityProjectsAllWindowsAndOnlyTwoDesktopSummaries()
    {
        const string source = """
        {"status":"SUCCESS","command":{"name":"usage","data":{"groups":[
          {"name":"Gemini Models","buckets":[{"id":"gemini-weekly","window":"weekly","remaining_fraction":0.74},{"window":"5h","remaining_fraction":1}]},
          {"name":"Third party models","buckets":[{"id":"3p-weekly","window":"weekly","remaining_fraction":0.51},{"window":"5h","remaining_fraction":0,"reset_time":"2026-11-25T15:30:00Z"}]},
          {"name":"Gemini 4 Argon","buckets":[{"window":"weekly","remaining_fraction":0.8}]}]}}}
        """;
        var reading = AntigravityQuotaSource.Parse(source) with { LastSuccessAt = DateTimeOffset.UtcNow };
        var buckets = reading.ToBuckets().ToDictionary(b => b.BucketKey);
        Assert.Equal(5, buckets.Count);
        Assert.Equal(80, buckets["ag.gemini-4-argon.7d"].Remaining);
        Assert.False(buckets["ag.gemini-4-argon.7d"].IsWidgetEligible);
        Assert.Equal(100, buckets["ag.gemini.5h"].Remaining);
        Assert.Equal(0, buckets["ag.claude.5h"].Remaining);
        Assert.Equal(DateTimeOffset.Parse("2026-11-25T15:30:00Z"), buckets["ag.claude.5h"].ResetsAt);
        Assert.Equal("AG·Claude", buckets["ag.claude.7d"].ShortName);
        Assert.Equal(new[] { 74, 51 }, reading.ToBuckets().Where(b => b.IsWidgetEligible).OrderBy(b => b.BucketKey == "ag.gemini.7d" ? 0 : 1).Select(q => q.Remaining!.Value));
    }

    [Fact]
    public void WebPeriodsPreserveResetTextAndDoNotCollideWithAntigravity()
    {
        var now = DateTimeOffset.UtcNow;
        var website = new Quota(Service.Gemini, 42, null, 300, ResetText: "明天 14:00") { LastSuccessAt = now };
        var bucket = QuotaBucket.FromQuota(website, 300);
        Assert.Equal(("gemini.5h", "明天 14:00", now), (bucket.BucketKey, bucket.ResetText, bucket.LastSuccessAt));
        Assert.Equal("gemini.7d", QuotaBucket.FromQuota(website, 10080).BucketKey);
        Assert.Equal("grok.7d", QuotaBucket.FromQuota(new(Service.Grok, 80, null), 10080).BucketKey);
        Assert.Equal(ServiceConnectionState.Unavailable, ServiceSnapshot.Create(ServiceKind.GeminiWeb, true, "读取失败", [bucket]).State);
        Assert.Equal(ServiceConnectionState.Connected, ServiceSnapshot.Create(ServiceKind.GeminiWeb, true, null, [bucket]).State);
    }
}
