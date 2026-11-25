using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace QuotaMonitor;

internal sealed record CodexResetReading(long? AvailableCount, IReadOnlyList<DateTimeOffset?> Expirations, string Status)
{
    public DateTimeOffset? LastSuccessAt { get; init; }
    public static CodexResetReading Unavailable(string status) => new(null, Array.Empty<DateTimeOffset?>(), status);
}

internal sealed record CodexAccountReading(Quota WorkQuota, CodexResetReading Resets, bool LoginRequired = false)
{
    public IReadOnlyList<QuotaBucket> Buckets { get; init; } = [];

    public static CodexAccountReading Unavailable(string status, bool loginRequired = false) =>
        new(new Quota(Service.ChatGPT, null, null, Status: status), CodexResetReading.Unavailable(status), loginRequired);
}

internal sealed class CodexAccountSource
{
    internal const string MissingWorkQuota = "暂未返回 ChatGPT 额度";
    private const string DesktopPackageFamily = "OpenAI.Codex_2p2nqsd0c76g0";
    private const int InsufficientBuffer = 122;
    private string? _executable;
    internal bool IsInstalled => File.Exists(_executable) || FindCodex() != null;

    public async Task<CodexAccountReading> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var executable = ResolveExecutable();
            if (executable is null) return CodexAccountReading.Unavailable("未找到 Codex");

            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            start.ArgumentList.Add("app-server");
            start.ArgumentList.Add("--listen");
            start.ArgumentList.Add("stdio://");
            using var process = Process.Start(start) ?? throw new IOException("Codex did not start.");
            process.ErrorDataReceived += static (_, _) => { };
            process.BeginErrorReadLine();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            var token = deadline.Token;
            using var cleanup = token.Register(() => Stop(process));
            try
            {
                var input = process.StandardInput;
                input.AutoFlush = true;
                await input.WriteLineAsync(
                    """{"id":1,"method":"initialize","params":{"clientInfo":{"name":"quota_monitor","title":"Quota Monitor","version":"1.0"}}}""".AsMemory(),
                    token).ConfigureAwait(false);
                await ReadResultAsync(process.StandardOutput, 1, token).ConfigureAwait(false);
                await input.WriteLineAsync("""{"method":"initialized"}""".AsMemory(), token).ConfigureAwait(false);
                await input.WriteLineAsync("""{"id":2,"method":"account/rateLimits/read"}""".AsMemory(), token).ConfigureAwait(false);
                var result = await ReadResultAsync(process.StandardOutput, 2, token).ConfigureAwait(false);
                return ParseAccount(result);
            }
            finally
            {
                try { process.StandardInput.Close(); }
                catch (IOException) { }
                try
                {
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(400)).ConfigureAwait(false);
                }
                catch (TimeoutException) { Stop(process); }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CodexAccountReading.Unavailable("读取超时");
        }
        catch (CodexReadException error)
        {
            return CodexAccountReading.Unavailable(error.LoginRequired ? "需要登录" : "读取失败", error.LoginRequired);
        }
        catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException)
        {
            return CodexAccountReading.Unavailable("读取失败");
        }
    }

    public async Task<string?> SignInAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var executable = ResolveExecutable();
            if (executable is null) return "未找到 Codex";

            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("login");
            using var process = Process.Start(start) ?? throw new IOException("Codex did not start.");
            process.OutputDataReceived += static (_, _) => { };
            process.ErrorDataReceived += static (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(5));
            using var cleanup = deadline.Token.Register(() => Stop(process));
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            return process.ExitCode == 0 ? null : "Codex 登录未完成，可在额度详情中重试。";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "Codex 登录已超时，可在额度详情中重试。";
        }
        catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException)
        {
            return "无法启动 Codex 登录";
        }
    }

    private string? ResolveExecutable()
    {
        if (!File.Exists(_executable)) _executable = FindCodex();
        return _executable;
    }

    private static async Task<JsonElement> ReadResultAsync(StreamReader output, int requestId, CancellationToken token)
    {
        while (await output.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (document)
            {
                var message = document.RootElement;
                if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("id", out var id) ||
                    id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out int value) || value != requestId)
                    continue;
                if (message.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
                    return result.Clone();
                var description = message.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String
                    ? text.GetString() ?? "" : "";
                throw new CodexReadException(RequiresLogin(description));
            }
        }
        token.ThrowIfCancellationRequested();
        throw new IOException("Codex closed the connection.");
    }

    internal static bool RequiresLogin(string description) =>
        description.Contains("authentication required", StringComparison.OrdinalIgnoreCase) ||
        description.Contains("requires authentication", StringComparison.OrdinalIgnoreCase) ||
        description.Contains("not logged in", StringComparison.OrdinalIgnoreCase) ||
        description.Contains("not signed in", StringComparison.OrdinalIgnoreCase) ||
        description.Contains("401 Unauthorized", StringComparison.OrdinalIgnoreCase) ||
        description.Contains("token_revoked", StringComparison.OrdinalIgnoreCase) ||
        description.Contains("token_expired", StringComparison.OrdinalIgnoreCase);

    internal static CodexResetReading Parse(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("rateLimitResetCredits", out var summary) || summary.ValueKind != JsonValueKind.Object ||
            !summary.TryGetProperty("availableCount", out var count) || count.ValueKind != JsonValueKind.Number ||
            !count.TryGetInt64(out long available) || available < 0)
            return CodexResetReading.Unavailable("暂未返回重置数据");

        var expirations = new List<DateTimeOffset?>();
        if (summary.TryGetProperty("credits", out var credits) && credits.ValueKind == JsonValueKind.Array)
        {
            foreach (var credit in credits.EnumerateArray())
            {
                if (expirations.Count >= available) break;
                if (credit.ValueKind != JsonValueKind.Object || !credit.TryGetProperty("status", out var status) ||
                    status.ValueKind != JsonValueKind.String || !status.ValueEquals("available") ||
                    !credit.TryGetProperty("expiresAt", out var expiry)) continue;
                if (expiry.ValueKind == JsonValueKind.Null)
                    expirations.Add(null);
                else if (expiry.ValueKind == JsonValueKind.Number && expiry.TryGetInt64(out long seconds) &&
                    seconds is >= -62135596800 and <= 253402300799)
                    expirations.Add(DateTimeOffset.FromUnixTimeSeconds(seconds));
            }
        }
        return new CodexResetReading(available, expirations, "");
    }

    internal static CodexAccountReading ParseAccount(JsonElement result)
    {
        var unavailable = new CodexAccountReading(new Quota(Service.ChatGPT, null, null, Status: MissingWorkQuota), Parse(result));
        if (result.ValueKind != JsonValueKind.Object) return unavailable;

        JsonElement bucket;
        if (!(result.TryGetProperty("rateLimitsByLimitId", out var buckets) &&
              buckets.ValueKind == JsonValueKind.Object && buckets.TryGetProperty("codex", out bucket) &&
              bucket.ValueKind == JsonValueKind.Object))
        {
            if (!result.TryGetProperty("rateLimits", out bucket) || bucket.ValueKind != JsonValueKind.Object ||
                (bucket.TryGetProperty("limitId", out var id) && id.ValueKind != JsonValueKind.Null &&
                 (id.ValueKind != JsonValueKind.String || !id.ValueEquals("codex"))))
                return unavailable;
        }

        var primary = ReadWindow(bucket, "primary");
        var secondary = ReadWindow(bucket, "secondary");
        var windows = new[] { primary, secondary }.OfType<Quota>().ToList();
        if (windows.Count == 0) return unavailable;
        return unavailable with
        {
            WorkQuota = windows.FirstOrDefault(q => q.WindowMinutes == 10080) ?? windows[0],
            Buckets = windows.DistinctBy(q => q.WindowMinutes)
                .Select(q => QuotaBucket.FromQuota(q, q.WindowMinutes!.Value)).ToArray()
        };
    }

    private static Quota? ReadWindow(JsonElement bucket, string field)
    {
        if (!bucket.TryGetProperty(field, out var window) || window.ValueKind != JsonValueKind.Object ||
            !window.TryGetProperty("usedPercent", out var used) || used.ValueKind != JsonValueKind.Number ||
            !used.TryGetInt32(out int percent) || percent < 0) return null;

        int? minutes = window.TryGetProperty("windowDurationMins", out var duration) &&
            duration.ValueKind == JsonValueKind.Number && duration.TryGetInt32(out int length) && length > 0 ? length : field == "primary" ? 300 : 10080;
        DateTimeOffset? resets = window.TryGetProperty("resetsAt", out var timestamp) &&
            timestamp.ValueKind == JsonValueKind.Number && timestamp.TryGetInt64(out long seconds) &&
            seconds is >= -62135596800 and <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
        return new Quota(Service.ChatGPT, Math.Max(0, 100 - percent), resets, minutes);
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }

    private static string? FindCodex()
    {
        uint count = 0, length = 0;
        if (GetPackagesByPackageFamily(DesktopPackageFamily, ref count, null, ref length, IntPtr.Zero) == InsufficientBuffer)
        {
            var names = new IntPtr[count];
            var buffer = Marshal.AllocHGlobal(checked((int)length * sizeof(char)));
            try
            {
                if (GetPackagesByPackageFamily(DesktopPackageFamily, ref count, names, ref length, buffer) == 0)
                {
                    foreach (var name in names)
                    {
                        if (Marshal.PtrToStringUni(name) is not { } fullName) continue;
                        uint pathLength = 0;
                        if (GetPackagePathByFullName(fullName, ref pathLength, null) != InsufficientBuffer) continue;
                        var path = new StringBuilder((int)pathLength);
                        if (GetPackagePathByFullName(fullName, ref pathLength, path) != 0) continue;
                        string candidate = Path.Combine(path.ToString(), "app", "resources", "codex.exe");
                        if (File.Exists(candidate)) return candidate;
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            string directory = entry.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(directory)) continue;
            string candidate = Path.Combine(directory, "codex.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetPackagesByPackageFamily(string family, ref uint count, [Out] IntPtr[]? names,
        ref uint bufferLength, IntPtr buffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetPackagePathByFullName(string fullName, ref uint pathLength, StringBuilder? path);

    private sealed class CodexReadException(bool loginRequired) : Exception
    {
        public bool LoginRequired { get; } = loginRequired;
    }
}
