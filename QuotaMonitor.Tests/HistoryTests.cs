using System.IO;
using System.Text.Json;
using Xunit;

namespace QuotaMonitor.Tests;

public sealed class HistoryTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory();
    private string FilePath => Path.Combine(_dir.FullName,"history.json");
    public void Dispose() => _dir.Delete(true);

    private static QuotaBucket Sample(DateTimeOffset time, int? remaining, string? status = null) =>
        new("chatgpt.5h", ServiceKind.ChatGPT, "ChatGPT 5h", "ChatGPT", "5 小时",
            remaining, null, 300, Status: status, LastSuccessAt: time);

    [Fact]
    public void DeduplicatesSamplesAndComputesDropAcrossRestarts()
    {
        var now = DateTimeOffset.UtcNow;
        using (var store = new UsageHistoryStore(FilePath, () => now))
        {
            store.Observe([Sample(now.AddMinutes(-20),100), Sample(now.AddMinutes(-10),85),
                Sample(now.AddMinutes(-8),85), Sample(now.AddMinutes(-5),85), Sample(now.AddMinutes(-2),61),
                Sample(now.AddMinutes(-3),99), Sample(now,null), Sample(now,1,"读取失败")]);
            Assert.Equal(24, store.GetRecentDrop("chatgpt.5h", TimeSpan.FromMinutes(15), now));
        }
        using var json = JsonDocument.Parse(File.ReadAllText(FilePath));
        Assert.Equal(4, json.RootElement.GetProperty("chatgpt.5h").GetArrayLength());
        using var reopened = new UsageHistoryStore(FilePath, () => now);
        Assert.Equal(24, reopened.GetRecentDrop("chatgpt.5h", TimeSpan.FromMinutes(15), now));
        Assert.Equal(0, reopened.GetRecentDrop("grok.7d", TimeSpan.FromMinutes(15), now));
    }

    [Fact]
    public void CorruptCacheIsRecoverableAndOldBucketsArePruned()
    {
        File.WriteAllText(FilePath, "{");
        var now = DateTimeOffset.UtcNow;
        using (var store = new UsageHistoryStore(FilePath, () => now))
        {
            store.Observe([Sample(now,88)]);
            now = now.AddHours(25);
            store.Observe([Sample(now,71) with { BucketKey = "gemini.7d" }]);
        }
        using var json = JsonDocument.Parse(File.ReadAllText(FilePath));
        Assert.False(json.RootElement.TryGetProperty("chatgpt.5h", out _));
        Assert.True(json.RootElement.TryGetProperty("gemini.7d", out _));
    }

    [Fact]
    public void FifteenMinutePeakExcludesExpiredSamplesAndUsesCurrentRemaining()
    {
        var now = DateTimeOffset.UtcNow;
        using var store = new UsageHistoryStore(FilePath, () => now);
        store.Observe([Sample(now.AddMinutes(-16), 100), Sample(now.AddMinutes(-15), 80),
            Sample(now.AddMinutes(-4), 20), Sample(now, 73)]);
        Assert.Equal(7, store.GetRecentDrop("chatgpt.5h", TimeSpan.FromMinutes(15), now));
    }
}
