using System;
using System.Collections.Generic;
using System.Linq;

namespace QuotaMonitor;

internal static class DesktopItems
{
    internal static readonly QuotaBucket[] Catalog =
    [
        QuotaBucket.FromQuota(new(Service.ChatGPT, null, null), 10080),
        QuotaBucket.FromQuota(new(Service.ChatGPT, null, null), 300),
        new("ag.gemini.7d", ServiceKind.Antigravity, "AG·Gemini", "Antigravity · Gemini 每周", "每周", null, null, 10080),
        new("ag.claude.7d", ServiceKind.Antigravity, "AG·Claude", "Antigravity · Claude 每周", "每周", null, null, 10080),
        QuotaBucket.FromQuota(new(Service.Grok, null, null), 10080)
    ];

    public static bool IsMonitored(this ServiceKind service) => service != ServiceKind.GeminiWeb;

    public static Service Icon(this ServiceKind service) => service switch
    {
        ServiceKind.ChatGPT => Service.ChatGPT, ServiceKind.Grok => Service.Grok,
        ServiceKind.Antigravity => Service.Antigravity, _ => Service.Gemini
    };

    public static string Title(this ServiceKind service) => service switch
    {
        ServiceKind.ChatGPT => "ChatGPT / Codex", ServiceKind.Grok => "Grok",
        ServiceKind.Antigravity => "Antigravity", _ => "Gemini 网页版"
    };

    public static List<Quota> Select(QuotaReading reading, Preferences prefs)
    {
        var available = reading.Services.SelectMany(s => s.Buckets).Where(b => b.IsWidgetEligible)
            .ToLookup(b => b.BucketKey);
        var result = new List<Quota>();
        foreach (string key in prefs.WidgetBucketOrder)
        {
            if (prefs.HiddenBuckets.Contains(key)) continue;
            var bucket = available[key].FirstOrDefault() ?? Array.Find(Catalog, b => b.BucketKey == key);
            if (bucket is null) continue;
            string name = key switch
            {
                "chatgpt.7d" when prefs.HiddenBuckets.Contains("chatgpt.5h") => "ChatGPT",
                "ag.gemini.7d" => "Gemini", "ag.claude.7d" => "Claude",
                "grok.7d" => "Grok", _ => bucket.ShortName
            };
            var status = reading.Services.FirstOrDefault(s => s.Service == bucket.Service)?.StatusText;
            result.Add(new Quota(bucket.Service.Icon(), bucket.Remaining, bucket.ResetsAt, bucket.WindowMinutes,
                bucket.Status ?? (bucket.Remaining is null ? status : null), bucket.ResetText, bucket.DisplayName, name)
                { LastSuccessAt = bucket.LastSuccessAt, BucketKey = key });
        }
        return result;
    }
}
