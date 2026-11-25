using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace QuotaMonitor;

internal sealed class WidgetWindow : GlassWindow
{
    public const int WidgetGutter = 8;
    private readonly ThemeManager _theme;
    private readonly Path _resizeMark;
    private readonly Grid _readings;
    private readonly TextBlock _checkedAt = Visuals.Text("", 11, Visuals.Gray);
    private readonly Grid _content = new();
    private readonly Preferences _prefs;
    private readonly List<Thumb> _handles = new();
    private readonly Dictionary<string, int?> _lastValues = new();
    private readonly List<(Quota Quota, QuotaHealth Health, FrameworkElement Element)> _rows = new();
    private readonly Dictionary<string, TextBlock> _countdowns = new();
    private (DisplayMode Mode, double Scale, bool Dark, WidgetSurfaceStyle Style, string Color,
        int Transparency, WidgetTextContrast Contrast, bool Countdown)? _appearance;
    private Panel? _rowPanel;
    internal bool IsMovingOrResizing { get; private set; }
    internal bool IsApplyingMetrics { get; private set; }
    public WidgetLayerMode LayerMode => _prefs.LayerMode;
    public event Action? RefreshRequested, DashboardRequested, SettingsRequested, Changed, ResetRequested, HideRequested;

    public WidgetWindow(Preferences preferences, ThemeManager theme)
        : base("额度监控器", preferences.WidgetWidth, translucent: true, gutter: WidgetGutter, noActivate: true)
    {
        _theme = theme;
        _prefs = preferences;
        SizeToContent = SizeToContent.Manual;
        MinWidth = OuterSize(Preferences.MinWidgetWidth);
        MinHeight = OuterSize(Preferences.MinWidgetHeight);
        MaxWidth = OuterSize(Preferences.MaxWidgetWidth);
        MaxHeight = OuterSize(Preferences.MaxWidgetHeight);
        Height = OuterSize(preferences.WidgetHeight);
        ShowActivated = false;
        Frame.Cursor = Cursors.SizeAll;
        var chrome = new Grid();
        Frame.Child = chrome;
        var content = _content;
        content.Margin = new Thickness(11, 7, 11, 7);
        content.RowDefinitions.Add(new RowDefinition());
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _readings = new Grid();
        _checkedAt.HorizontalAlignment = HorizontalAlignment.Right;
        _checkedAt.Margin = new Thickness(0, 2, 0, 0);
        Grid.SetRow(_checkedAt, 1);
        content.Children.Add(_readings);
        content.Children.Add(_checkedAt);
        chrome.Children.Add(content);

        Frame.MouseLeftButtonDown += (_, e) =>
        {
            if (!_prefs.WidgetLocked && e.LeftButton == MouseButtonState.Pressed)
            {
                e.Handled = true;
                IsMovingOrResizing = true;
                try { DragMove(); } finally { IsMovingOrResizing = false; }
                Desktop.SnapWidgetToEdges(this);
            }
        };

        _resizeMark = new Path
        {
            Data = Geometry.Parse("M 0,7 L 7,0 M 4,7 L 7,4"),
            Stroke = Visuals.Gray, StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 3, 3), Opacity = 0.58, IsHitTestVisible = false
        };
        chrome.Children.Add(_resizeMark);
        AddResizeHandle(chrome, HorizontalAlignment.Left, VerticalAlignment.Stretch, Cursors.SizeWE);
        AddResizeHandle(chrome, HorizontalAlignment.Right, VerticalAlignment.Stretch, Cursors.SizeWE);
        AddResizeHandle(chrome, HorizontalAlignment.Stretch, VerticalAlignment.Top, Cursors.SizeNS);
        AddResizeHandle(chrome, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, Cursors.SizeNS);
        AddResizeHandle(chrome, HorizontalAlignment.Left, VerticalAlignment.Top, Cursors.SizeNWSE);
        AddResizeHandle(chrome, HorizontalAlignment.Right, VerticalAlignment.Top, Cursors.SizeNESW);
        AddResizeHandle(chrome, HorizontalAlignment.Left, VerticalAlignment.Bottom, Cursors.SizeNESW);
        AddResizeHandle(chrome, HorizontalAlignment.Right, VerticalAlignment.Bottom, Cursors.SizeNWSE);
        Frame.ContextMenu = new ContextMenu { Style = (Style)FindResource("WidgetContextMenu") };
        BuildMenu(Frame.ContextMenu);
        Frame.ContextMenuOpening += (_, _) => BuildMenu(Frame.ContextMenu);
    }

    private void BuildMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        MenuItem Item(ItemsControl parent, string title, Action action, bool? selected = null)
        {
            var item = new MenuItem { Header = title,
                IsCheckable = selected.HasValue, IsChecked = selected ?? false };
            item.Click += (_, _) => action();
            parent.Items.Add(item);
            return item;
        }
        Item(menu, "立即刷新额度", () => RefreshRequested?.Invoke());
        Item(menu, "打开额度监测中心", () => DashboardRequested?.Invoke());
        Item(menu, "小组件设置…", () => SettingsRequested?.Invoke());
        menu.Items.Add(new Separator());
        var modes = new MenuItem { Header = "显示方式" };
        menu.Items.Add(modes);
        foreach (var mode in Enum.GetValues<DisplayMode>())
            Item(modes, LayoutName(mode), () => { _prefs.Mode = mode; Changed?.Invoke(); }, _prefs.Mode == mode);
        var layers = new MenuItem { Header = "窗口层级" };
        menu.Items.Add(layers);
        foreach (var layer in Enum.GetValues<WidgetLayerMode>())
            Item(layers, LayerName(layer), () => { _prefs.LayerMode = layer; Changed?.Invoke(); }, LayerMode == layer);
        Item(menu, "锁定位置与尺寸", () => { _prefs.WidgetLocked = !_prefs.WidgetLocked; Changed?.Invoke(); }, _prefs.WidgetLocked);
        Item(menu, "恢复默认尺寸", () => ResetRequested?.Invoke());
        Item(menu, "隐藏桌面小组件", () => HideRequested?.Invoke());
    }

    internal static string LayoutName(DisplayMode mode) => mode switch
        { DisplayMode.Bars => "进度条", DisplayMode.Rings => "圆环网格", _ => "迷你胶囊" };
    internal static string LayerName(WidgetLayerMode layer) => layer switch
        { WidgetLayerMode.DesktopPinned => "贴桌面底层", WidgetLayerMode.Normal => "普通窗口", _ => "始终置顶" };


    private static double OuterSize(double contentSize) => contentSize + WidgetGutter * 2;

    public void ResetSize()
    {
        double width = Math.Max(OuterSize(Preferences.DefaultWidgetWidth * _prefs.WidgetScale), MinWidth);
        double height = Math.Clamp(OuterSize(Preferences.DefaultWidgetHeight * _prefs.WidgetScale), MinHeight, MaxHeight);
        if (!Desktop.TryGetBounds(this, out var bounds))
        {
            Width = width;
            Height = height;
            return;
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        bounds.Width = (int)Math.Round(width * dpi.DpiScaleX);
        bounds.Height = (int)Math.Round(height * dpi.DpiScaleY);
        Desktop.SetBounds(this, bounds);
    }

    private void AddResizeHandle(Grid chrome, HorizontalAlignment horizontal, VerticalAlignment vertical, Cursor cursor)
    {
        bool corner = horizontal != HorizontalAlignment.Stretch && vertical != VerticalAlignment.Stretch;
        var thumb = new Thumb
        {
            Template = (ControlTemplate)Application.Current.FindResource("WidgetResizeHandle"),
            Background = Visuals.ColorBrush("#01000000"), Focusable = false,
            HorizontalAlignment = horizontal, VerticalAlignment = vertical, Cursor = cursor,
            Width = horizontal == HorizontalAlignment.Stretch ? double.NaN : corner ? 12 : 5,
            Height = vertical == VerticalAlignment.Stretch ? double.NaN : corner ? 12 : 5
        };
        if (horizontal == HorizontalAlignment.Right && vertical == VerticalAlignment.Bottom)
            AutomationProperties.SetHelpText(thumb, "拖动调整宽高");
        chrome.Children.Add(thumb);
        _handles.Add(thumb);

        Drawing.Rectangle start = default;
        Drawing.Point pointer = default;
        DpiScale dpi = default;
        thumb.DragStarted += (_, _) =>
        {
            IsMovingOrResizing = true;
            Desktop.TryGetBounds(this, out start);
            pointer = Forms.Control.MousePosition;
            dpi = VisualTreeHelper.GetDpi(this);
        };
        thumb.DragCompleted += (_, _) => { IsMovingOrResizing = false; Desktop.SnapWidgetToEdges(this); };
        thumb.DragDelta += (_, _) =>
        {
            if (_prefs.WidgetLocked || start.IsEmpty) return;
            var current = Forms.Control.MousePosition;
            var delta = new Drawing.Point(current.X - pointer.X, current.Y - pointer.Y);
            Desktop.SetBounds(this, ResizedBounds(start, delta, dpi, horizontal, vertical, MinHeight, MinWidth, MaxHeight, MaxWidth));
        };
    }

    internal static Drawing.Rectangle ResizedBounds(Drawing.Rectangle start, Drawing.Point delta, DpiScale dpi,
        HorizontalAlignment horizontal, VerticalAlignment vertical,
        double minimumHeight = Preferences.MinWidgetHeight + WidgetGutter * 2,
        double minimumWidth = Preferences.MinWidgetWidth + WidgetGutter * 2, double maximumHeight = Preferences.MaxWidgetHeight + WidgetGutter * 2,
        double maximumWidth = Preferences.MaxWidgetWidth + WidgetGutter * 2)
    {
        var bounds = start;
        if (horizontal != HorizontalAlignment.Stretch)
        {
            bool left = horizontal == HorizontalAlignment.Left;
            bounds.Width = Math.Clamp(start.Width + (left ? -delta.X : delta.X),
                (int)Math.Ceiling(minimumWidth * dpi.DpiScaleX),
                (int)Math.Floor(maximumWidth * dpi.DpiScaleX));
            if (left) bounds.X = start.Right - bounds.Width;
        }
        if (vertical != VerticalAlignment.Stretch)
        {
            bool top = vertical == VerticalAlignment.Top;
            bounds.Height = Math.Clamp(start.Height + (top ? -delta.Y : delta.Y),
                (int)Math.Ceiling(minimumHeight * dpi.DpiScaleY),
                Math.Max((int)Math.Ceiling(minimumHeight * dpi.DpiScaleY), (int)Math.Floor(maximumHeight * dpi.DpiScaleY)));
            if (top) bounds.Y = start.Bottom - bounds.Height;
        }
        return bounds;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.InvokeAsync(() =>
        {
            if (IsVisible && !IsMovingOrResizing)
            {
                UpdateLayout();
                Desktop.SnapWidgetToEdges(this);
            }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    public void Present(QuotaReading reading, Preferences settings)
    {
        var quotas = DesktopItems.Select(reading, settings);
        var stale = quotas.Where(q => q.LastReadingNote is not null).ToList();
        Visuals.SetText(_checkedAt, quotas.Count == 0 ? "可在小组件设置中选择" : stale.Count switch
        {
            0 => reading.CheckTimeText,
            1 => $"{stale[0].Name} {stale[0].LastReadingNote}",
            _ => $"{stale.Count} 项为上次读数"
        });
        var help = string.Join(Environment.NewLine, stale.Select(q => $"{q.Name} · {q.Status ?? "尚未更新"} · {q.LastReadingNote}"));
        if (AutomationProperties.GetHelpText(_checkedAt) != help) AutomationProperties.SetHelpText(_checkedAt, help);
        var appearance = (settings.Mode, settings.WidgetScale, _theme.IsDark, settings.SurfaceStyle, settings.CustomBackground,
            settings.Transparency, settings.TextContrast, settings.ShowCountdownOnWidget);
        var scale = settings.WidgetScale;
        bool pill = settings.Mode == DisplayMode.CompactPill;
        bool sameLayout = _appearance == appearance && _rows.Select(row => row.Quota.BucketKey).SequenceEqual(quotas.Select(q => q.BucketKey));
        var healths = quotas.Select(q => ThemeManager.Health(q.Remaining,
            settings.PerBucketLowThresholds.GetValueOrDefault(q.BucketKey!, settings.LowThreshold), settings.CriticalThreshold)).ToArray();
        Frame.Cursor = settings.WidgetLocked ? Cursors.Arrow : Cursors.SizeAll;
        _resizeMark.Visibility = pill || settings.WidgetLocked ? Visibility.Collapsed : Visibility.Visible;
        foreach (var handle in _handles) handle.IsEnabled = !settings.WidgetLocked;
        for (int i = 0; sameLayout && i < quotas.Count; i++)
        {
            var q = quotas[i];
            if (_countdowns.TryGetValue(q.BucketKey!, out var clock) && q.ResetsAt is DateTimeOffset reset)
                Visuals.SetText(clock, CountdownFormatter.Compact(reset, DateTimeOffset.Now));
            DescribeForAccessibility(_rows[i].Element, q, healths[i] is QuotaHealth.Low or QuotaHealth.Critical);
        }
        bool RowChanged(int i) => _rows[i].Quota != (quotas[i] with { LastSuccessAt = null }) || _rows[i].Health != healths[i];
        if (sameLayout && !Enumerable.Range(0, quotas.Count).Any(RowChanged))
            return;
        var custom = (Color)ColorConverter.ConvertFromString(settings.CustomBackground);
        var surface = ThemeManager.Surface(settings.SurfaceStyle, _theme.Color("Widget.PlateDefault"), custom,
            settings.Transparency, _theme.IsDark, settings.TextContrast);
        bool glass = settings.Transparency >= 40 || settings.SurfaceStyle == WidgetSurfaceStyle.HudGlass;
        var ink = _theme.Brush("Text.Primary", surface.LightInk);
        var muted = glass ? ink : _theme.Brush("Text.Secondary", surface.LightInk);
        var shadow = glass ? new DropShadowEffect { Color = surface.LightInk ? Colors.Black : Colors.White,
            RenderingBias = RenderingBias.Performance, BlurRadius = 5, ShadowDepth = 1, Direction = 270, Opacity = surface.LightInk ? .62 : .5 } : null;
        shadow?.Freeze();
        if (_appearance != appearance) ApplySurface(surface, muted, shadow);

        int columns = quotas.Count <= 3 ? Math.Max(1, quotas.Count) : quotas.Count == 4 ? 2 : 3;
        int rows = (quotas.Count + columns - 1) / columns;
        if (!sameLayout)
        {
            bool resize = _appearance?.Scale != scale || _appearance?.Mode != settings.Mode;
            _appearance = appearance;
            ConfigureLayout(settings, quotas.Count, columns, rows, resize);
        }
        if (quotas.Count == 0)
        {
            var empty = Visuals.Text("暂无可显示的额度", 12, muted);
            empty.Effect = shadow;
            _readings.Children.Add(empty);
            return;
        }
        for (int index = 0; index < quotas.Count; index++)
        {
            if (sameLayout && !RowChanged(index)) continue;
            var quota = quotas[index];
            var health = healths[index];
            bool low = health is QuotaHealth.Low or QuotaHealth.Critical;
            var accent = _theme.Brush($"{health}.Text", surface.LightInk);
            var fill = _theme.Brush($"{health}.Fill", surface.LightInk);
            var track = _theme.Brush($"{health}.Track", surface.LightInk);
            if (glass && surface.LightInk)
            {
                fill = health switch
                {
                    QuotaHealth.Normal => Visuals.Frozen(new LinearGradientBrush(Color.FromRgb(56, 189, 248), Color.FromRgb(59, 130, 246), 0)),
                    QuotaHealth.Low => Visuals.ColorBrush("#FBBF24"),
                    QuotaHealth.Critical => Visuals.ColorBrush("#FB7185"),
                    _ => Brushes.Transparent
                };
                track = Visuals.ColorBrush("#2EFFFFFF");
            }
            var colors = new MeterColors(glass ? ink : accent, fill, track, fill);
            FrameworkElement element;
            if (settings.Mode == DisplayMode.Rings)
            {
                element = QuotaRingGauge.Create(quota, low, ink, colors, quotas.Count, shadow, scale);
                Grid.SetRow(element, index / columns);
                Grid.SetColumn(element, index % columns);
            }
            else if (pill)
            {
                element = PillReading(quota, low, ink, colors, shadow, index, scale);
            }
            else
            {
                var key = quota.BucketKey!;
                _countdowns.Remove(key);
                element = BarReading(quota, low, ink, colors, shadow, settings.ShowCountdownOnWidget,
                    _lastValues.GetValueOrDefault(key), scale);
                _lastValues[key] = quota.Remaining;
                Grid.SetRow(element, index);
                if (!sameLayout) _readings.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
            var row = (quota with { LastSuccessAt = null }, health, element);
            if (sameLayout)
            {
                _rows[index] = row;
                _rowPanel!.Children.RemoveAt(index);
            }
            else _rows.Add(row);
            _rowPanel!.Children.Insert(index, element);
        }
    }

    private void ApplySurface(WidgetSurface surface, Brush muted, Effect? shadow)
    {
        var background = Visuals.Alpha(surface.Color, surface.Alpha);
        Plate.Background = surface.Alpha < 1 ? background : Brushes.Transparent;
        SetTextSurface(background, opaque: surface.Alpha == 1);
        Shadow.BorderBrush = Visuals.Alpha(_theme.Color("Widget.ShadowColor"), surface.Alpha * (_theme.IsDark ? .42 : .18));
        var edge = _theme.Color("Widget.BorderDefault");
        Frame.BorderBrush = Visuals.Frozen(new LinearGradientBrush(Color.FromArgb((byte)(105 * surface.Alpha), 255, 255, 255),
            Color.FromArgb((byte)(edge.A * surface.Alpha), edge.R, edge.G, edge.B), 90));
        _resizeMark.Stroke = muted;
        _checkedAt.Foreground = muted;
        _checkedAt.Effect = shadow;
    }

    private void ConfigureLayout(Preferences settings, int count, int columns, int rows, bool resize)
    {
        var scale = settings.WidgetScale;
        bool pill = settings.Mode == DisplayMode.CompactPill;
        bool bars = settings.Mode == DisplayMode.Bars;
        _readings.Children.Clear();
        _readings.RowDefinitions.Clear();
        _countdowns.Clear();
        _rows.Clear();
        var radius = new CornerRadius((pill ? 18 : 12) * scale);
        Frame.CornerRadius = Plate.CornerRadius = Shadow.CornerRadius = radius;
        _content.Margin = new Thickness(11 * scale, 7 * scale, 11 * scale, 7 * scale);
        _checkedAt.FontSize = Math.Max(11, 11 * scale);
        _checkedAt.Visibility = pill ? Visibility.Collapsed : Visibility.Visible;
        _content.RowDefinitions[0].Height = bars ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        _content.VerticalAlignment = bars ? VerticalAlignment.Center : VerticalAlignment.Stretch;
        double required = bars ? 36 + 30 * count : 34 + rows * 72;
        IsApplyingMetrics = true;
        try
        {
            MinWidth = OuterSize((pill ? Math.Max(Preferences.MinWidgetWidth, count * 60 + 24) : Preferences.MinWidgetWidth) * scale);
            MinHeight = OuterSize((pill ? 36 : Math.Max(Preferences.MinWidgetHeight, required)) * scale);
            MaxWidth = OuterSize(Preferences.MaxWidgetWidth * scale);
            MaxHeight = OuterSize((pill ? 36 : Preferences.MaxWidgetHeight) * scale);
            if (resize)
            {
                Width = OuterSize(settings.WidgetWidth * scale);
                Height = OuterSize((pill ? 36 : settings.WidgetHeight) * scale);
            }
        }
        finally { IsApplyingMetrics = false; }
        _rowPanel = _readings;
        if (pill) _rowPanel = new UniformGrid { Rows = 1 };
        if (settings.Mode == DisplayMode.Rings)
        {
            var grid = new Grid();
            for (int c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition());
            for (int r = 0; r < rows; r++) grid.RowDefinitions.Add(new RowDefinition());
            _rowPanel = grid;
        }
        if (_rowPanel != _readings && count > 0) _readings.Children.Add(_rowPanel);
    }

    private static FrameworkElement PillReading(Quota quota, bool low, Brush ink, MeterColors colors, Effect? shadow, int index, double scale)
    {
        var pair = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        pair.Children.Add(ServiceMark.Create(quota.Service, ink, 14 * scale, quota.BucketKey));
        pair.Effect = shadow;
        var number = Visuals.Text(DisplayedValue(quota), Math.Max(14, 14 * scale), colors.Number, true);
        number.Margin = new Thickness(3, 0, 0, 0);
        pair.Children.Add(number);
        var cell = new Grid();
        cell.Children.Add(pair);
        if (index > 0)
            cell.Children.Add(new Border { Width = 1, Height = 12 * scale, Opacity = .22, Background = ink,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left });
        DescribeForAccessibility(cell, quota, low);
        return cell;
    }

    private FrameworkElement BarReading(Quota quota, bool low, Brush ink, MeterColors colors,
        Effect? shadow, bool countdown, int? previous, double scale)
    {
        var entry = new StackPanel { MinHeight = 30 * scale, VerticalAlignment = VerticalAlignment.Center };
        var label = Visuals.Columns(19 * scale, -1, 0, 0, low ? 16 * scale : 1);
        Visuals.Place(label, ServiceMark.Create(quota.Service, ink, 14 * scale, quota.BucketKey), 0);
        var name = Visuals.Text(quota.Name, Math.Max(12.5, 12.5 * scale), ink, bold: true);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.Margin = new Thickness(0, 0, 4, 0);
        Visuals.Place(label, name, 1);
        if (countdown && quota.ResetsAt is DateTimeOffset reset)
        {
            var time = Visuals.Text(CountdownFormatter.Compact(reset, DateTimeOffset.Now), Math.Max(11, 11 * scale), ink);
            _countdowns[quota.BucketKey!] = time;
            time.Margin = new Thickness(2, 0, 7, 0);
            Visuals.Place(label, time, 2);
        }
        Visuals.Place(label, Visuals.Text(DisplayedValue(quota), Math.Max(14, 15 * scale),
            quota.Remaining.HasValue ? colors.Number : ink, bold: true), 3);
        if (low) Visuals.Place(label, Warning(colors.Arc, size: 12 * scale), 4);
        label.Effect = shadow;
        entry.Children.Add(label);
        var meter = QuotaProgressBar.Create(quota.Remaining, 4 * scale, colors.Track, colors.Fill, previous);
        meter.Margin = new Thickness(0, 2, 0, 0);
        entry.Children.Add(meter);
        DescribeForAccessibility(entry, quota, low);
        return entry;
    }

    internal static string DisplayedValue(Quota quota) => quota.Remaining is int remaining ? $"{remaining}%" : "—";

    internal static void DescribeForAccessibility(FrameworkElement entry, Quota quota, bool low)
    {
        var description = quota.Remaining is int remaining
            ? $"剩余 {remaining}%{(low ? "，额度偏低" : "")}"
            : quota.Status ?? "暂未读取";
        if (quota.Scope is string scope)
            description = $"{scope}，{description}";
        if (quota.ResetsAt.HasValue || !string.IsNullOrWhiteSpace(quota.ResetText))
            description += $"，{CountdownFormatter.Format(quota.ResetsAt, DateTimeOffset.Now, quota.ResetText)}";
        if (quota.LastReadingNote is string note) description += $"，{quota.Status ?? "尚未更新"}，{note}";
        var name = $"{quota.Name}，{description}";
        if (AutomationProperties.GetName(entry) != name) AutomationProperties.SetName(entry, name);
        if (AutomationProperties.GetHelpText(entry) != description) AutomationProperties.SetHelpText(entry, description);
    }

    internal static FrameworkElement Warning(Brush accent, double size)
    {
        var glyph = Visuals.Symbol("\uE7BA", size, accent);
        AutomationProperties.SetHelpText(glyph, "剩余额度偏低");
        return glyph;
    }
}
