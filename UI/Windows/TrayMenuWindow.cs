using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Media;

namespace QuotaMonitor;

internal sealed class TrayMenuWindow : TrayPopupWindow
{
    private readonly List<Button> _items = new();
    private readonly TextBlock _visibleMark = Visuals.Symbol("\uE73E", 11, Visuals.Blue);
    private readonly TextBlock _lockedMark = Visuals.Symbol("\uE73E", 11, Visuals.Blue);

    internal TrayMenuWindow(Action dashboard, Action refresh, Action toggleWidget, Action toggleLock,
        Action settings, Action exit) : base("额度监控器菜单", 228)
    {
        Frame.CornerRadius = Plate.CornerRadius = new CornerRadius(12);
        Frame.Padding = new Thickness(6);
        var list = new StackPanel();
        Frame.Child = list;
        Add("额度监测中心", "\uE8A5", dashboard, prominent: true);
        Add("立即刷新额度", "\uE72C", refresh);
        Add("显示桌面小组件", "\uE890", toggleWidget, _visibleMark);
        Add("锁定小组件位置", "\uE72E", toggleLock, _lockedMark);
        Add("设置中心…", "\uE713", settings);
        var rule = Visuals.Rule(4, 4);
        rule.Margin = new Thickness(8, 4, 8, 4);
        list.Children.Add(rule);
        Add("退出", "\uE7E8", exit, destructive: true);

        void Add(string name, string icon, Action action, TextBlock? mark = null, bool prominent = false, bool destructive = false)
        {
            var row = Visuals.Columns(24, -1, 16);
            var symbol = Visuals.Symbol(icon, 16);
            var label = Visuals.Text(name, 12.5, bold: prominent);

            label.Margin = new Thickness(5, 0, 0, 0);
            Visuals.Place(row, symbol, 0);
            Visuals.Place(row, label, 1);
            if (mark != null) Visuals.Place(row, mark, 2);
            var item = new Button { Content = row, Height = 36, Tag = destructive ? "True" : null,
                Style = (Style)FindResource("TrayMenuItem") };
            var ink = new Binding(nameof(Button.Foreground)) { Source = item };
            symbol.SetBinding(TextBlock.ForegroundProperty, ink);
            label.SetBinding(TextBlock.ForegroundProperty, ink);
            AutomationProperties.SetName(item, name);
            item.Click += (_, _) => { Hide(); action(); };
            _items.Add(item);
            list.Children.Add(item);
        }
        KeyboardNavigation.SetTabNavigation(list, KeyboardNavigationMode.Cycle);
        Activated += (_, _) => _items[0].Focus();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
            if (e.Key is not (Key.Down or Key.Up)) return;
            int index = _items.FindIndex(item => item.IsKeyboardFocusWithin);
            int next = index < 0 ? (e.Key == Key.Up ? _items.Count - 1 : 0)
                : (index + (e.Key == Key.Down ? 1 : -1) + _items.Count) % _items.Count;
            _items[next].Focus();
            e.Handled = true;
        };
    }

    internal void Sync(bool visible, bool locked)
    {
        SetMark(_visibleMark, _items[2], visible);
        SetMark(_lockedMark, _items[3], locked);
        static void SetMark(TextBlock mark, Button button, bool on)
        {
            mark.Visibility = on ? Visibility.Visible : Visibility.Hidden;
            AutomationProperties.SetItemStatus(button, on ? "已勾选" : "未勾选");
        }
    }
}
