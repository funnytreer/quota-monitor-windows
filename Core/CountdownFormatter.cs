using System;
using System.Globalization;

namespace QuotaMonitor;

internal static class CountdownFormatter
{
    public static string Format(DateTimeOffset? resetsAt, DateTimeOffset now, string? resetText = null)
    {
        if (resetsAt is not DateTimeOffset reset)
            return string.IsNullOrWhiteSpace(resetText) ? "暂无重置时间" : resetText;
        var remaining = reset - now;
        if (remaining <= TimeSpan.Zero) return "重置时间已到，等待更新";
        string relative = Relative(remaining, " ");
        string absolute = reset.ToLocalTime().ToString(remaining.TotalDays < 1 ? "HH:mm" : "MM/dd HH:mm",
            CultureInfo.InvariantCulture);
        return $"{relative} 后重置 ({absolute})";
    }

    public static string Compact(DateTimeOffset reset, DateTimeOffset now) => Relative(reset - now, "");

    private static string Relative(TimeSpan remaining, string separator)
    {
        if (remaining <= TimeSpan.Zero) return "待重置";
        return remaining.TotalHours < 1 ? $"{Math.Max(1, (int)remaining.TotalMinutes)}m"
            : remaining.TotalDays < 1 ? $"{remaining.Hours}h{separator}{remaining.Minutes}m"
            : $"{remaining.Days}d{separator}{remaining.Hours}h";
    }
}
