using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;

namespace QuotaMonitor;

internal sealed class GrokWebClient
{
    private Task<CoreWebView2Environment>? _environment;

    public Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        if (_environment is null || _environment.IsFaulted || _environment.IsCanceled)
        {
            var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QuotaMonitor", "GrokWebView");
            _environment = CoreWebView2Environment.CreateAsync(userDataFolder: profile);
        }
        return _environment;
    }

    public async Task<Quota> ReadAsync(CancellationToken cancellationToken)
    {
        var environment = await GetEnvironmentAsync();
        cancellationToken.ThrowIfCancellationRequested();
        using var host = new HwndSource(new HwndSourceParameters("Quota Monitor Grok Usage")
        {
            WindowStyle = unchecked((int)0x80000000), Width = 980, Height = 740
        });
        var controller = await environment.CreateCoreWebView2ControllerAsync(host.Handle);
        try
        {
            controller.Bounds = new System.Drawing.Rectangle(0, 0, 980, 740);
            controller.IsVisible = false;
            var browser = controller.CoreWebView2;
            Configure(browser);
            browser.NewWindowRequested += (_, args) => args.Handled = true;
            browser.NavigationStarting += (_, args) =>
            {
                if (!HealthyGrokPage(args.Uri) && !IsSignInPage(args.Uri)) args.Cancel = true;
            };
            return await ReadPageAsync(browser, cancellationToken);
        }
        finally { controller.Close(); }
    }

    public static async Task<Quota> ReadPageAsync(CoreWebView2 browser, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(24));
        var token = deadline.Token;
        bool completed = false;
        int responseStatus = 0;
        ulong? navigation = null;
        void Started(object? sender, CoreWebView2NavigationStartingEventArgs args)
        {
            if (args.Uri == GrokUsagePage.UsageUrl) navigation = args.NavigationId;
        }
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.NavigationId != navigation) return;
            completed = true;
            responseStatus = args.HttpStatusCode;
        }
        browser.NavigationStarting += Started;
        browser.NavigationCompleted += Completed;
        Quota? partial = null;
        try
        {
            browser.Navigate(GrokUsagePage.UsageUrl);
            while (true)
            {
                await Task.Delay(700, token);
                if (!completed) continue;
                if (IsSignInPage(browser.Source) || responseStatus == 401) return Unavailable("需要登录");
                if (responseStatus == 429) return Unavailable("查询受限");
                if (responseStatus == 403) return Unavailable("需要网页验证");
                if (responseStatus >= 400 || !HealthyGrokPage(browser.Source)) return Unavailable("用量页未加载");

                string result = await browser.ExecuteScriptAsync(GrokUsagePage.InspectScript).WaitAsync(token);
                if (!HealthyGrokPage(browser.Source)) continue;
                using var document = JsonDocument.Parse(result);
                var fields = document.RootElement;
                if (fields.ValueKind != JsonValueKind.Object) continue;
                if (fields.TryGetProperty("status", out var state) && state.ValueKind == JsonValueKind.String)
                {
                    if (state.ValueEquals("login")) return Unavailable("需要登录");
                    if (state.ValueEquals("challenge")) return Unavailable("需要网页验证");
                }
                if (GrokUsagePage.Parse(fields, DateTimeOffset.Now, TimeZoneInfo.Local) is { } reading)
                {
                    if (reading.ResetsAt.HasValue) return reading;
                    partial = reading;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return partial ?? Unavailable(completed ? "未读到周额度" : "用量页未加载");
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or JsonException)
        {
            return Unavailable("用量读取失败");
        }
        finally
        {
            browser.NavigationStarting -= Started;
            browser.NavigationCompleted -= Completed;
        }
    }

    public static Quota Unavailable(string status) => new(Service.Grok, null, null, Status: status);

    public static void Configure(CoreWebView2 browser)
    {
        browser.Settings.AreHostObjectsAllowed = false;
        browser.Settings.IsWebMessageEnabled = false;
        browser.Settings.AreDevToolsEnabled = false;
        browser.Profile.IsPasswordAutosaveEnabled = false;
        browser.Profile.IsGeneralAutofillEnabled = false;
        browser.DownloadStarting += (_, args) => args.Cancel = true;
    }

    public static bool HealthyGrokPage(string source) => TrySecureAddress(source, out var address) &&
        (address.Host is "grok.com" or "www.grok.com") &&
        !address.AbsolutePath.StartsWith("/sign-in", StringComparison.Ordinal);

    private static bool IsSignInPage(string source) => TrySecureAddress(source, out var address) &&
        (address.Host is "accounts.x.ai" or "accounts.spacex.ai" or "auth.x.ai" or "auth.grok.com" ||
        ((address.Host is "grok.com" or "www.grok.com") &&
            address.AbsolutePath.StartsWith("/sign-in", StringComparison.Ordinal)));

    public static bool CanNavigate(string source) =>
        source == "about:blank" || TrySecureAddress(source, out _);

    private static bool TrySecureAddress(string source, out Uri address)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var parsed) &&
            parsed.Scheme == Uri.UriSchemeHttps && parsed.IsDefaultPort && parsed.UserInfo.Length == 0)
        {
            address = parsed;
            return true;
        }
        address = null!;
        return false;
    }
}
