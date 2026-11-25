using System;
using System.Collections.Generic;
using System.Linq;

namespace QuotaMonitor;

internal enum ServiceKind { ChatGPT, Antigravity, Grok, GeminiWeb }
internal enum ServiceConnectionState { NotConfigured, Connecting, Connected, LoginRequired, Unavailable }

internal sealed record QuotaBucket(string BucketKey, ServiceKind Service, string ShortName, string DisplayName,
    string WindowLabel, int? Remaining, DateTimeOffset? ResetsAt, int? WindowMinutes, string? ResetText = null,
    string? Status = null, DateTimeOffset? LastSuccessAt = null, bool IsWidgetEligible = true)
{
    public string? GroupName { get; init; }

    public static QuotaBucket FromQuota(Quota quota, int minutes)
    {
        var (service, prefix, name, title) = quota.Service switch
        {
            QuotaMonitor.Service.ChatGPT => (ServiceKind.ChatGPT, "chatgpt", "ChatGPT", "ChatGPT"),
            QuotaMonitor.Service.Grok => (ServiceKind.Grok, "grok", "Grok", "Grok"),
            QuotaMonitor.Service.Gemini => (ServiceKind.GeminiWeb, "gemini", "Gemini 网页", "Gemini 网页版"),
            _ => throw new ArgumentOutOfRangeException(nameof(quota))
        };
        var (key, suffix, window) = minutes switch
        {
            300 => ("5h", "5h", "5 小时"),
            10080 => ("7d", "周", service == ServiceKind.Grok ? "共享周" : "每周"),
            _ => ($"{minutes}m", $"{minutes}m", $"{minutes} 分钟")
        };
        return new($"{prefix}.{key}", service, $"{name} {suffix}", $"{title} · {window}额度", window,
            quota.Remaining, quota.ResetsAt, minutes, quota.ResetText, quota.Status, quota.LastSuccessAt,
            minutes is 300 or 10080);
    }
}

internal sealed record ServiceSnapshot(ServiceKind Service, ServiceConnectionState State, string StatusText,
    DateTimeOffset? LastSuccessAt, IReadOnlyList<QuotaBucket> Buckets, CodexResetReading? CodexResets = null,
    IReadOnlyList<AntigravityGroup>? AntigravityGroups = null)
{
    public static ServiceSnapshot Create(ServiceKind service, bool configured, string? status,
        IReadOnlyList<QuotaBucket> buckets, bool loginRequired = false, CodexResetReading? resets = null,
        IReadOnlyList<AntigravityGroup>? groups = null)
    {
        var last = buckets.Select(b => b.LastSuccessAt).Append(resets?.LastSuccessAt).Max();
        var state = !configured ? ServiceConnectionState.NotConfigured
            : loginRequired ? ServiceConnectionState.LoginRequired
            : status is "正在读取" or "正在读取…" or "等待 Chrome 连接" ? ServiceConnectionState.Connecting
            : string.IsNullOrEmpty(status) && last.HasValue ? ServiceConnectionState.Connected
            : ServiceConnectionState.Unavailable;
        string message = state switch
        {
            ServiceConnectionState.NotConfigured => "未连接",
            ServiceConnectionState.Connected => "已连接",
            _ => status ?? "暂时无法读取"
        };
        return new(service, state, message, last, buckets, resets, groups);
    }
}
