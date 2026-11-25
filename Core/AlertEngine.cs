using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace QuotaMonitor;

internal sealed record BucketAlert(QuotaBucket Bucket, int LowThreshold, bool EnteredLow, int? Drop, bool Recovered)
{
    public string Message
    {
        get
        {
            var reasons = new List<string>();
            if (Recovered) reasons.Add($"周期已重置，额度恢复至 {Bucket.Remaining}%");
            if (EnteredLow) reasons.Add($"剩余 {Bucket.Remaining}%，低于 {LowThreshold}%");
            if (Drop is int drop) reasons.Add($"15 分钟内下降 {drop} 个百分点");
            return Bucket.DisplayName + "：" + string.Join("，", reasons);
        }
    }
}

internal sealed class AlertEngine
{
    private static readonly TimeSpan FastWindow = TimeSpan.FromMinutes(15);
    private sealed class BucketState(QuotaBucket first)
    {
        public QuotaBucket Last = first;
        public DateTimeOffset? LastFastAlert;
        public readonly Queue<(DateTimeOffset Time, int Remaining)> Samples = new();
    }

    private readonly Dictionary<string, BucketState> _state = new();

    public IReadOnlyList<BucketAlert> Observe(IReadOnlyList<ServiceSnapshot> services, Preferences p, DateTimeOffset now)
    {
        var alerts = new List<BucketAlert>();
        bool quiet = IsQuiet(p, TimeOnly.FromDateTime(now.LocalDateTime));
        foreach (var service in services)
        {
            if (service.State is ServiceConnectionState.LoginRequired or ServiceConnectionState.NotConfigured)
            {
                foreach (var key in _state.Where(pair => pair.Value.Last.Service == service.Service).Select(pair => pair.Key).ToArray())
                    _state.Remove(key);
                continue;
            }
            if (service.State != ServiceConnectionState.Connected) continue;
            foreach (var bucket in service.Buckets)
            {
                if (bucket.Remaining is not (>= 0 and <= 100) || bucket.LastSuccessAt is not DateTimeOffset at ||
                    bucket.Status is { Length: > 0 } || at > now) continue;
                var alert = ObserveBucket(bucket, at, p);
                if (alert is not null && !quiet) alerts.Add(alert);
            }
        }
        return alerts;
    }

    private BucketAlert? ObserveBucket(QuotaBucket bucket, DateTimeOffset at, Preferences p)
    {
        int remaining = bucket.Remaining!.Value;
        if (!_state.TryGetValue(bucket.BucketKey, out var state))
        {
            _state[bucket.BucketKey] = state = new BucketState(bucket);
            state.Samples.Enqueue((at, remaining));
            return null;
        }

        var prior = state.Last;
        if (at <= prior.LastSuccessAt) return null;
        int threshold = p.PerBucketLowThresholds.GetValueOrDefault(bucket.BucketKey, p.LowThreshold);
        bool nextPeriod = prior.ResetsAt.HasValue && bucket.ResetsAt.HasValue
            ? bucket.ResetsAt > prior.ResetsAt
            : !string.IsNullOrWhiteSpace(prior.ResetText) && !string.IsNullOrWhiteSpace(bucket.ResetText) && prior.ResetText != bucket.ResetText;
        if (nextPeriod)
        {
            state.Samples.Clear();
            state.LastFastAlert = null;
        }

        var cutoff = at - FastWindow;
        while (state.Samples.TryPeek(out var head) && head.Time < cutoff) state.Samples.Dequeue();
        state.Samples.Enqueue((at, remaining));
        int drop = state.Samples.Max(sample => sample.Remaining) - remaining;
        bool fast = p.FastEnabled && drop >= p.FastThreshold && (state.LastFastAlert is null || at - state.LastFastAlert >= FastWindow);
        bool low = p.LowEnabled && remaining < threshold && prior.Remaining >= threshold;
        bool recovered = p.ResetRecoveryEnabled && nextPeriod && prior.Remaining <= threshold && remaining >= threshold;
        if (fast) state.LastFastAlert = at;
        state.Last = bucket;
        return low || fast || recovered ? new(bucket, threshold, low, fast ? drop : null, recovered) : null;
    }

    internal static bool IsQuiet(Preferences p, TimeOnly time)
    {
        if (!p.QuietHoursEnabled) return false;
        var start = TimeOnly.ParseExact(p.QuietStart, "HH:mm", CultureInfo.InvariantCulture);
        var end = TimeOnly.ParseExact(p.QuietEnd, "HH:mm", CultureInfo.InvariantCulture);
        return start == end || (start < end ? time >= start && time < end : time >= start || time < end);
    }
}
