using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Forms = System.Windows.Forms;

namespace QuotaMonitor;

internal sealed class SettingsWindow : GlassWindow
{
    private static readonly Brush Surface = Visuals.ThemeBrush("Surface.Window");
    private static readonly string[] Presets = { "#F4F2F2", "#DFEAFC", "#CAD6C6", "#CDC9E5" };
    private readonly Preferences _preferences;
    private readonly Action _changed;
    private readonly StackPanel _body = new(), _nav = new();
    private readonly ScrollViewer _scroll;
    private readonly WidgetWindow _previewWidget;
    private readonly Grid _previewVisual;
    private readonly Grid _previewArea = new();
    private readonly Viewbox _preview = new() { Stretch = Stretch.Uniform, Margin = new Thickness(12, 7, 12, 7) };
    private readonly Border _previewPlate = new() { Height = 152, Margin = new Thickness(0, 0, 0, 10), CornerRadius = new CornerRadius(12), ClipToBounds = true };
    private readonly List<(Button Button, Func<bool> Selected)> _choices = new();
    private readonly Button[] _pages = new Button[4];
    private readonly Border[] _indicators = new Border[4];
    private readonly TextBlock _pageTitle = Visuals.Text("", 17, bold: true);
    private bool _mist;
    private StackPanel? _connections;
    private TextBlock? _diagnostic;
    private QuotaReading? _reading;
    private int _page;
    public event Action<ServiceKind>? ConnectRequested, CheckRequested;
    public event Action? ResetRequested, TestRequested;
    public Func<string>? Diagnostics { get; set; }

    public SettingsWindow(Preferences preferences, Action changed, ThemeManager theme)
        : base("额度监控器 · 设置", 624, gutter: 8, native: true)
    {
        _preferences = preferences;
        _changed = changed;
        SizeToContent = SizeToContent.Manual;
        Width = 640; Height = 500; MinWidth = 580; MinHeight = 450;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        SetTextSurface(Surface, true);
        _previewWidget = new WidgetWindow(preferences, theme);
        _previewVisual = (Grid)_previewWidget.Content;
        _previewWidget.Content = null;
        _previewVisual.IsHitTestVisible = false;
        _previewVisual.HorizontalAlignment = HorizontalAlignment.Center;
        _previewVisual.VerticalAlignment = VerticalAlignment.Center;
        _previewArea.Children.Add(_previewVisual);
        _preview.Child = _previewArea;
        var stage = new Grid();
        stage.Children.Add(_preview);
        var wallpaper = Visuals.Button(Visuals.Symbol("\uE91B", 13, Brushes.White), "切换预览壁纸", 25, 25, () =>
        {
            _mist = !_mist;
            _previewPlate.Background = Wallpaper(_mist);
        });
        wallpaper.Background = Visuals.ColorBrush("#90203452");
        wallpaper.HorizontalAlignment = HorizontalAlignment.Right;
        wallpaper.VerticalAlignment = VerticalAlignment.Top;
        wallpaper.Margin = new Thickness(6);
        stage.Children.Add(wallpaper);
        _previewPlate.Background = Wallpaper(false);
        _previewPlate.Child = stage;
        Closed += (_, _) => _previewWidget.Close();

        var columns = Visuals.Columns(168, -1);
        Frame.Child = columns;
        var sidebar = new Border { Child = _nav, Background = Visuals.ThemeBrush("Surface.Sidebar"),
            CornerRadius = new CornerRadius(12, 0, 0, 12), BorderBrush = Visuals.Outline, BorderThickness = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(8, 14, 8, 10) };
        Visuals.Place(columns, sidebar, 0);
        var brand = Visuals.Columns(26, -1);
        brand.Margin = new Thickness(4, 1, 0, 16);
        Visuals.Place(brand, Visuals.Symbol("\uE713", 16, Visuals.Blue), 0);
        Visuals.Place(brand, Visuals.Text("额度监控器 · 设置", 13, bold: true), 1);
        _nav.Children.Add(brand);
        var main = new DockPanel();
        Visuals.Place(columns, main, 1);
        var title = Visuals.Columns(-1, 0);
        title.Margin = new Thickness(16, 8, 8, 5);
        Visuals.Place(title, _pageTitle, 0);
        Visuals.Place(title, Visuals.Button(Visuals.Symbol("\uE711", 12), "关闭设置", 28, 28, Close), 1);
        void Drag(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
        title.Background = brand.Background = Brushes.Transparent;
        title.MouseLeftButtonDown += Drag;
        brand.MouseLeftButtonDown += Drag;
        DockPanel.SetDock(title, Dock.Top);
        main.Children.Add(title);
        string[] names = ["外观与桌面", "服务与显示项", "提醒与通知", "常规与诊断"];
        string[] icons = ["\uE7F4", "\uE8B8", "\uEA8F", "\uE713"];
        for (int i = 0; i < names.Length; i++)
        {
            int index = i;
            var item = Visuals.Columns(24, -1);
            item.Width = 112;
            Visuals.Place(item, Visuals.Symbol(icons[i], 13), 0);
            Visuals.Place(item, Visuals.Text(names[i], 12.5, bold: true), 1);
            var button = Visuals.Button(item, names[i], double.NaN, 34, () => Navigate(index));
            button.Margin = new Thickness(0, 0, 0, 4);
            _pages[i] = button;
            button.BorderThickness = new Thickness(0);
            var itemHost = new Grid();
            itemHost.Children.Add(button);
            _indicators[i] = new Border { Background = Visuals.Blue, Width = 3, Height = 16, IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 0, 4) };
            itemHost.Children.Add(_indicators[i]);
            _nav.Children.Add(itemHost);
        }
        _body.Margin = new Thickness(14, 4, 14, 12);
        _scroll = new ScrollViewer { Style = (Style)Application.Current.FindResource("PanelScroll"), Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        main.Children.Add(_scroll);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        Navigate(0);
    }

    private void Navigate(int index)
    {
        _page = index;
        for (int i = 0; i < _pages.Length; i++)
        {
            bool selected = i == index;
            _pages[i].Background = selected ? Visuals.ThemeBrush("Accent.PrimarySoft") : Brushes.Transparent;
            _indicators[i].Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetItemStatus(_pages[i], selected ? "已选" : "");
            if (selected) _pageTitle.Text = AutomationProperties.GetName(_pages[i]);
        }
        BuildPage();
        _scroll.ScrollToTop();
    }

    internal void BuildPage()
    {
        if (_previewPlate.Parent is Panel old) old.Children.Remove(_previewPlate);
        _choices.Clear();
        _connections = null;
        _diagnostic = null;
        _body.Children.Clear();
        switch (_page)
        {
            case 0: Appearance(); break;
            case 1: Services(); break;
            case 2: Alerts(); break;
            case 3: General(); break;
        }
        SyncChoices();
        UpdateReadings(_reading);
    }

    private void Apply(bool rebuild = false)
    {
        _changed();
        if (rebuild) BuildPage();
        else { SyncChoices(); UpdatePreview(); }
    }

    private static void Choose(Button button, bool selected)
    {
        button.Background = selected ? Visuals.ThemeBrush("Surface.Selected") : Brushes.Transparent;
        button.Effect = selected ? Visuals.Frozen(new DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = .18,
            RenderingBias = RenderingBias.Performance }) : null;
        AutomationProperties.SetItemStatus(button, selected ? "已选" : "");
    }

    private void SyncChoices()
    {
        foreach (var option in _choices) Choose(option.Button, option.Selected());
    }

    private StackPanel Group(string? title = null)
    {
        var stack = new StackPanel();
        var card = Visuals.Card(stack);
        card.Padding = new Thickness(16, 9, 16, 9);
        card.Margin = new Thickness(0, 0, 0, 8);
        if (title != null) stack.Children.Add(Visuals.Text(title, 13, bold: true));
        _body.Children.Add(card);
        return stack;
    }

    private Border Options<T>(IEnumerable<T> values, Func<T, string> label, Func<T> current, Action<T> select)
    {
        var grid = new UniformGrid { Rows = 1 };
        foreach (var value in values)
        {
            var button = Visuals.Button(Visuals.Text(label(value), 11), label(value), double.NaN, 27, () => select(value));
            button.Padding = new Thickness(2, 0, 2, 0);
            button.BorderThickness = new Thickness(0);
            _choices.Add((button, () => EqualityComparer<T>.Default.Equals(current(), value)));
            grid.Children.Add(button);
        }
        return new Border { Child = grid, Background = Visuals.ThemeBrush("Surface.Tray"), Padding = new Thickness(3),
            CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center };
    }

    private static void Toggle(StackPanel parent, string title, bool initial, Action<bool> update)
    {
        var toggle = new CheckBox { IsChecked = initial, Style = (Style)Application.Current.FindResource("Switch"),
            HorizontalAlignment = HorizontalAlignment.Right };
        AutomationProperties.SetName(toggle, title);
        toggle.Click += (_, _) => update(toggle.IsChecked == true);
        var row = Visuals.Columns(-1, 0);
        row.MinHeight = 43;
        Visuals.Place(row, Visuals.Text(title, 12), 0); Visuals.Place(row, toggle, 1);
        parent.Children.Add(new Border { Child = row, BorderBrush = Visuals.Outline, BorderThickness = new Thickness(0, 1, 0, 0) });
    }

    private void Appearance()
    {
        _body.Children.Add(_previewPlate);
        var style = Group();
        var p = _preferences;
        style.Children.Add(SettingRow("主题模式", Options(Enum.GetValues<ThemePreference>(),
            t => t switch { ThemePreference.System => "跟随系统", ThemePreference.Light => "浅色", _ => "深色" },
            () => p.ThemeMode, t => { p.ThemeMode = t; Apply(); }), 0));
        style.Children.Add(SettingRow("显示方式", Options(Enum.GetValues<DisplayMode>(), WidgetWindow.LayoutName,
            () => p.Mode, mode => { p.Mode = mode; Apply(true); }), 7));
        if (p.Mode == DisplayMode.Bars)
            Toggle(style, "显示重置倒计时", p.ShowCountdownOnWidget, b => { p.ShowCountdownOnWidget = b; Apply(); });
        style.Children.Add(SettingRow("小组件尺寸", Options(new[] { .9, 1, 1.15, 1.3 },
            scale => scale switch { .9 => "紧凑 90%", 1 => "标准 100%", 1.15 => "舒适 115%", _ => "大号 130%" },
            () => p.WidgetScale, scale => { p.WidgetScale = scale; Apply(); }), 7));
        style.Children.Add(SettingRow("背景材质", Options(Enum.GetValues<WidgetSurfaceStyle>(),
            v => v switch { WidgetSurfaceStyle.Themed => "主题默认", WidgetSurfaceStyle.HudGlass => "无界通透", _ => "自定义颜色" },
            () => p.SurfaceStyle, v => { p.SurfaceStyle = v; Apply(true); }), 7));
        if (p.SurfaceStyle == WidgetSurfaceStyle.CustomColor)
        {
            var palette = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var hex in Presets)
            {
                var selected = hex.Equals(p.CustomBackground, StringComparison.OrdinalIgnoreCase);
                var chip = Visuals.Button(Visuals.Text(selected ? "✓" : "", 12, Brushes.Black), "背景色 " + hex,
                    26, 24, () => { p.CustomBackground = hex; Apply(true); });
                chip.Background = Visuals.ColorBrush(hex);
                chip.BorderBrush = selected ? Visuals.Blue : Visuals.Outline;
                chip.Margin = new Thickness(0, 0, 5, 0);
                palette.Children.Add(chip);
            }
            var custom = Visuals.Button(Visuals.Text("+", 14, ThemeManager.InkOnSolidColor((Color)ColorConverter.ConvertFromString(p.CustomBackground))),
                "自定义背景色", 26, 24, () => PickColor(p.CustomBackground, hex => { p.CustomBackground = hex; Apply(true); }));
            custom.Background = Visuals.ColorBrush(p.CustomBackground);
            palette.Children.Add(custom);
            style.Children.Add(SettingRow("背景颜色", palette, 7));
        }
        var slide = Visuals.Columns(-1, 39);
        var percent = Visuals.Text($"{p.Transparency}%", 11);
        percent.HorizontalAlignment = HorizontalAlignment.Right;
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = p.Transparency, IsSnapToTickEnabled = true,
            TickFrequency = 1, Style = (Style)FindResource("TransparencySlider") };
        AutomationProperties.SetName(slider, "背景透明度");
        slider.ValueChanged += (_, _) => { p.Transparency = (int)slider.Value; percent.Text = $"{p.Transparency}%"; Apply(); };
        Visuals.Place(slide, slider, 0); Visuals.Place(slide, percent, 1);
        style.Children.Add(SettingRow("背景透明度", slide, 9));
        style.Children.Add(SettingRow("高透明文字", Options(Enum.GetValues<WidgetTextContrast>(),
            t => t switch { WidgetTextContrast.Light => "亮白", WidgetTextContrast.Dark => "深墨", _ => "自动" },
            () => p.TextContrast, t => { p.TextContrast = t; Apply(); }), 7));
        var behavior = Group("桌面行为");
        var layer = new ComboBox { Style = (Style)FindResource("LayerSelector"), ItemsSource = Enum.GetValues<WidgetLayerMode>().Select(WidgetWindow.LayerName), SelectedIndex = (int)p.LayerMode };
        layer.SelectionChanged += (_, _) => { if (layer.SelectedIndex >= 0) { p.LayerMode = (WidgetLayerMode)layer.SelectedIndex; Apply(); } };
        AutomationProperties.SetName(layer, "窗口层级");
        behavior.Children.Add(SettingRow("窗口层级", layer, 7));
        Toggle(behavior, "锁定位置与尺寸", p.WidgetLocked, b => { p.WidgetLocked = b; Apply(); });
        var reset = Visuals.ActionButton("恢复默认尺寸与位置", () => ResetRequested?.Invoke());
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        behavior.Children.Add(reset);
    }

    public void UpdateReadings(QuotaReading? reading)
    {
        bool changedBuckets = _page == 2 && _reading != reading &&
            !new HashSet<string>(AlertItems(_reading).Select(b => b.BucketKey)).SetEquals(AlertItems(reading).Select(b => b.BucketKey));
        _reading = reading;
        if (changedBuckets) { BuildPage(); return; }
        if (_page == 0) UpdatePreview();
        if (_connections != null && reading != null) PresentConnections(reading);
        if (_diagnostic != null) _diagnostic.Text = Diagnostics?.Invoke() ?? "";
    }

    private void UpdatePreview()
    {
        if (_page != 0) return;
        var p = _preferences;
        _previewWidget.Present(_reading ?? new QuotaReading(null, []), p);
        _previewVisual.Width = Math.Max(_previewWidget.MinWidth, Math.Min(308, p.WidgetWidth) * p.WidgetScale + 16);
        _previewVisual.Height = Math.Max(_previewWidget.MinHeight, (p.Mode == DisplayMode.CompactPill ? 36 : 89) * p.WidgetScale + 16);
        // Fit the same desktop area for every choice so the change of scale stays visible.
        _previewArea.Width = (_previewVisual.Width - 16) / p.WidgetScale * 1.3 + 16;
        _previewArea.Height = (_previewVisual.Height - 16) / p.WidgetScale * 1.3 + 16;
    }

    private static Brush Wallpaper(bool mist)
    {
        var art = new DrawingGroup();
        using (var dc = art.Open())
        {
            var sky = new RadialGradientBrush(mist ? Color.FromRgb(239, 248, 255) : Color.FromRgb(50, 95, 165),
                mist ? Color.FromRgb(148, 184, 212) : Color.FromRgb(12, 23, 48))
            { Center = new Point(.25, .2), GradientOrigin = new Point(.25, .2), RadiusX = .9, RadiusY = 1.2 };
            dc.DrawRectangle(sky, null, new Rect(0, 0, 440, 152));
            if (!mist)
            {
                var random = new Random(153);
                for (int i = 0; i < 105; i++)
                {
                    double size = .35 + random.NextDouble() * .65;
                    dc.DrawEllipse(Visuals.Alpha(Colors.White, .2 + random.NextDouble() * .6), null,
                        new Point(random.Next(440), random.Next(152)), size, size);
                }
            }
        }
        var wallpaper = new DrawingBrush(art) { Stretch = Stretch.Fill };
        wallpaper.Freeze();
        return wallpaper;
    }

    private void Services()
    {
        _connections = new StackPanel();
        _body.Children.Add(_connections);
        var items = Group("桌面显示项");
        var p = _preferences;
        var order = p.WidgetBucketOrder.Where(key => DesktopItems.Catalog.Any(b => b.BucketKey == key)).ToList();
        for (int index = 0; index < order.Count; index++)
        {
            string key = order[index];
            var bucket = DesktopItems.Catalog.First(b => b.BucketKey == key);
            var row = Visuals.Columns(-1, 28, 28);
            row.Margin = new Thickness(0, 4, 0, 0);
            var check = new CheckBox { Style = (Style)FindResource("DisplayItem"), Content = bucket.DisplayName, IsChecked = !p.HiddenBuckets.Contains(key),
                Foreground = Visuals.Ink, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            check.Click += (_, _) =>
            {
                if (check.IsChecked == true) p.HiddenBuckets.Remove(key); else p.HiddenBuckets.Add(key);
                Apply();
            };
            Visuals.Place(row, check, 0);
            void Move(string other)
            {
                var a = p.WidgetBucketOrder.IndexOf(key);
                var b = p.WidgetBucketOrder.IndexOf(other);
                (p.WidgetBucketOrder[a], p.WidgetBucketOrder[b]) = (p.WidgetBucketOrder[b], p.WidgetBucketOrder[a]);
                Apply(true);
            }
            int i = index;
            var up = Visuals.Button(Visuals.Symbol("\uE70E", 10), "上移 " + bucket.DisplayName, 24, 24, () => Move(order[i - 1]));
            var down = Visuals.Button(Visuals.Symbol("\uE70D", 10), "下移 " + bucket.DisplayName, 24, 24, () => Move(order[i + 1]));
            up.IsEnabled = i > 0; down.IsEnabled = i + 1 < order.Count;
            Visuals.Place(row, up, 1); Visuals.Place(row, down, 2);
            items.Children.Add(row);
        }
    }

    private void PresentConnections(QuotaReading reading)
    {
        if (_connections is null) return;
        _connections.Children.Clear();
        foreach (var service in reading.Services)
        {
            var row = Visuals.Columns(22, -1, 0);
            row.Margin = new Thickness(0, 1, 0, 1);
            Visuals.Place(row, ServiceMark.Create(service.Service.Icon(), Visuals.Ink, 16), 0);
            var caption = new StackPanel();
            caption.Children.Add(Visuals.Text(service.Service.Title(), 12, bold: true));
            string last = service.LastSuccessAt is DateTimeOffset time ? "  上次成功 " + Quota.LocalTime(time) : "";
            var statusLine = new WrapPanel();
            var state = Visuals.Text(DashboardPopover.StateName(service.State), 10, Visuals.Gray);
            statusLine.Children.Add(new Border { Background = Visuals.ThemeBrush("Surface.Subtle"), Child = state,
                Padding = new Thickness(4, 0, 4, 0), CornerRadius = new CornerRadius(4), ToolTip = service.StatusText });
            if (last.Length > 0) statusLine.Children.Add(Visuals.Text(last, 10, Visuals.Gray));
            caption.Children.Add(statusLine);
            Visuals.Place(row, caption, 1);
            var actions = new WrapPanel();
            var kind = service.Service;
            string? label = kind switch
            {
                ServiceKind.ChatGPT when service.State == ServiceConnectionState.LoginRequired => "登录",
                ServiceKind.Grok => _preferences.GrokConnected ? "管理连接" : "连接",
                ServiceKind.Antigravity => "登录指引",
                _ => null
            };
            if (label != null) actions.Children.Add(Visuals.ActionButton(label, () => ConnectRequested?.Invoke(kind)));
            if (service.State != ServiceConnectionState.NotConfigured)
                actions.Children.Add(Visuals.ActionButton("刷新", () => CheckRequested?.Invoke(kind)));
            Visuals.Place(row, actions, 2);
            var card = Visuals.Card(row);
            card.Margin = new Thickness(0, 0, 0, 6);
            _connections.Children.Add(card);
        }
    }

    private static IEnumerable<QuotaBucket> AlertItems(QuotaReading? reading) =>
        DesktopItems.Catalog.Concat(reading?.Services.SelectMany(s => s.Buckets) ?? []).DistinctBy(b => b.BucketKey);

    private void Alerts()
    {
        var p = _preferences;
        var low = Group("额度阈值");
        Toggle(low, "低额度提醒", p.LowEnabled, b => { p.LowEnabled = b; Apply(); });
        void Number(StackPanel parent, string name, int current, Action<int> change, int maximum = 100, bool percent = true)
        {
            var input = Threshold(name, current, percent, change, maximum);
            input.HorizontalAlignment = HorizontalAlignment.Left;
            parent.Children.Add(SettingRow(name, input, 6));
        }
        Number(low, "低额度阈值", p.LowThreshold, n => { p.LowThreshold = n; Apply(); });
        Number(low, "危急阈值", p.CriticalThreshold, n => { p.CriticalThreshold = n; Apply(); }, 50);
        var overrides = new StackPanel();
        foreach (var bucket in AlertItems(_reading))
        {
            var key = bucket.BucketKey;
            var row = Visuals.Columns(-1, 0);
            bool enabled = p.PerBucketLowThresholds.TryGetValue(key, out int threshold);
            var check = new CheckBox { Style = (Style)FindResource("DisplayItem"), Content = bucket.DisplayName, IsChecked = enabled, Foreground = Visuals.Ink,
                FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            int value = enabled ? threshold : p.LowThreshold;
            var input = Threshold(bucket.DisplayName, value, true,
                n => { value = n; p.PerBucketLowThresholds[key] = n; Apply(); });
            input.IsEnabled = enabled;
            check.Click += (_, _) =>
            {
                if (check.IsChecked == true)
                    p.PerBucketLowThresholds[key] = value;
                else p.PerBucketLowThresholds.Remove(key);
                input.IsEnabled = check.IsChecked == true;
                Apply();
            };
            row.Margin = new Thickness(0, 4, 0, 0);
            Visuals.Place(row, check, 0); Visuals.Place(row, input, 1);
            overrides.Children.Add(row);
        }
        low.Children.Add(new Expander { Header = "单项阈值", Content = overrides, Foreground = Visuals.Ink, Margin = new Thickness(0, 8, 0, 0) });
        var fast = Group("用量变化");
        Toggle(fast, "消耗过快提醒", p.FastEnabled, b => { p.FastEnabled = b; Apply(); });
        Number(fast, "15 分钟降幅", p.FastThreshold, n => { p.FastThreshold = n; Apply(); }, percent: false);
        Toggle(fast, "额度重置恢复提醒", p.ResetRecoveryEnabled, b => { p.ResetRecoveryEnabled = b; Apply(); });
        var quiet = Group("免打扰");
        Toggle(quiet, "启用免打扰时段", p.QuietHoursEnabled, b => { p.QuietHoursEnabled = b; Apply(true); });
        var times = new StackPanel { Orientation = Orientation.Horizontal, IsEnabled = p.QuietHoursEnabled };
        TextBox TimeField(string name, Func<string> get, Action<string> save)
        {
            var field = new TextBox { Text = get(), Width = 52, MaxLength = 5, Padding = new Thickness(4, 2, 4, 2),
                Background = Surface, Foreground = Visuals.Ink, BorderBrush = Visuals.Outline };
            AutomationProperties.SetName(field, name);
            void Commit()
            {
                if (TimeOnly.TryParseExact(field.Text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                { if (field.Text != get()) { save(field.Text); Apply(); } }
                else field.Text = get();
            }
            field.LostKeyboardFocus += (_, _) => Commit();
            field.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
            return field;
        }
        times.Children.Add(TimeField("开始时间", () => p.QuietStart, t => p.QuietStart = t));
        times.Children.Add(Visuals.Text("  至  ", 11, Visuals.Gray));
        times.Children.Add(TimeField("结束时间", () => p.QuietEnd, t => p.QuietEnd = t));
        quiet.Children.Add(SettingRow("本地时间", times, 6));
        var test = Visuals.ActionButton("发送测试通知", () => TestRequested?.Invoke());
        test.HorizontalAlignment = HorizontalAlignment.Left;
        _body.Children.Add(test);
    }

    private void General()
    {
        var p = _preferences;
        var general = Group("启动与刷新");
        general.Children.Add(StartupOption());
        general.Children.Add(SettingRow("刷新间隔", Options(new[] { 1, 3, 5, 10, 15 }, n => n + "m",
            () => p.PollingIntervalMinutes, n => { p.PollingIntervalMinutes = n; Apply(); }), 8));
        var diag = Group("诊断信息");
        _diagnostic = Visuals.Text(Diagnostics?.Invoke() ?? "", 11, Visuals.Gray);
        _diagnostic.FontFamily = Visuals.NumericTypeface;
        _diagnostic.Margin = new Thickness(0, 6, 0, 8);
        _diagnostic.TextWrapping = TextWrapping.Wrap;
        diag.Children.Add(_diagnostic);
        var actions = new WrapPanel();
        var feedback = Visuals.Text("", 10, Visuals.Gray);
        actions.Children.Add(Visuals.ActionButton("复制诊断摘要", () =>
        {
            try { Clipboard.SetText(_diagnostic.Text); feedback.Text = "已复制"; }
            catch (System.Runtime.InteropServices.COMException) { feedback.Text = "剪贴板正忙，请重试"; }
        }));
        actions.Children.Add(Visuals.ActionButton("打开配置目录", () =>
        {
            var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuotaMonitor");
            try { System.IO.Directory.CreateDirectory(path); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.IOException or UnauthorizedAccessException)
            { feedback.Text = "无法打开配置目录"; }
        }));
        diag.Children.Add(actions);
        diag.Children.Add(feedback);
    }

    private static StackPanel StartupOption()
    {
        var panel = new StackPanel();
        var row = Visuals.Columns(-1, 0);
        var feedback = Visuals.Text("", 11, Visuals.Gray);
        feedback.Visibility = Visibility.Collapsed;
        var toggle = new CheckBox { Style = (Style)Application.Current.FindResource("Switch") };
        AutomationProperties.SetName(toggle, "开机自启动");
        Visuals.Place(row, Visuals.Text("开机自启动", 12), 0);
        Visuals.Place(row, toggle, 1);
        panel.Children.Add(row);
        panel.Children.Add(feedback);

        bool enabled = false;
        try { enabled = StartupRegistration.IsEnabled(); }
        catch (Exception error) when (error is System.IO.IOException or System.Security.SecurityException or UnauthorizedAccessException)
        {
            toggle.IsEnabled = false;
            ShowError("无法读取 Windows 启动设置");
        }
        toggle.IsChecked = enabled;
        toggle.Click += (_, _) =>
        {
            try
            {
                StartupRegistration.SetEnabled(toggle.IsChecked == true);
                enabled = toggle.IsChecked == true;
                feedback.Visibility = Visibility.Collapsed;
            }
            catch (Exception error) when (error is System.IO.IOException or System.Security.SecurityException or UnauthorizedAccessException)
            {
                toggle.IsChecked = enabled;
                ShowError("无法更新 Windows 启动设置");
            }
        };
        return panel;

        void ShowError(string message)
        {
            feedback.Text = message;
            feedback.Visibility = Visibility.Visible;
        }
    }

    private static Border SettingRow(string label, UIElement control, double top)
    {
        var row = Visuals.Columns(86, -1);
        row.MinHeight = 43;
        Visuals.Place(row, Visuals.Text(label, 12), 0);
        Visuals.Place(row, control, 1);
        return new Border { Child = row, BorderBrush = Visuals.Outline, BorderThickness = new Thickness(0, top > 0 ? 1 : 0, 0, 0) };
    }

    private void PickColor(string initialHex, Action<string> selected)
    {
        var color = (Color)ColorConverter.ConvertFromString(initialHex);
        using var dialog = new Forms.ColorDialog
        {
            FullOpen = true, Color = System.Drawing.Color.FromArgb(color.R, color.G, color.B)
        };
        if (dialog.ShowDialog(new WindowOwner(new WindowInteropHelper(this).Handle)) == Forms.DialogResult.OK)
            selected($"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}");
    }

    private sealed record WindowOwner(IntPtr Handle) : Forms.IWin32Window;

    private static Border Threshold(string title, int value, bool percent, Action<int> changed, int maximum = 100)
    {
        var columns = Visuals.Columns(-1, 18);
        var input = new TextBox
        {
            Text = value.ToString(CultureInfo.InvariantCulture), FontSize = 12, Foreground = Visuals.Ink,
            Background = Surface, BorderThickness = new Thickness(0),
            // Keep the editor's opaque background inside the rounded border.
            Margin = new Thickness(3, 2, 0, 2), Padding = new Thickness(1, 0, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch, MaxLength = 3
        };
        TextOptions.SetTextHintingMode(input, TextHintingMode.Fixed);
        AutomationProperties.SetName(input, title);
        if (percent)
        {
            var percentage = new StackPanel { Orientation = Orientation.Horizontal };
            percentage.Children.Add(input);
            percentage.Children.Add(Visuals.Text("%", 12));
            Visuals.Place(columns, percentage, 0);
        }
        else Visuals.Place(columns, input, 0);
        var arrows = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Visuals.Place(columns, arrows, 1);

        int Read() => int.TryParse(input.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
            ? Math.Clamp(number, 1, maximum) : value;
        void Apply(int number)
        {
            int next = Math.Clamp(number, 1, maximum);
            input.Text = next.ToString(CultureInfo.InvariantCulture);
            if (next == value) return;
            value = next;
            changed(next);
        }
        foreach (var (glyph, label, step) in new[] { ("\uE70E", "提高", 1), ("\uE70D", "降低", -1) })
        {
            var button = Visuals.Button(Visuals.Symbol(glyph, 7, Visuals.Gray), label + title, 17, 10,
                () => Apply(Read() + step));
            button.Padding = new Thickness(0);
            arrows.Children.Add(button);
        }
        input.LostKeyboardFocus += (_, _) => Apply(Read());
        input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Apply(Read());
            else if (e.Key == Key.Up) Apply(Read() + 1);
            else if (e.Key == Key.Down) Apply(Read() - 1);
            else return;
            e.Handled = true;
        };
        return new Border
        {
            Width = percent ? 70 : 62, Height = 25,
            BorderBrush = Visuals.Outline, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
            Background = Surface, Child = columns
        };
    }
}
