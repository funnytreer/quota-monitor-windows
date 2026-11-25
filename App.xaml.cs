using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace QuotaMonitor;

public partial class App : Application
{
    private readonly CodexAccountSource _codexSource = new();
    private readonly GrokWebClient _grokClient = new();
    private readonly AntigravityQuotaSource _antigravitySource = new();
    private readonly AlertEngine _alerts = new();
    private NotificationService? _notifications;
    private TraySummary _traySummary;
    private readonly CancellationTokenSource _exited = new();
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMinutes(5) };
    private readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(350) };

    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuotaMonitor", "settings.json");
    private Preferences _preferences = new();
    private UsageHistoryStore? _history;
    private ThemeManager? _theme;
    private QuotaReading? _reading;
    private CodexAccountReading _codexReading = CodexAccountReading.Unavailable("正在读取");
    private Quota _grokReading = GrokWebClient.Unavailable("未连接");
    private AntigravityReading _antigravityReading = AntigravityReading.Unavailable("正在读取…");
    private EventWaitHandle? _activation;
    private RegisteredWaitHandle? _activationWait;
    private WidgetWindow? _widget;
    private Desktop.DesktopLayerMonitor? _desktopLayer;
    private DashboardPopover? _details;
    private WidgetLayerMode? _appliedLayer;
    private DateTimeOffset _nextRefresh;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMinutes(1) };
    private GrokConnectionWindow? _grokConnection;
    private AntigravityGuideWindow? _antigravityGuide;
    private SettingsWindow? _settings;
    private Forms.NotifyIcon? _tray;
    private TrayMenuWindow? _trayMenu;
    private Drawing.Icon? _icon;
    private bool _accountBusy;
    private bool _refreshing;
    private bool _grokReadingInProgress;
    private bool _antigravityReadingInProgress;
    private int _grokConnectionVersion;
    private bool _exiting;
    private bool _placingWidget;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length > 0 && e.Args[0].StartsWith("chrome-extension://", StringComparison.Ordinal))
        {
            // An older Chrome registration can still launch this executable. Exit before activating the widget.
            Shutdown();
            return;
        }
        // Setting the named event also reveals the existing widget when the app is launched twice.
        _activation = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\QuotaMonitor.Activate", out bool first);
        if (!first)
        {
            _activation.Set();
            Shutdown();
            return;
        }
        _activationWait = ThreadPool.RegisterWaitForSingleObject(_activation, (_, _) =>
        {
            Dispatcher.BeginInvoke(() => SetWidgetVisible(true));
        }, null, Timeout.Infinite, executeOnlyOnce: false);

        _preferences = Preferences.Load(_settingsPath);
        if (_preferences.GrokConnected) _grokReading = GrokWebClient.Unavailable("正在读取");
        _history = new UsageHistoryStore(Path.Combine(Path.GetDirectoryName(_settingsPath)!, "history.json"));
        _theme = new ThemeManager(this, _preferences.ThemeMode, () =>
        {
            UpdateVisibleReadings();
            _settings?.UpdateReadings(_reading);
        });
        _widget = new WidgetWindow(_preferences, _theme);
        _widget.RefreshRequested += () => _ = RefreshAllAsync();
        _widget.DashboardRequested += ShowDetails;
        _widget.PreviewMouseDown += (_, _) => { _details?.DismissFromOutside(); _trayMenu?.DismissFromOutside(); };
        _widget.SettingsRequested += OpenSettings;
        _widget.Changed += () => { ApplyAppearance(); _settings?.BuildPage(); };
        _widget.ResetRequested += ResetWidgetSize;
        _widget.HideRequested += () => SetWidgetVisible(false);
        ApplyLayer();
        _widget.LocationChanged += (_, _) => RememberWidgetBounds();
        _widget.SizeChanged += (_, _) => RememberWidgetBounds();
        _widget.Closing += (_, e) => HideOnClose(e, _widget, () => SetWidgetVisible(false));
        _details = new DashboardPopover(_preferences, _history);
        _details.RefreshRequested += () => _ = RefreshAllAsync();
        _details.SettingsRequested += OpenSettings;
        _details.ToggleWidgetRequested += () => SetWidgetVisible(_widget?.IsVisible != true);
        _details.ConnectRequested += ConnectService;
        _details.SizeChanged += (_, _) => _details.Dispatcher.InvokeAsync(() => Desktop.PlaceNearTray(_details),
            DispatcherPriority.Loaded);
        _details.Closing += (_, e) => HideOnClose(e, _details, _details.Hide);
        CreateTray();

        _save.Tick += (_, _) =>
        {
            _save.Stop();
            SavePreferences();
        };
        _poll.Interval = TimeSpan.FromMinutes(_preferences.PollingIntervalMinutes);
        _nextRefresh = DateTimeOffset.Now + _poll.Interval;
        Publish(_codexReading, sendAlerts: false, checkCompleted: false);
        if (_preferences.WidgetVisible) SetWidgetVisible(true);
        _poll.Tick += (_, _) => { _nextRefresh = DateTimeOffset.Now + _poll.Interval; _ = RefreshAllAsync(); };
        _clock.Tick += (_, _) => UpdateVisibleReadings();
        _clock.Start();
        _poll.Start();
        _ = RefreshAllAsync();
    }

    private void HideOnClose(CancelEventArgs args, Window window, Action hide)
    {
        if (_exiting || !window.IsVisible) return;
        args.Cancel = true;
        Dispatcher.BeginInvoke(hide);
    }

    private void CreateTray()
    {
        _traySummary = TrayIconRenderer.Summarize([], _preferences);
        _icon = TrayIconRenderer.Create(_traySummary);
        _trayMenu = new TrayMenuWindow(ShowDetails, () => _ = RefreshAllAsync(),
            () => SetWidgetVisible(!_preferences.WidgetVisible), () =>
            {
                _preferences.WidgetLocked = !_preferences.WidgetLocked;
                ApplyAppearance();
                _settings?.BuildPage();
            }, OpenSettings, ExitApp);
        _trayMenu.Closing += (_, e) => HideOnClose(e, _trayMenu, _trayMenu.Hide);
        _tray = new Forms.NotifyIcon { Icon = _icon, Visible = true, Text = "额度监控器" };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) ToggleDetails();
            else if (e.Button == Forms.MouseButtons.Right)
            {
                _details?.Hide();
                if (_trayMenu.IsVisible) { _trayMenu.Hide(); return; }
                if (_trayMenu.WasDismissedByTrayClick()) return;
                _trayMenu.Sync(_preferences.WidgetVisible, _preferences.WidgetLocked);
                Desktop.ShowNearTray(_trayMenu);
            }
        };
        _notifications = new NotificationService(_tray, Dispatcher, ShowDetails);
        _tray.BalloonTipClicked += (_, _) => ShowDetails();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += TrayAppearanceChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += TrayAppearanceChanged;
    }

    private void TrayAppearanceChanged(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_exiting || _tray is null) return;
            UpdateTray(forceIcon: true);
        });
    }

    private void UpdateTray(bool forceIcon = false)
    {
        if (_tray is null) return;
        var summary = TrayIconRenderer.Summarize(_reading?.Services ?? [], _preferences);
        if (forceIcon || summary.Minimum != _traySummary.Minimum || summary.Badge != _traySummary.Badge)
        {
            var previous = _icon;
            _icon = TrayIconRenderer.Create(summary);
            _tray.Icon = _icon;
            previous?.Dispose();
        }
        if (_tray.Text != summary.Tooltip) _tray.Text = summary.Tooltip;
        _traySummary = summary;
    }

    private void ToggleDetails()
    {
        _trayMenu?.Hide();
        if (_details?.IsVisible == true) _details.Hide();
        else if (_details?.WasDismissedByTrayClick() != true) ShowDetails();
    }

    private void ShowDetails()
    {
        if (_details is null) return;
        _trayMenu?.Hide();
        if (_reading is not null) _details.Present(_reading, _nextRefresh);
        if (!_details.IsVisible) Desktop.ShowNearTray(_details);
        else _details.Activate();
    }

    private void OpenSettings()
    {
        if (_settings is null)
        {
            _settings = new SettingsWindow(_preferences, ApplyAppearance, _theme!);
            _settings.Diagnostics = BuildDiagnostics;
            _settings.ConnectRequested += ConnectService;
            _settings.CheckRequested += CheckService;
            _settings.ResetRequested += ResetWidgetPosition;
            _settings.TestRequested += () => _notifications?.Show("额度监控器 · 测试通知", "提醒渠道已连接。", warning: false);
            _settings.UpdateReadings(_reading);
            _settings.Closed += (_, _) => _settings = null;
            Desktop.ShowSettings(_settings, _widget);
        }
        _settings.Activate();
    }

    private void ApplyAppearance()
    {
        _theme?.Apply(_preferences.ThemeMode);
        ApplyLayer();
        var interval = TimeSpan.FromMinutes(_preferences.PollingIntervalMinutes);
        if (_poll.Interval != interval)
        {
            _poll.Stop();
            _poll.Interval = interval;
            _nextRefresh = DateTimeOffset.Now + interval;
            _poll.Start();
        }
        UpdateVisibleReadings();
        UpdateTray();
        ScheduleSave();
    }

    private void UpdateVisibleReadings()
    {
        if (_reading is null) return;
        _widget?.Present(_reading, _preferences);
        if (_details?.IsVisible == true) _details.Present(_reading, _nextRefresh);
    }

    private void ApplyLayer()
    {
        if (_widget is null || _appliedLayer == _preferences.LayerMode) return;
        _desktopLayer?.Dispose();
        _appliedLayer = _preferences.LayerMode;
        _desktopLayer = _appliedLayer == WidgetLayerMode.DesktopPinned ? new Desktop.DesktopLayerMonitor(_widget) : null;
        Desktop.ApplyWidgetLayer(_widget);
    }

    private void SetWidgetVisible(bool visible)
    {
        if (_widget is null || _exiting) return;
        if (visible && !_widget.IsVisible)
        {
            _placingWidget = true;
            try { Desktop.ShowWidget(_widget, _preferences); }
            finally { _placingWidget = false; }
            RememberWidgetBounds();
        }
        else if (!visible && _widget.IsVisible)
        {
            RememberWidgetBounds();
            _widget.Hide();
        }
        _preferences.WidgetVisible = visible;
        _details?.UpdateFooter();
        _trayMenu?.Sync(visible, _preferences.WidgetLocked);
        ScheduleSave();
    }

    private void RememberWidgetBounds()
    {
        if (_placingWidget || _widget?.IsApplyingMetrics == true || _widget?.IsVisible != true || !Desktop.TryGetBounds(_widget, out var bounds)) return;
        var dpi = VisualTreeHelper.GetDpi(_widget);
        _preferences.WidgetX = bounds.Left;
        _preferences.WidgetY = bounds.Top;
        _preferences.WidgetWidth = (int)Math.Round((bounds.Width / dpi.DpiScaleX - WidgetWindow.WidgetGutter * 2) / _preferences.WidgetScale);
        if (_preferences.Mode != DisplayMode.CompactPill)
            _preferences.WidgetHeight = (int)Math.Round((bounds.Height / dpi.DpiScaleY - WidgetWindow.WidgetGutter * 2) / _preferences.WidgetScale);
        ScheduleSave();
    }

    private void ResetWidgetSize()
    {
        _preferences.WidgetWidth = Preferences.DefaultWidgetWidth;
        _preferences.WidgetHeight = Preferences.DefaultWidgetHeight;
        _widget?.ResetSize();
        ScheduleSave();
    }

    private void ResetWidgetPosition()
    {
        _placingWidget = true;
        try
        {
            _preferences.WidgetX = _preferences.WidgetY = null;
            ResetWidgetSize();
            if (_widget?.IsVisible == true) Desktop.ShowWidget(_widget, _preferences);
        }
        finally { _placingWidget = false; }
        RememberWidgetBounds();
    }

    private void ConnectService(ServiceKind service)
    {
        switch (service)
        {
            case ServiceKind.ChatGPT: _ = SignInCodexAsync(); break;
            case ServiceKind.Grok: OpenGrokConnection(); break;
            case ServiceKind.Antigravity:
                if (_antigravityGuide is null)
                {
                    _antigravityGuide = new AntigravityGuideWindow(RefreshAntigravityAsync);
                    _antigravityGuide.Closed += (_, _) => _antigravityGuide = null;
                    _antigravityGuide.Present(_antigravityReading);
                    _antigravityGuide.Show();
                }
                _antigravityGuide.Activate();
                break;
        }
    }

    private void CheckService(ServiceKind service)
    {
        _ = service switch
        {
            ServiceKind.ChatGPT => RefreshAsync(), ServiceKind.Grok => RefreshGrokAsync(),
            ServiceKind.Antigravity => RefreshAntigravityAsync(),
            _ => Task.CompletedTask
        };
    }

    private string BuildDiagnostics()
    {
        var lines = new List<string>
        {
            $"版本  {typeof(App).Assembly.GetName().Version}",
            $"主题  {(_theme?.IsDark == true ? "深色" : "浅色")}",
            $"Codex CLI  {(_codexSource.IsInstalled ? "已检测到" : "未检测到")}",
            $"Antigravity CLI  {(AntigravityQuotaSource.IsInstalled ? "已检测到" : "未检测到")}"
        };
        if (_reading != null) lines.AddRange(_reading.Services.Select(s => s.Service.Title() + "  " + DashboardPopover.StateName(s.State)));
        lines.Add(@"配置文件  %LOCALAPPDATA%\QuotaMonitor\settings.json");
        return string.Join(Environment.NewLine, lines);
    }

    private void OpenGrokConnection()
    {
        if (_grokConnection is null)
        {
            _grokConnectionVersion++;
            _grokConnection = new GrokConnectionWindow(_grokClient, _preferences.GrokConnected, ApplyGrokConnection);
            _grokConnection.Closed += (_, _) => _grokConnection = null;
            _grokConnection.Show();
        }
        _grokConnection.Activate();
    }

    private void ApplyGrokConnection(Quota? reading)
    {
        _preferences.GrokConnected = reading is not null;
        _grokReading = reading?.AfterRead(null, DateTimeOffset.Now) ?? GrokWebClient.Unavailable("未连接");
        ScheduleSave();
        RenderReadings(completed: reading is not null);
    }

    private async Task RefreshAllAsync()
    {
        if (_refreshing || _exiting) return;
        _refreshing = true;
        _details?.SetBusy(true);
        try
        {
            await Task.WhenAll(RefreshAsync(), RefreshGrokAsync(), RefreshAntigravityAsync());
        }
        finally
        {
            _refreshing = false;
            if (!_exiting) _details?.SetBusy(_accountBusy);
        }
    }

    private async Task RefreshAntigravityAsync()
    {
        if (_exiting || _antigravityReadingInProgress) return;
        _antigravityReadingInProgress = true;
        try
        {
            var reading = await _antigravitySource.ReadAsync(_exited.Token);
            if (!_exiting) ApplyAntigravityReading(reading);
        }
        catch (OperationCanceledException) when (_exited.IsCancellationRequested) { }
        finally { _antigravityReadingInProgress = false; }
    }

    private void ApplyAntigravityReading(AntigravityReading reading)
    {
        if (reading.Groups.Count > 0)
            _antigravityReading = reading with { LastSuccessAt = DateTimeOffset.Now };
        else if (_antigravityReading.LastSuccessAt is not null && reading.Status != "请先在 agy 登录")
            _antigravityReading = _antigravityReading with { Status = reading.Status };
        else
            _antigravityReading = reading;
        _antigravityGuide?.Present(_antigravityReading);
        RenderReadings();
    }

    private async Task RefreshGrokAsync()
    {
        if (_exiting || !_preferences.GrokConnected || _grokConnection is not null || _grokReadingInProgress) return;
        int version = _grokConnectionVersion;
        _grokReadingInProgress = true;
        Quota reading;
        try
        {
            reading = await _grokClient.ReadAsync(_exited.Token);
        }
        catch (OperationCanceledException) when (_exited.IsCancellationRequested) { return; }
        catch (Exception exception)
        {
            Trace.TraceError(exception.ToString());
            reading = GrokWebClient.Unavailable("用量读取失败");
        }
        finally
        {
            _grokReadingInProgress = false;
        }
        if (_exiting || version != _grokConnectionVersion) return;
        _grokReading = reading.AfterRead(reading.Status == "需要登录" ? null : _grokReading, DateTimeOffset.Now);
        RenderReadings();
    }

    private async Task RefreshAsync()
    {
        if (_accountBusy || _exiting) return;
        SetAccountBusy(true);
        try
        {
            var snapshot = await _codexSource.ReadAsync(_exited.Token);
            if (!_exiting) Publish(snapshot);
        }
        catch (OperationCanceledException) when (_exited.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            Trace.TraceError(exception.ToString());
            if (!_exiting) Publish(CodexAccountReading.Unavailable("读取失败"));
        }
        finally
        {
            SetAccountBusy(false);
        }
    }

    private async Task SignInCodexAsync()
    {
        if (_accountBusy || _exiting) return;
        SetAccountBusy(true);
        string? failure;
        try
        {
            failure = await _codexSource.SignInAsync(_exited.Token);
        }
        catch (OperationCanceledException) when (_exited.IsCancellationRequested) { return; }
        finally
        {
            SetAccountBusy(false);
        }

        if (_exiting) return;
        if (failure is null) await RefreshAsync();
        else MessageBox.Show(failure, "登录 Codex", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void SetAccountBusy(bool busy)
    {
        _accountBusy = busy;
        if (!_exiting) _details?.SetBusy(busy || _refreshing);
    }

    private void Publish(CodexAccountReading snapshot, bool sendAlerts = true, bool checkCompleted = true)
    {
        if (checkCompleted)
        {
            var now = DateTimeOffset.Now;
            var previous = snapshot.LoginRequired ? null : _codexReading;
            var resets = snapshot.Resets;
            if (resets.AvailableCount.HasValue) resets = resets with { LastSuccessAt = now };
            else if (previous?.Resets.LastSuccessAt is not null && resets.Status != "暂未返回重置数据")
                resets = previous.Resets with { Status = resets.Status };
            _codexReading = snapshot with
            {
                WorkQuota = snapshot.WorkQuota.AfterRead(snapshot.WorkQuota.Status == CodexAccountSource.MissingWorkQuota
                    ? null : previous?.WorkQuota, now),
                Buckets = snapshot.Buckets.Count > 0
                    ? snapshot.Buckets.Select(b => b with { LastSuccessAt = now }).ToArray()
                    : previous is not null && snapshot.WorkQuota.Status != CodexAccountSource.MissingWorkQuota
                        ? previous.Buckets.Select(b => b with { Status = snapshot.WorkQuota.Status }).ToArray() : [],
                Resets = resets
            };
        }
        RenderReadings(sendAlerts, checkCompleted);
    }

    private void RenderReadings(bool sendAlerts = true, bool completed = true)
    {
        var snapshot = _codexReading;
        var ag = _antigravityReading.ToBuckets();
        ServiceSnapshot[] services =
        [
            ServiceSnapshot.Create(ServiceKind.ChatGPT, true, snapshot.WorkQuota.Status, snapshot.Buckets,
                snapshot.LoginRequired, resets: snapshot.Resets),
            ServiceSnapshot.Create(ServiceKind.Antigravity, true, _antigravityReading.Status, ag,
                _antigravityReading.Status == "请先在 agy 登录", groups: _antigravityReading.Groups),
            ServiceSnapshot.Create(ServiceKind.Grok, _preferences.GrokConnected, _grokReading.Status,
                [QuotaBucket.FromQuota(_grokReading, 10080)], _grokReading.Status == "需要登录")
        ];
        _history?.Observe(services.SelectMany(s => s.Buckets));
        var checkedAt = services.Max(s => s.LastSuccessAt);
        var reading = new QuotaReading(checkedAt, services)
            { HasCompletedCheck = completed || _reading?.HasCompletedCheck == true };
        _reading = reading;
        _widget?.Present(reading, _preferences);
        if (_details?.IsVisible == true) _details.Present(reading, _nextRefresh);
        _settings?.UpdateReadings(reading);
        UpdateTray();
        var alerts = _alerts.Observe(services, _preferences, DateTimeOffset.Now);
        if (sendAlerts && alerts.Count > 0)
        {
            bool warning = alerts.Any(a => a.EnteredLow || a.Drop.HasValue);
            var body = string.Join(Environment.NewLine, alerts.Select(a => a.Message));
            _notifications?.Show(warning ? "额度提醒" : "额度已恢复", body, warning);
        }
    }

    private void ScheduleSave()
    {
        _save.Stop();
        if (!_exiting) _save.Start();
    }

    private void SavePreferences()
    {
        try { _preferences.Save(_settingsPath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceError(exception.ToString());
        }
    }

    private void ExitApp()
    {
        _exiting = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _clock.Stop();
        _poll.Stop();
        _save.Stop();
        _exited.Cancel();
        if (_widget is not null) SavePreferences();
        _theme?.Dispose();
        _history?.Dispose();
        _activationWait?.Unregister(null);
        _desktopLayer?.Dispose();
        _activation?.Dispose();
        if (_tray is not null)
        {
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= TrayAppearanceChanged;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= TrayAppearanceChanged;
            _tray.Visible = false;
            _tray.Dispose();
        }
        _icon?.Dispose();
        base.OnExit(e);
    }
}
