using System;
using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace QuotaMonitor;

internal sealed class DashboardPopover : TrayPopupWindow
{
    private readonly Preferences _prefs;
    private readonly UsageHistoryStore? _history;
    private readonly StackPanel _cards = new();
    private readonly TextBlock _footer = Visuals.Text("", 10.5, Visuals.Gray);
    private readonly TextBlock _visibilityLabel = Visuals.Text("", 11, Visuals.Blue);
    private readonly Button _pin, _refresh, _visibility;
    private bool _pinned, _expanded;
    protected override bool KeepOpen => _pinned;
    private QuotaReading? _reading;
    private DateTimeOffset _next;
    public event Action? RefreshRequested, SettingsRequested, ToggleWidgetRequested;
    public event Action<ServiceKind>? ConnectRequested;

    public DashboardPopover(Preferences prefs, UsageHistoryStore? history) : base("额度监测中心", 340)
    {
        SizeToContent = SizeToContent.Manual;
        _prefs = prefs;
        _history = history;
        var panel = new DockPanel { LastChildFill = true };
        Frame.Child = panel;
        var head = Visuals.Columns(-1, 0, 0, 0);
        head.Margin = new Thickness(14, 9, 10, 6);
        Visuals.Place(head, Visuals.Text(Title, 15, bold: true), 0);
        _pin = Tool("\uE718", "固定监测中心", () => SetPinned(!_pinned));
        _refresh = Tool("\uE72C", "立即刷新", () => RefreshRequested?.Invoke());
        Visuals.Place(head, _pin, 1);
        Visuals.Place(head, _refresh, 2);
        Visuals.Place(head, Tool("\uE713", "小组件设置", () => SettingsRequested?.Invoke()), 3);
        DockPanel.SetDock(head, Dock.Top);
        panel.Children.Add(head);
        var headRule = Visuals.Rule(0, 3);
        DockPanel.SetDock(headRule, Dock.Top);
        panel.Children.Add(headRule);
        var foot = Visuals.Columns(-1, 0);
        foot.Margin = new Thickness(14, 5, 12, 7);
        _footer.TextWrapping = TextWrapping.Wrap;
        Visuals.Place(foot, _footer, 0);
        var action = new StackPanel { Orientation = Orientation.Horizontal };
        action.Children.Add(Visuals.Symbol("\uE890", 12, Visuals.Blue));
        _visibilityLabel.Margin = new Thickness(4, 0, 0, 0);
        action.Children.Add(_visibilityLabel);
        _visibility = Visuals.Button(action, "切换桌面小组件", double.NaN, 26, () => ToggleWidgetRequested?.Invoke());
        _visibility.Padding = new Thickness(6, 2, 6, 2);
        Visuals.Place(foot, _visibility, 1);
        DockPanel.SetDock(foot, Dock.Bottom);
        panel.Children.Add(foot);
        var footRule = Visuals.Rule(3, 0);
        DockPanel.SetDock(footRule, Dock.Bottom);
        panel.Children.Add(footRule);
        _cards.Margin = new Thickness(12, 0, 12, 0);
        panel.Children.Add(new ScrollViewer { Style = (Style)Application.Current.FindResource("PanelScroll"), Content = _cards, VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { SetPinned(false); Hide(); } };
    }

    private static Button Tool(string icon, string title, Action action)
    {
        var button = Visuals.Button(Visuals.Symbol(icon, 13), title, 28, 28, action);
        button.Margin = new Thickness(3, 0, 0, 0);
        button.Background = Visuals.ThemeBrush("Surface.Subtle");
        button.ToolTip = title;
        return button;
    }

    private void SetPinned(bool pinned)
    {
        _pinned = pinned;
        WatchOutsideClicks();
        _pin.Background = pinned ? Visuals.ThemeBrush("Accent.PrimarySoft") : Visuals.ThemeBrush("Surface.Subtle");
        _pin.ToolTip = pinned ? "取消固定" : "固定监测中心";
        AutomationProperties.SetItemStatus(_pin, pinned ? "已固定" : "未固定");
    }

    private sealed record CardState(ServiceKind Service, ServiceConnectionState State, string Status, string? LastSuccess,
        (QuotaBucket Bucket, QuotaHealth Health, int Drop)[] Buckets, long? Resets, string? ResetStatus, string? ResetNote,
        DateTimeOffset?[] Expirations, Brush Palette)
    {
        internal bool Matches(CardState other) =>
            this with { Buckets = other.Buckets, Expirations = other.Expirations } == other &&
            Buckets.SequenceEqual(other.Buckets) && Expirations.SequenceEqual(other.Expirations);
    }

    private sealed record CardView(CardState State, List<(TextBlock Label, QuotaBucket Bucket)> Times);

    private CardState Capture(ServiceSnapshot service, DateTimeOffset now)
    {
        var buckets = service.Buckets.Select(b => (b with { LastSuccessAt = null }, Health(b),
            _history?.GetRecentDrop(b.BucketKey, TimeSpan.FromMinutes(15), now) ?? 0)).ToArray();
        var resets = service.CodexResets;
        return new(service.Service, service.State, service.StatusText,
            service.State != ServiceConnectionState.Connected && service.LastSuccessAt is DateTimeOffset last ? Quota.LocalTime(last) : null,
            buckets, resets?.AvailableCount, resets?.Status, Quota.LastSuccessNote(resets?.LastSuccessAt, resets?.Status),
            resets?.Expirations.ToArray() ?? [], (Brush)FindResource("Normal.Fill"));
    }

    private QuotaHealth Health(QuotaBucket b) => ThemeManager.Health(b.Remaining,
        _prefs.PerBucketLowThresholds.GetValueOrDefault(b.BucketKey, _prefs.LowThreshold), _prefs.CriticalThreshold);

    public void SetBusy(bool busy) => _refresh.IsEnabled = !busy;
    public void Present(QuotaReading reading, DateTimeOffset nextRefresh)
    {
        _reading = reading;
        _next = nextRefresh;
        var now = DateTimeOffset.Now;
        var services = reading.Services;
        for (int i = 0; i < services.Count; i++)
        {
            var service = services[i];
            var state = Capture(service, now);
            if (i < _cards.Children.Count && ((Border)_cards.Children[i]).Tag is CardView current && current.State.Matches(state))
            {
                foreach (var (label, bucket) in current.Times)
                    Visuals.SetText(label, CountdownFormatter.Format(bucket.ResetsAt, now, bucket.ResetText));
                continue;
            }
            var times = new List<(TextBlock, QuotaBucket)>();
            var card = ServiceCard(service, times);
            card.Tag = new CardView(state, times);
            if (i < _cards.Children.Count) _cards.Children.RemoveAt(i);
            _cards.Children.Insert(i, card);
        }
        if (_cards.Children.Count > services.Count)
            _cards.Children.RemoveRange(services.Count, _cards.Children.Count - services.Count);
        UpdateFooter();
    }

    public void UpdateFooter()
    {
        if (_reading is null) return;
        int minutes = Math.Max(0, (int)Math.Ceiling((_next - DateTimeOffset.Now).TotalMinutes));
        Visuals.SetText(_footer, $"{_reading.CheckTimeText} · " + (minutes > 0 ? $"下次刷新 {minutes}m" : "即将刷新"));
        Visuals.SetText(_visibilityLabel, _prefs.WidgetVisible ? "隐藏小组件" : "显示小组件");
    }

    internal static string StateName(ServiceConnectionState state) => state switch
    {
        ServiceConnectionState.Connected => "已连接", ServiceConnectionState.LoginRequired => "需登录",
        ServiceConnectionState.Connecting => "连接中", ServiceConnectionState.NotConfigured => "未连接", _ => "暂不可用"
    };

    private Border ServiceCard(ServiceSnapshot service, List<(TextBlock, QuotaBucket)> times)
    {
        var stack = new StackPanel();
        var head = Visuals.Columns(22, -1, 0);
        Visuals.Place(head, ServiceMark.Create(service.Service.Icon(), Visuals.Ink, 16), 0);
        Visuals.Place(head, Visuals.Text(service.Service.Title(), 13, bold: true), 1);
        var status = Visuals.Text(StateName(service.State), 10,
            service.State == ServiceConnectionState.Connected ? Visuals.Blue : Visuals.Gray);
        Visuals.Place(head, new Border { Child = status, Background = Visuals.ThemeBrush("Surface.Subtle"),
            Padding = new Thickness(6, 2, 6, 2), CornerRadius = new CornerRadius(6), ToolTip = service.StatusText }, 2);
        stack.Children.Add(head);
        if (service.State != ServiceConnectionState.Connected)
        {
            var line = Visuals.Columns(-1, 0);
            line.Margin = new Thickness(0, 5, 0, 0);
            var message = Visuals.Text(service.StatusText, 11, Visuals.Gray);
            message.TextWrapping = TextWrapping.Wrap;
            Visuals.Place(line, message, 0);
            string? action = service.Service switch
            {
                ServiceKind.ChatGPT when service.State == ServiceConnectionState.LoginRequired => "登录 Codex",
                ServiceKind.Antigravity when service.State != ServiceConnectionState.Connecting => "登录指引",
                ServiceKind.Grok => service.State == ServiceConnectionState.LoginRequired ? "重新登录" : "连接 Grok",
                _ => null
            };
            if (action is not null) Visuals.Place(line, Visuals.ActionButton(action, () => ConnectRequested?.Invoke(service.Service)), 1);
            stack.Children.Add(line);
        }

        if (service.Service == ServiceKind.Antigravity && service.Buckets.Count > 0)
            AddModelPeriods(stack, service, times);
        else foreach (var b in service.Buckets.Where(b => b.Remaining.HasValue)) AddBucket(stack, b, b.WindowLabel, times);
        if (service.CodexResets is { } resets)
        {
            stack.Children.Add(Visuals.Rule(8, 6));
            var panel = new CodexResetPanel();
            panel.Present(resets);
            stack.Children.Add(panel);
        }
        if (service.State != ServiceConnectionState.Connected && service.LastSuccessAt is DateTimeOffset last)
            stack.Children.Add(Visuals.Text("上次成功 " + Quota.LocalTime(last), 10, Visuals.Gray));
        var card = Visuals.Card(stack);
        card.Margin = new Thickness(0, 3, 0, 5);
        return card;
    }

    private void AddModelPeriods(StackPanel stack, ServiceSnapshot service, List<(TextBlock, QuotaBucket)> times)
    {
        foreach (var bucket in service.Buckets.Where(b => b.IsWidgetEligible))
            AddBucket(stack, bucket, bucket.ShortName.Replace("AG·", "") + " · 每周", times);

        var details = new StackPanel();
        foreach (var group in service.Buckets.Where(b => !b.IsWidgetEligible).GroupBy(b => b.GroupName ?? b.ShortName))
        {
            var title = Visuals.Text(group.Key, 12, bold: true);
            title.TextWrapping = TextWrapping.Wrap;
            title.Margin = new Thickness(0, 9, 0, 0);
            details.Children.Add(title);
            foreach (var bucket in group) AddBucket(details, bucket, bucket.WindowLabel, times);
        }
        if (details.Children.Count == 0) return;

        string Caption() => _expanded ? "收起模型周期  ▴" : "展开全部模型周期  ▾";
        var caption = Visuals.Text(Caption(), 11, Visuals.Blue);
        var toggle = new Button { Content = caption, Height = 25, Padding = new Thickness(7, 2, 7, 2),
            Style = (Style)FindResource("ControlButton") };
        AutomationProperties.SetName(toggle, caption.Text);
        toggle.Click += (_, _) =>
        {
            _expanded = !_expanded;
            details.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
            caption.Text = Caption();
            AutomationProperties.SetName(toggle, caption.Text);
        };
        details.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        toggle.Background = Visuals.ThemeBrush("Accent.PrimarySoft");
        toggle.Margin = new Thickness(0, 6, 0, 0);
        toggle.HorizontalAlignment = HorizontalAlignment.Left;
        stack.Children.Add(toggle);
        stack.Children.Add(details);
    }

    private void AddBucket(StackPanel stack, QuotaBucket b, string title, List<(TextBlock, QuotaBucket)> times)
    {
        var health = Health(b);
        var line = Visuals.Columns(-1, 0);
        line.Margin = new Thickness(0, 6, 0, 1);
        Visuals.Place(line, Visuals.Text(title, 12), 0);
        Visuals.Place(line, Visuals.Text(b.Remaining is int n ? $"{n}%" : "—", 16,
            Visuals.ThemeBrush($"{health}.Text"), true), 1);
        stack.Children.Add(line);
        stack.Children.Add(QuotaProgressBar.Create(b.Remaining, 4, Visuals.ThemeBrush($"{health}.Track"),
            (Brush)FindResource($"{health}.Fill")));
        var now = DateTimeOffset.Now;
        var schedule = Visuals.Columns(-1, 0);
        schedule.Margin = new Thickness(0, 3, 0, 0);
        var reset = Visuals.Text(CountdownFormatter.Format(b.ResetsAt, now, b.ResetText), 10.5, Visuals.Gray);
        times.Add((reset, b));
        reset.TextWrapping = TextWrapping.Wrap;
        Visuals.Place(schedule, reset, 0);
        var drop = _history?.GetRecentDrop(b.BucketKey, TimeSpan.FromMinutes(15), now) ?? 0;
        if (drop >= 3) Visuals.Place(schedule, new Border { CornerRadius = new CornerRadius(8),
            Background = Visuals.ThemeBrush("Low.Track"), Padding = new Thickness(6, 1, 6, 1),
            Child = Visuals.Text($"↓ {drop}%/15m", 10.5, Visuals.Amber) }, 1);
        stack.Children.Add(schedule);
    }
}
