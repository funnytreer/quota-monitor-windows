using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace QuotaMonitor;

internal sealed record AntigravityLimit(string Label, int Remaining, DateTimeOffset? ResetsAt,
    string? Id = null, string? Window = null);
internal sealed record AntigravityGroup(string Name, IReadOnlyList<AntigravityLimit> Limits);
internal sealed record AntigravityReading(IReadOnlyList<AntigravityGroup> Groups, string? Status = null)
{
    public DateTimeOffset? LastSuccessAt { get; init; }
    public static AntigravityReading Unavailable(string status) => new(Array.Empty<AntigravityGroup>(), status);

    public IReadOnlyList<QuotaBucket> ToBuckets()
    {
        var buckets = new List<QuotaBucket>();
        foreach (var group in Groups)
        {
            string groupKey = group.Name.Equals("Gemini Models", StringComparison.OrdinalIgnoreCase) ||
                group.Limits.Any(l => l.Id == "gemini-weekly") ? "gemini"
                : group.Name.Equals("Claude and GPT models", StringComparison.OrdinalIgnoreCase) ||
                    group.Limits.Any(l => l.Id == "3p-weekly") ? "claude" : Slug(group.Name);
            string shortName = groupKey switch { "gemini" => "Gemini", "claude" => "Claude", _ => group.Name };
            foreach (var limit in group.Limits)
            {
                bool weekly = limit.Window == "weekly" || (limit.Window is null && limit.Id?.EndsWith("-weekly") == true);
                var key = weekly ? "7d" : limit.Window == "5h" ? "5h" : Slug(limit.Window ?? limit.Id ?? limit.Label);
                bool summary = weekly && groupKey is "gemini" or "claude";
                buckets.Add(new($"ag.{groupKey}.{key}", ServiceKind.Antigravity, $"AG·{shortName}",
                    summary ? $"Antigravity · {shortName} 周" : $"Antigravity · {group.Name} ({limit.Label})",
                    weekly ? "每周" : limit.Label, limit.Remaining, limit.ResetsAt, weekly ? 10080 : key == "5h" ? 300 : null,
                    Status: Status, LastSuccessAt: LastSuccessAt, IsWidgetEligible: summary) { GroupName = group.Name });
            }
        }
        return buckets;
    }

    private static string Slug(string name) =>
        Regex.Replace(name.ToLowerInvariant(), @"[^\p{L}\p{Nd}]+", "-").Trim('-');
}

internal sealed class AntigravityQuotaSource
{
    private const string Unavailable = "暂时读不到用量";
    private static readonly string LocalData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string Executable = Path.Combine(LocalData, "agy", "bin", "agy.exe");

    internal static bool IsInstalled => File.Exists(Executable);

    internal static ProcessStartInfo CreateUsageCommand(string workingDirectory)
    {
        var start = new ProcessStartInfo
        {
            FileName = Executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        // agy's periodic self-updater launches another console process on Windows.
        // Scope its documented opt-out to quota polling; interactive agy can still update.
        start.Environment["AGY_CLI_DISABLE_AUTO_UPDATE"] = "true";
        // Keep /usage a built-in command even if print-mode slash commands are disabled in the user's CLI settings.
        foreach (var arg in new[]
        {
            "--settings", """{"disableSlashCommands":false,"useG1Credits":false}""",
            "--print-timeout", "12s", "--output-format", "json", "-p", "/usage"
        })
            start.ArgumentList.Add(arg);
        return start;
    }

    public async Task<AntigravityReading> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(Executable)) return AntigravityReading.Unavailable("未找到 Antigravity CLI");

        string workingDirectory = Path.Combine(LocalData, "QuotaMonitor");
        return await ReadUsageAsync(CreateUsageCommand(workingDirectory), TimeSpan.FromSeconds(20), cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<AntigravityReading> ReadUsageAsync(ProcessStartInfo command,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            Directory.CreateDirectory(command.WorkingDirectory);
            using var process = Process.Start(command);
            if (process is null) return AntigravityReading.Unavailable(Unavailable);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            try
            {
                process.StandardInput.Close();
                var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
                var error = process.StandardError.ReadToEndAsync(deadline.Token);
                await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, error).ConfigureAwait(false);
                var reading = Parse(await output.ConfigureAwait(false));
                if (process.ExitCode == 0 && reading.Groups.Count != 0) return reading;
                if (NeedsLogin(await error.ConfigureAwait(false))) return AntigravityReading.Unavailable("请先在 agy 登录");
                return reading.Status is not null ? reading : AntigravityReading.Unavailable(Unavailable);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
                cancellationToken.ThrowIfCancellationRequested();
                return AntigravityReading.Unavailable("读取超时");
            }
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException)
        {
            return AntigravityReading.Unavailable(Unavailable);
        }
    }

    internal static AntigravityReading Parse(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (Text(root, "status") != "SUCCESS" || !ReadProperty(root, "command", out var command) ||
                Text(command, "name") != "usage" ||
                !ReadProperty(command, "data", out var data) ||
                !ReadProperty(data, "groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
                return AntigravityReading.Unavailable(NeedsLogin(Text(root, "response"))
                    ? "请先在 agy 登录" : Unavailable);

            var results = new List<AntigravityGroup>();
            foreach (var group in groups.EnumerateArray())
            {
                string? name = Text(group, "name");
                if (string.IsNullOrWhiteSpace(name) || !ReadProperty(group, "buckets", out var buckets) ||
                    buckets.ValueKind != JsonValueKind.Array)
                    continue;

                var limits = new List<AntigravityLimit>();
                foreach (var bucket in buckets.EnumerateArray())
                {
                    if (!ReadProperty(bucket, "remaining_fraction", out var fraction) ||
                        fraction.ValueKind != JsonValueKind.Number || !fraction.TryGetDouble(out double value) ||
                        !double.IsFinite(value) || value is < 0 or > 1)
                        continue;
                    string? window = Text(bucket, "window");
                    string label = window switch
                    {
                        "5h" => "5 小时",
                        "weekly" => "每周",
                        _ => Text(bucket, "name") ?? "额度"
                    };
                    DateTimeOffset? reset = ReadProperty(bucket, "reset_time", out var time) &&
                        time.ValueKind == JsonValueKind.String && time.TryGetDateTimeOffset(out var timestamp) ? timestamp : null;
                    var limit = new AntigravityLimit(label, (int)Math.Round(value * 100, MidpointRounding.AwayFromZero), reset,
                        Text(bucket, "id"), window);
                    if (window == "5h") limits.Insert(0, limit);
                    else limits.Add(limit);
                }
                if (limits.Count > 0) results.Add(new AntigravityGroup(name, limits));
            }
            return results.Count > 0 ? new AntigravityReading(results) : AntigravityReading.Unavailable(Unavailable);
        }
        catch (JsonException)
        {
            return AntigravityReading.Unavailable(NeedsLogin(output) ? "请先在 agy 登录" : Unavailable);
        }
    }

    private static bool NeedsLogin(string? text) => text is not null &&
        (text.Contains("authentication required", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("please sign in", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("not logged in", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("please log in", StringComparison.OrdinalIgnoreCase));

    private static bool ReadProperty(JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        return parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out value);
    }

    private static string? Text(JsonElement parent, string name) =>
        ReadProperty(parent, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
