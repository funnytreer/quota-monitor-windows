using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace QuotaMonitor;

internal sealed class GrokConnectionWindow : Window
{
    private readonly GrokWebClient _client;
    private readonly bool _connected;
    private readonly Action<Quota?> _apply;
    private readonly CancellationTokenSource _closed = new();
    private readonly WebView2 _web = new();
    private readonly TextBlock _address = Visuals.Text("grok.com", 12, Visuals.Gray);
    private readonly TextBlock _status = Visuals.Text("正在打开 Grok 官网…", 12, Visuals.Gray);
    private readonly Button _connect;
    private readonly Button? _disconnect;
    private Window? _popup;

    public GrokConnectionWindow(GrokWebClient client, bool connected, Action<Quota?> apply)
    {
        _client = client;
        _connected = connected;
        _apply = apply;
        Title = "连接 Grok";
        Width = Math.Min(880, SystemParameters.WorkArea.Width);
        Height = Math.Min(680, SystemParameters.WorkArea.Height);
        MinWidth = 480;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Background = Visuals.ThemeBrush("Surface.Window");
        FontFamily = Visuals.Typeface;
        UseLayoutRounding = true;
        NativeChrome.Attach(this);

        var shell = new DockPanel { Margin = new Thickness(14) };
        var heading = new StackPanel();
        var domain = Visuals.Columns(22, -1, 0);
        Visuals.Place(domain, Visuals.Symbol("\uE72E", 14, Visuals.Blue), 0);
        Visuals.Place(domain, _address, 1);
        AddCaption(domain, this, 2);
        heading.Children.Add(domain);
        var guide = Visuals.Text("在 Grok 官网完成登录，然后点击「读取并开始监测」。", 11, Visuals.Gray);
        guide.Margin = new Thickness(2, 3, 0, 9);
        heading.Children.Add(guide);
        DockPanel.SetDock(heading, Dock.Top);
        shell.Children.Add(heading);

        var footer = Visuals.Columns(-1, 0, 0);
        footer.Margin = new Thickness(0, 10, 0, 0);
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(2, 0, 8, 0);
        Visuals.Place(footer, _status, 0);
        if (connected)
        {
            _disconnect = Visuals.Button(Visuals.Text("断开并清除登录", 12), "断开并清除登录", 106, 30,
                () => _ = DisconnectAsync());
            _disconnect.IsEnabled = false;
            _disconnect.Margin = new Thickness(0, 0, 7, 0);
            Visuals.Place(footer, _disconnect, 1);
        }
        const string action = "读取并开始监测";
        _connect = Visuals.Button(Visuals.Text(action, 12, Brushes.White, bold: true), action, 114, 30,
            () => _ = ConnectAsync());
        _connect.Background = Visuals.ThemeBrush("Accent.Primary");
        _connect.IsEnabled = false;
        _connect.Cursor = Cursors.Hand;
        Visuals.Place(footer, _connect, 2);
        DockPanel.SetDock(footer, Dock.Bottom);
        shell.Children.Add(footer);
        shell.Children.Add(new Border { BorderThickness = new Thickness(1), BorderBrush = Visuals.Outline, Child = _web });
        Content = shell;

        Loaded += Start;
        Closed += (_, _) =>
        {
            _closed.Cancel();
            _popup?.Close();
            _web.Dispose();
        };
    }

    private static void AddCaption(Grid row, Window window, int column)
    {
        row.Background = Brushes.Transparent;
        row.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
                window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            else if (e.LeftButton == MouseButtonState.Pressed) window.DragMove();
        };
        Visuals.Place(row, Visuals.Button(Visuals.Symbol("\uE711", 12), "关闭", 28, 28, window.Close), column);
    }

    private async void Start(object sender, RoutedEventArgs args)
    {
        Loaded -= Start;
        try
        {
            var environment = await _client.GetEnvironmentAsync();
            if (_closed.IsCancellationRequested) return;
            await _web.EnsureCoreWebView2Async(environment);
            if (_closed.IsCancellationRequested) return;
            var browser = _web.CoreWebView2;
            PrepareBrowser(browser, _address);
            browser.NewWindowRequested += OpenPopup;
            browser.Navigate(_connected ? GrokUsagePage.UsageUrl : GrokUsagePage.LoginUrl);
            SetBusy(false);
            _status.Text = _connected ? "已保存本机登录，可重新读取额度。" : "等候网页登录";
        }
        catch (Exception error) when (BrowserError(error))
        {
            _status.Text = "网页窗口无法打开，请检查 Microsoft Edge WebView2 运行时。";
        }
    }

    private async Task ConnectAsync()
    {
        if (_web.CoreWebView2 is not { } browser) return;
        SetBusy(true);
        _status.Text = "正在读取 Grok 周额度…";
        try
        {
            var reading = await GrokWebClient.ReadPageAsync(browser, _closed.Token);
            if (_closed.IsCancellationRequested) return;
            if (reading.Remaining is null)
            {
                _status.Text = reading.Status switch
                {
                    "需要登录" => "请先在网页完成 Grok 登录，再点击按钮。",
                    "需要网页验证" => "请在页面上完成网页验证，再试一次。",
                    _ => $"{reading.Status}。请确认网页上已显示「使用量」的周额度。"
                };
                return;
            }
            _apply(reading);
            Close();
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
        finally
        {
            if (!_closed.IsCancellationRequested) SetBusy(false);
        }
    }

    private async Task DisconnectAsync()
    {
        if (_web.CoreWebView2 is not { } browser) return;
        SetBusy(true);
        try
        {
            _popup?.Close();
            await browser.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllSite);
            if (_closed.IsCancellationRequested) return;
            _apply(null);
            Close();
        }
        catch (Exception error) when (BrowserError(error))
        {
            _status.Text = "无法清除监控器内的 Grok 网页登录，请重试。";
        }
        finally
        {
            if (!_closed.IsCancellationRequested) SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _connect.IsEnabled = !busy;
        if (_disconnect is not null) _disconnect.IsEnabled = !busy;
    }

    private static void PrepareBrowser(CoreWebView2 browser, TextBlock address)
    {
        GrokWebClient.Configure(browser);
        browser.NavigationStarting += (_, args) => args.Cancel = !GrokWebClient.CanNavigate(args.Uri);
        browser.SourceChanged += (_, _) =>
        {
            address.Text = Uri.TryCreate(browser.Source, UriKind.Absolute, out var url) &&
                url.Scheme == Uri.UriSchemeHttps ? url.GetLeftPart(UriPartial.Path) : "";
        };
    }

    private async void OpenPopup(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (_closed.IsCancellationRequested || !args.IsUserInitiated || !GrokWebClient.CanNavigate(args.Uri)) return;
        var deferral = args.GetDeferral();
        Window? popup = null;
        try
        {
            _popup?.Close();
            var view = new WebView2();
            var address = Visuals.Text("", 12, Visuals.Gray);
            address.Margin = new Thickness(8);
            var layout = new DockPanel();
            var header = Visuals.Columns(-1, 0);
            Visuals.Place(header, address, 0);
            DockPanel.SetDock(header, Dock.Top);
            layout.Children.Add(header);
            layout.Children.Add(view);
            popup = new Window
            {
                Owner = this, Title = "网页登录", Content = layout, Background = Visuals.ThemeBrush("Surface.Window"),
                Width = Math.Min(600, Width), Height = Math.Min(650, Height),
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false
            };
            _popup = popup;
            NativeChrome.Attach(popup);
            AddCaption(header, popup, 1);
            popup.Closed += (_, _) =>
            {
                if (ReferenceEquals(_popup, popup)) _popup = null;
                view.Dispose();
            };
            popup.Show();
            await view.EnsureCoreWebView2Async(await _client.GetEnvironmentAsync());
            if (!ReferenceEquals(_popup, popup)) return;
            PrepareBrowser(view.CoreWebView2, address);
            view.CoreWebView2.WindowCloseRequested += (_, _) => Dispatcher.BeginInvoke(() => popup.Close());
            args.NewWindow = view.CoreWebView2;
        }
        catch (Exception error) when (BrowserError(error))
        {
            if (popup?.IsVisible == true) popup.Close();
            _status.Text = "登录弹窗无法打开，请重试。";
        }
        finally { deferral.Complete(); }
    }

    private static bool BrowserError(Exception error) =>
        error is COMException or InvalidOperationException or IOException or UnauthorizedAccessException;
}
