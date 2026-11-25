using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace QuotaMonitor;

internal sealed class UsageHistoryStore : IDisposable
{
    private sealed record Sample(DateTimeOffset Timestamp, int Remaining);
    private readonly Dictionary<string, List<Sample>> _samples = new();
    private readonly Dictionary<string, DateTimeOffset> _lastSeen = new();
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMinutes(5);
    private readonly object _gate = new(), _writer = new();
    private readonly string _path;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Timer _flushTimer;
    private bool _dirty, _closed;

    public UsageHistoryStore(string path, Func<DateTimeOffset>? clock = null)
    {
        _path = path;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _flushTimer = new Timer(_ => Flush());
        try
        {
            if (!File.Exists(path)) return;
            var saved = JsonSerializer.Deserialize<Dictionary<string, List<Sample>>>(File.ReadAllText(path));
            if (saved is null) return;
            var now = _clock();
            bool pruned = false;
            foreach (var (key, values) in saved)
            {
                if (values is null || string.IsNullOrWhiteSpace(key)) continue;
                var valid = values.Where(s => s is not null && s.Remaining is >= 0 and <= 100 &&
                    s.Timestamp >= now - Retention && s.Timestamp <= now).OrderBy(s => s.Timestamp)
                    .DistinctBy(s => s.Timestamp).ToList();
                if (valid.Count > 0)
                {
                    _samples[key] = valid;
                    _lastSeen[key] = valid[^1].Timestamp;
                }
                pruned |= values.Count != valid.Count;
            }
            if (pruned) ScheduleFlush();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { Trace.TraceWarning(ex.Message); }
    }

    public void Observe(IEnumerable<QuotaBucket> buckets)
    {
        lock (_gate)
        {
            if (_closed) return;
            var now = _clock();
            var cutoff = now - Retention;
            bool changed = Prune(cutoff);
            foreach (var bucket in buckets)
            {
                if (bucket.Status is { Length: > 0 } || bucket.Remaining is not (>= 0 and <= 100) ||
                    bucket.LastSuccessAt is not DateTimeOffset at || at < cutoff || at > now) continue;
                var key = bucket.BucketKey;
                if (_lastSeen.TryGetValue(key, out var seen) && at <= seen) continue;
                _lastSeen[key] = at;
                if (!_samples.TryGetValue(key, out var values)) _samples[key] = values = [];
                var last = values.LastOrDefault();
                if (last?.Remaining == bucket.Remaining && at - last.Timestamp < SampleInterval) continue;
                values.Add(new(at, bucket.Remaining.Value));
                changed = true;
            }
            if (changed) ScheduleFlush();
        }
    }

    public int GetRecentDrop(string bucketKey, TimeSpan window, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_samples.TryGetValue(bucketKey, out var values)) return 0;
            var cutoff = now - (window < Retention ? window : Retention);
            var latest = values.LastOrDefault(s => s.Timestamp >= cutoff && s.Timestamp <= now);
            if (latest is null) return 0;
            return values.Where(s => s.Timestamp >= cutoff && s.Timestamp <= now).Max(s => s.Remaining) - latest.Remaining;
        }
    }

    private bool Prune(DateTimeOffset cutoff)
    {
        bool changed = false;
        foreach (var (key, values) in _samples.ToArray())
        {
            changed |= values.RemoveAll(s => s.Timestamp < cutoff) > 0;
            if (values.Count == 0)
            {
                _samples.Remove(key);
                _lastSeen.Remove(key);
            }
        }
        return changed;
    }

    private void ScheduleFlush()
    {
        if (!_dirty && !_closed) _flushTimer.Change(TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
        _dirty = true;
    }

    private void Flush()
    {
        lock (_writer)
        {
            Dictionary<string, Sample[]> snapshot;
            lock (_gate)
            {
                if (!_dirty) return;
                snapshot = _samples.ToDictionary(p => p.Key, p => p.Value.ToArray());
                _dirty = false;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
                File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(snapshot));
                File.Move(_path + ".tmp", _path, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning(ex.Message);
                lock (_gate) ScheduleFlush();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _closed = true;
            _flushTimer.Dispose();
        }
        Flush();
    }
}
