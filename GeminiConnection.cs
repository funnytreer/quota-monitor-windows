using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace QuotaMonitor;

internal sealed record GeminiReading(Quota Current, Quota? Weekly)
{
    public static GeminiReading Unavailable(string status) => new(new Quota(Service.Gemini, null, null, Status: status), null);
}

internal enum GeminiBridgeState { WaitingForChrome, Handshaking, NeedsPermission, NeedsSignIn, Connected, ReadFailed }

internal sealed class GeminiConnection
{
    private const string Host = "com.quotamonitor.gemini";
    private const string Origin = "chrome-extension://ogojeakbiilhhagkmchjdeabgebhdbek";
    private const string RegistryPath = @"Software\Google\Chrome\NativeMessagingHosts\" + Host;
    private const int MaxMessageSize = 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _write = new(1);
    private NamedPipeServerStream? _browser;

    public GeminiBridgeState State { get; private set; }
    public event Action<GeminiBridgeState>? StateChanged;

    private void SetState(GeminiBridgeState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }

    public static string ExtensionPath => Path.Combine(AppContext.BaseDirectory, "GeminiExtension");

    internal static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            return key?.GetValue("") is string manifest && File.Exists(manifest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return false; }
    }

    public static void Register()
    {
        if (!File.Exists(Path.Combine(ExtensionPath, "manifest.json")))
            throw new IOException("找不到 Gemini 扩展文件。");
        var app = Path.Combine(AppContext.BaseDirectory, "QuotaMonitor.exe");
        if (!File.Exists(app)) throw new IOException("找不到额度监控器程序。");
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuotaMonitor");
        Directory.CreateDirectory(folder);
        var manifest = Path.Combine(folder, Host + ".json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new
        {
            name = Host, description = "Quota Monitor Gemini connection", path = app,
            type = "stdio", allowed_origins = new[] { Origin + "/" }
        }));
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
        key.SetValue("", manifest, RegistryValueKind.String);
    }

    public static void Unregister() => Registry.CurrentUser.DeleteSubKey(RegistryPath, throwOnMissingSubKey: false);

    public void Disconnect() => _browser?.Dispose();

    // Chrome launches the same executable as a stdio-to-pipe relay, without starting the tray app.
    public static bool IsHostLaunch(string[] args) =>
        args.Length > 0 && args[0].StartsWith("chrome-extension://", StringComparison.Ordinal);

    public static async Task RunHostAsync(string origin)
    {
        if (origin != Origin && origin != Origin + "/") return;
        try
        {
            using var pipe = new NamedPipeClientStream(".", Host, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(1500);
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();
            await Task.WhenAny(input.CopyToAsync(pipe), pipe.CopyToAsync(output));
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Gemini browser relay disconnected.");
        }
    }

    public async Task ListenAsync(Action<GeminiReading> report, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(Host, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(stop);
                _browser = pipe;
                SetState(GeminiBridgeState.Handshaking);
                await RefreshAsync(stop);
                while (!stop.IsCancellationRequested)
                    report(await ReceiveAsync(pipe, stop));
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or ObjectDisposedException)
            {
                report(GeminiReading.Unavailable("等待 Chrome 连接"));
            }
            finally { _browser = null; SetState(GeminiBridgeState.WaitingForChrome); }
        }
    }

    internal async Task<GeminiReading> ReceiveAsync(Stream pipe, CancellationToken stop)
    {
        var reading = ReadUsage(await ReadMessageAsync(pipe, stop));
        var state = reading.Current.Status switch
        {
            "请授予 Gemini 网站访问权限" => GeminiBridgeState.NeedsPermission,
            "请在 Chrome 登录 Gemini" => GeminiBridgeState.NeedsSignIn,
            _ when reading.Current.Remaining.HasValue => GeminiBridgeState.Connected,
            _ => GeminiBridgeState.ReadFailed
        };
        SetState(state);
        return reading;
    }

    public async Task RefreshAsync(CancellationToken stop)
    {
        var pipe = _browser;
        if (pipe is null) return;
        try
        {
            await _write.WaitAsync(stop);
            try
            {
                byte[] body = """{"type":"refresh"}"""u8.ToArray();
                byte[] message = new byte[sizeof(int) + body.Length];
                BinaryPrimitives.WriteInt32LittleEndian(message, body.Length);
                body.CopyTo(message, sizeof(int));
                await pipe.WriteAsync(message, stop);
            }
            finally { _write.Release(); }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private static async Task<GeminiMessage?> ReadMessageAsync(Stream pipe, CancellationToken stop)
    {
        byte[] header = new byte[sizeof(int)];
        await pipe.ReadExactlyAsync(header, stop);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxMessageSize) throw new InvalidDataException("Invalid Gemini message length.");
        byte[] body = new byte[length];
        await pipe.ReadExactlyAsync(body, stop);
        return JsonSerializer.Deserialize<GeminiMessage>(body, Json);
    }

    private static GeminiReading ReadUsage(GeminiMessage? message)
    {
        if (message?.Type == "signIn") return GeminiReading.Unavailable("请在 Chrome 登录 Gemini");
        if (message?.Type == "permission") return GeminiReading.Unavailable("请授予 Gemini 网站访问权限");
        if (message?.Type != "usage" || message.Current?.Used is not (>= 0 and <= 100))
            return GeminiReading.Unavailable("暂时读不到用量");
        return new GeminiReading(ToQuota(message.Current, 300), message.Weekly?.Used is >= 0 and <= 100
            ? ToQuota(message.Weekly, 10080) : null);
    }

    private static Quota ToQuota(GeminiUsage value, int minutes) => new(Service.Gemini, 100 - value.Used!.Value,
        null, minutes, ResetText: value.Reset is { Length: > 0 and <= 48 } ? value.Reset : null);

    private sealed record GeminiUsage(int? Used, string? Reset);
    private sealed record GeminiMessage(string? Type, GeminiUsage? Current, GeminiUsage? Weekly);
}
