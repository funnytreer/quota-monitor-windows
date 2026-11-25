using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QuotaMonitor;

internal static class GrokUsagePage
{
    public const string UsageUrl = "https://grok.com/?_s=usage";
    public const string LoginUrl = "https://grok.com/sign-in?return_to=%2F%3F_s%3Dusage";

    private const string WeeklyHeading =
        @"^(?:weekly(?:\s+(?:supergrok(?:\s+heavy)?|grok|(?:the\s+)?x subscription|premium(?:\s*\+|\s+plus)?|usage))?\s+limit|weekly usage|(?:本周|每周|周度|本週|每週)(?:共享|使用|用量|订阅|訂閱|\s|SuperGrok(?:\s+Heavy)?|Grok|Premium(?:\s*\+|\s+Plus)?)*(?:额度|額度|限额|限額|用量|上限))$";
    private const string ResetMarker = @"\breset(?:s|ting)?\b|重[置设設]";
    private const string Breakdown =
        @"^(?:(?:usage by|breakdown|extra usage|additional usage|grok (?:build|bot)|chat|api|imagine|voice)\b|按产品|按產品|额外用量|额外额度|额外使用积分)";
    private const string Percent = @"(?<![\d.])(\d{1,3}(?:[.,]\d+)?)\s*[%％]";
    private static readonly Regex Heading = new(WeeklyHeading, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Reset = new(ResetMarker, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string PageScript = $$"""
        (() => {
          if (!["https://grok.com", "https://www.grok.com"].includes(location.origin)) return {};
          const clean = text => (text ?? "").trim().replace(/\s+/g, " ");
          const visible = node => node.getClientRects().length > 0 && getComputedStyle(node).visibility !== "hidden";
          const weekly = new RegExp({{JsonSerializer.Serialize(WeeklyHeading)}}, "i");
          const reset = new RegExp({{JsonSerializer.Serialize(ResetMarker)}}, "i");
          const headings = 'h1,h2,h3,h4,h5,h6,[role="heading"]';
          const usageTitle = /^(usage|用量|使用情况|使用情況|使用量)$/i;
          const hasUsage = node => [...node.querySelectorAll(headings + ',button,a,[role="tab"]')]
            .some(item => visible(item) && usageTitle.test(clean(item.textContent)));
          const usageRoute = new URLSearchParams(location.search).get("_s") === "usage" ||
            /^\/(?:settings\/usage|dashboard\/usage|usage)(?:\/|$)/.test(location.pathname);
          const dialog = [...document.querySelectorAll('dialog[open],[role="dialog"]')]
            .find(node => visible(node) && hasUsage(node));
          const root = dialog ?? (usageRoute && document.body && hasUsage(document.body) ? document.body : null);
          if (root) {
            for (const heading of root.querySelectorAll(headings)) {
              const title = clean(heading.textContent);
              if (!visible(heading) || !weekly.test(title)) continue;
              for (let card = heading.parentElement; card && card !== document.body; card = card.parentElement) {
                const content = card.innerText?.trim() ?? "";
                if (content.length > 1400) break;
                const index = content.indexOf(title);
                if (index < 0) continue;
                const details = content.slice(index + title.length).trim();
                if (!reset.test(details)) continue;
                const gauges = [...card.querySelectorAll('progress,[role="progressbar"]')].filter(node =>
                  visible(node) && /^(included usage|包含的使用量|包含的用量)$/i.test(clean(node.getAttribute("aria-label"))));
                let includedPercent;
                if (gauges.length === 1) {
                  const gauge = gauges[0];
                  const native = gauge instanceof HTMLProgressElement;
                  const value = gauge.getAttribute(native ? "value" : "aria-valuenow");
                  const min = native ? 0 : Number(gauge.getAttribute("aria-valuemin") ?? 0);
                  const max = native ? gauge.max : Number(gauge.getAttribute("aria-valuemax") ?? 100);
                  const percent = (Number(value) - min) / (max - min) * 100;
                  if (value?.trim() && percent >= 0 && percent <= 100) includedPercent = percent;
                }
                if (includedPercent === undefined && !/[%％]/.test(details)) continue;
                const times = [...card.querySelectorAll("time[datetime]")].filter(visible);
                const dateTime = times.length === 1 ? times[0].getAttribute("datetime") : null;
                return { card: title + "\n" + details, includedPercent, dateTime };
              }
            }
          }
          if (/just a moment|attention required|checking your browser|verify you are human/i.test(document.title)) {
            return { status: "challenge" };
          }
          const login = /^\/(?:sign-in|login)(?:\/|$)/.test(location.pathname) ||
            [...document.querySelectorAll("button,a")].some(node =>
              visible(node) && /^(log in|sign in|登录|登入|登入帳號|ログイン)$/i.test(clean(node.textContent)));
          return login && !root ? { status: "login" } : {};
        })()
        """;

    public static string InspectScript => PageScript;

    public static Quota? Parse(JsonElement result, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("card", out var card) || card.ValueKind != JsonValueKind.String) return null;
        var lines = (card.GetString() ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2 || !Heading.IsMatch(lines[0])) return null;

        var details = lines.Skip(1).Take(12).ToArray();
        int resetIndex = Array.FindIndex(details, line => Reset.IsMatch(line));
        if (resetIndex < 0) return null;
        var beforeReset = details.Take(resetIndex)
            .Append(details[resetIndex][..Reset.Match(details[resetIndex]).Index]);
        string total = string.Join(" ", beforeReset.TakeWhile(line => !Regex.IsMatch(line, Breakdown, RegexOptions.IgnoreCase)));
        var percentages = Regex.Matches(total, Percent);
        double? remaining = null;
        if (percentages.Count == 1 && TryPercent(percentages[0].Groups[1].Value, out double percent))
        {
            bool displaysRemaining = Regex.IsMatch(total, @"\b(?:remaining|left)\b|剩[余餘]", RegexOptions.IgnoreCase);
            remaining = displaysRemaining ? percent : 100 - percent;
        }
        if (remaining is null && result.TryGetProperty("includedPercent", out var gauge) &&
            gauge.ValueKind == JsonValueKind.Number && gauge.TryGetDouble(out var used) && used is >= 0 and <= 100)
            remaining = 100 - used;
        if (remaining is null) return null;

        DateTimeOffset? reset = null;
        if (result.TryGetProperty("dateTime", out var timestamp) && timestamp.ValueKind == JsonValueKind.String &&
            Regex.IsMatch(timestamp.GetString() ?? "", @"(?:Z|[+-]\d\d:\d\d)$", RegexOptions.IgnoreCase) &&
            timestamp.TryGetDateTimeOffset(out var exact))
            reset = exact;
        reset ??= ParseReset(string.Join(" ", details.Skip(resetIndex).Take(2)), now, timeZone);
        return new Quota(Service.Grok, (int)Math.Round(remaining.Value, MidpointRounding.AwayFromZero), reset, 10080);
    }

    private static bool TryPercent(string value, out double percent) =>
        double.TryParse(value.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out percent) &&
        percent is >= 0 and <= 100;

    private static DateTimeOffset? ParseReset(string text, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        var english = Regex.Match(text,
            @"\b([a-z]{3,9})\.?\s+(\d{1,2})(?:,?\s+(\d{4}))?\s*(?:,|at|·|-)?\s*(\d{1,2}:\d{2})(?:\s*([ap]m))?\b",
            RegexOptions.IgnoreCase);
        var chinese = Regex.Match(text,
            @"(?:(\d{4})\s*年\s*)?(\d{1,2})\s*月\s*(\d{1,2})\s*[日号號]\s*(?:周[一二三四五六日天]\s*)?(上午|下午|凌晨|晚上)?\s*(\d{1,2}):(\d{2})");
        int currentYear = TimeZoneInfo.ConvertTime(now, timeZone).Year;

        if (english.Success)
        {
            string date = $"{english.Groups[1].Value[..3]} {english.Groups[2]} {english.Groups[4]}";
            string format = "MMM d H:mm";
            if (english.Groups[5].Success)
            {
                date += $" {english.Groups[5]}";
                format = "MMM d h:mm tt";
            }
            return ParseLocal(date, format, english.Groups[3].Value, currentYear, now, timeZone);
        }
        if (chinese.Success && int.TryParse(chinese.Groups[5].Value, out int hour) && hour < 24)
        {
            hour = chinese.Groups[4].Value switch
            {
                "下午" or "晚上" => hour % 12 + 12,
                "上午" or "凌晨" => hour % 12,
                _ => hour
            };
            string date = $"{chinese.Groups[2]}-{chinese.Groups[3]} {hour}:{chinese.Groups[6]}";
            return ParseLocal(date, "M-d H:mm", chinese.Groups[1].Value, currentYear, now, timeZone);
        }
        return null;
    }

    private static DateTimeOffset? ParseLocal(string date, string format, string year, int currentYear,
        DateTimeOffset now, TimeZoneInfo timeZone)
    {
        bool explicitYear = int.TryParse(year, out int suppliedYear);
        foreach (int candidate in explicitYear ? new[] { suppliedYear } : new[] { currentYear, currentYear + 1 })
        {
            if (!DateTime.TryParseExact($"{candidate} {date}", $"yyyy {format}", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var local) || timeZone.IsInvalidTime(local)) continue;
            var instant = new DateTimeOffset(local, timeZone.GetUtcOffset(local));
            if (explicitYear || instant >= now.AddDays(-1)) return instant;
        }
        return null;
    }
}
