using System;
using System.Collections.Generic;

namespace QuotaMonitor;

internal enum Service { ChatGPT, Grok, Antigravity = 3 }
internal enum WidgetItem { Work, Grok, AntigravityGemini, AntigravityClaude }

internal sealed record Quota(Service Service, int? Remaining, DateTimeOffset? ResetsAt, int? WindowMinutes = null,
    string? Status = null, string? ResetText = null, string? Scope = null, string? DisplayName = null)
{
    public DateTimeOffset? LastSuccessAt { get; init; }
    public string? BucketKey { get; init; }

    public Quota AfterRead(Quota? previous, DateTimeOffset now)
    {
        if (Remaining.HasValue) return this with { LastSuccessAt = now };
        return previous?.LastSuccessAt is not null ? previous with { Status = Status } : this;
    }

    public string? LastReadingNote => LastSuccessNote(LastSuccessAt, Status);
    public static string? LastSuccessNote(DateTimeOffset? last, string? status) => last is DateTimeOffset time &&
        (!string.IsNullOrEmpty(status) || DateTimeOffset.Now - time > TimeSpan.FromMinutes(10))
        ? $"上次成功 {LocalTime(time)}" : null;

    public static string LocalTime(DateTimeOffset time)
    {
        var local = time.ToLocalTime();
        return local.ToString(local.Date == DateTime.Today ? "HH:mm" : "M/d HH:mm");
    }

    public string Name => DisplayName ?? Service.ToString();
}

internal sealed record QuotaReading(DateTimeOffset? UpdatedAt, IReadOnlyList<ServiceSnapshot> Services)
{
    public bool HasCompletedCheck { get; init; }
    public string CheckTimeText => UpdatedAt is DateTimeOffset time ? $"检查于 {Quota.LocalTime(time)}"
        : HasCompletedCheck ? "尚无成功读数" : "正在检查…";
}
