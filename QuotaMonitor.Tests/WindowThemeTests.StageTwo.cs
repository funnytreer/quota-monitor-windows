using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace QuotaMonitor.Tests;

public partial class WindowThemeTests
{
    private static void CheckStageTwoWindows(ThemeManager theme)
    {
        var p = new Preferences();
        p.HiddenBuckets.Remove("chatgpt.5h");
        var reading = DesktopItemTests.Sample();
        var widget = new WidgetWindow(p, theme);
        var dashboard = new DashboardPopover(p, null);
        void Apply()
        {
            theme.Apply(p.ThemeMode);
            widget.Present(reading, p);
            dashboard.Present(reading, DateTimeOffset.Now.AddMinutes(5));
        }
        var settings = new SettingsWindow(p, Apply, theme);
        Assert.False(settings.AllowsTransparency);
        Assert.Equal(6, System.Windows.Shell.WindowChrome.GetWindowChrome(settings).ResizeBorderThickness.Left);
        Assert.True(widget.AllowsTransparency && dashboard.AllowsTransparency);
        settings.UpdateReadings(reading);
        widget.Changed += Apply;
        p.ThemeMode = ThemePreference.Light;
        Apply();
        var frame = (Grid)widget.Content;
        var settingsContent = (Grid)settings.Content;
        var dashboardContent = (Grid)dashboard.Content;
        static void Layout(FrameworkElement host, double width, double height)
        {
            host.Measure(new Size(width, height));
            host.Arrange(new Rect(0, 0, width, height));
            host.UpdateLayout();
        }
        void Click(FrameworkElement host, string name)
        {
            host.UpdateLayout();
            var button = Assert.Single(Descendants(host).OfType<Button>(), b => AutomationProperties.GetName(b) == name);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            host.UpdateLayout();
        }
        Layout(frame, 295, widget.MinHeight);
        var meters = Descendants(frame).OfType<Border>().Where(b => b.Height == 4).ToList();
        Assert.Equal(5, meters.Count);
        Assert.All(meters, meter => Assert.Equal(4, meter.ActualHeight));
        var points = meters.Select(m => m.TransformToAncestor(frame).Transform(new Point()).Y).ToArray();
        for (int i = 1; i < points.Length; i++) Assert.InRange(points[i] - points[i - 1], 29, 31);
        Capture(frame, "widget-bars");
        Layout(settingsContent, 640, 500);
        double originalWidth = widget.Width;
        Click(settingsContent, "大号 130%");
        Assert.Equal(1.3, p.WidgetScale);
        Assert.Equal((originalWidth - 16) * 1.3 + 16, widget.Width, 5);
        Layout(frame, widget.Width, widget.MinHeight);
        Assert.Contains(Descendants(frame).OfType<TextBlock>(), t => t.FontSize == 12.5 * 1.3);
        Click(settingsContent, "标准 100%");
        Assert.Equal(originalWidth, widget.Width);
        Layout(frame, 295, widget.MinHeight);
        var unchangedMeter = Descendants(frame).OfType<Border>().First(b => b.Height == 4);
        widget.Present(reading with { UpdatedAt = DateTimeOffset.Now.AddMinutes(1) }, p);
        Assert.Contains(unchangedMeter, Descendants(frame));
        var slider = Assert.Single(Descendants(settingsContent).OfType<Slider>());
        foreach (var opacity in new[] { 0, 1, 50, 100 })
        {
            slider.Value = opacity;
            Assert.Equal(opacity, p.Transparency);
        }
        var widgetFrame = (Border)frame.Children[^1];
        Assert.Equal(1, ((SolidColorBrush)widgetFrame.Background).Color.A);
        Assert.True(widgetFrame.IsHitTestVisible);
        Assert.Equal(Cursors.SizeAll, widgetFrame.Cursor);
        var refreshMenu = widgetFrame.ContextMenu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "立即刷新额度"));
        bool requested = false;
        widget.RefreshRequested += () => requested = true;
        refreshMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, refreshMenu));
        Assert.True(requested);
        slider.Value = 0;
        Click(settingsContent, "圆环网格");
        Layout(frame, 295, widget.MinHeight);
        var grid = Assert.Single(Descendants(frame).OfType<Grid>(), g => g.ColumnDefinitions.Count == 3 && g.RowDefinitions.Count == 2);
        Assert.Equal(5, grid.Children.Count);
        Capture(frame, "widget-rings");
        Click(settingsContent, "深色");
        Layout(dashboardContent, 356, 624);
        Assert.True(theme.IsDark);
        Assert.Equal(theme.Color("Surface.Window"), ((SolidColorBrush)((Border)dashboardContent.Children[^1]).Background).Color);
        Capture(settingsContent, "settings-dark");
        Click(settingsContent, "浅色");
        Assert.False(theme.IsDark);
        Capture(settingsContent, "settings-light");
        Click(settingsContent, "跟随系统");
        Assert.Equal(ThemePreference.System, theme.Preference);
        Click(settingsContent, "迷你胶囊");
        Layout(frame, widget.MinWidth, widget.MinHeight);
        var pill = Assert.Single(Descendants(frame).OfType<UniformGrid>());
        Assert.Equal(1, pill.Rows);
        Assert.Equal(5, pill.Children.Count);
        Assert.All(pill.Children.Cast<FrameworkElement>(), c => Assert.Contains("重置", AutomationProperties.GetHelpText(c)));
        Assert.Equal(36, widget.MaxHeight - WidgetWindow.WidgetGutter * 2);
        Capture(frame, "widget-pill");
        Click(settingsContent, "服务与显示项");
        Assert.DoesNotContain(Descendants(settingsContent).OfType<TextBlock>(), t => t.Text.Contains("Gemini 网页版"));
        var desktopLabels = Descendants(settingsContent).OfType<CheckBox>().Select(c => c.Content as string).ToArray();
        Assert.Equal(5, desktopLabels.Length);
        Assert.Contains("Antigravity · Gemini 每周", desktopLabels);
        Assert.Contains("Antigravity · Claude 每周", desktopLabels);
        Assert.DoesNotContain(desktopLabels, s => s?.Contains("网页版") == true);
        Capture(settingsContent, "services");
        string move = "上移 " + DesktopItems.Catalog.Single(b => b.BucketKey == "grok.7d").DisplayName;
        int before = p.WidgetBucketOrder.IndexOf("grok.7d");
        Click(settingsContent, move);
        Assert.Equal(before - 1, p.WidgetBucketOrder.IndexOf("grok.7d"));
        Click(settingsContent, "提醒与通知");
        Assert.DoesNotContain(Descendants(settingsContent).OfType<TextBlock>(), t => t.Text.Contains("Gemini 网页版"));
        bool notified = false;
        settings.TestRequested += () => notified = true;
        Click(settingsContent, "发送测试通知");
        Assert.True(notified);
        Click(settingsContent, "常规与诊断");
        Click(settingsContent, "3m");
        Assert.Equal(3, p.PollingIntervalMinutes);

        Layout(dashboardContent, 356, 624);
        var scroll = Assert.Single(Descendants(dashboardContent).OfType<ScrollViewer>(), v => v.Content is StackPanel);
        Assert.Equal(3, ((StackPanel)scroll.Content).Children.Count);
        Assert.DoesNotContain(Descendants(dashboardContent).OfType<TextBlock>(), t => t.Text == "Gemini 网页版");
        Assert.Equal(340, dashboard.Width - 16);
        Assert.Equal("额度监测中心", dashboard.Title);
        var visibility = Assert.Single(Descendants(dashboardContent).OfType<Button>(), b => AutomationProperties.GetName(b) == "切换桌面小组件");
        foreach (bool show in new[] { false, true })
        {
            p.WidgetVisible = show;
            dashboard.UpdateFooter();
            dashboardContent.UpdateLayout();
            var label = Assert.Single(Descendants(visibility).OfType<TextBlock>(), t => t.Text == (show ? "隐藏小组件" : "显示小组件"));
            var textBounds = label.TransformToAncestor(visibility).TransformBounds(new Rect(label.RenderSize));
            Assert.True(textBounds.Right <= visibility.ActualWidth - visibility.Padding.Right);
            Assert.True(textBounds.Left >= visibility.Padding.Left);
        }
        Capture(dashboardContent, "dashboard");
        var serviceCards = ((StackPanel)scroll.Content).Children.Cast<UIElement>().ToArray();
        var disclosure = Descendants(dashboardContent).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "展开全部模型周期  ▾");
        var disclosurePosition = disclosure.TranslatePoint(new Point(), dashboardContent);
        dashboard.Present(reading with { UpdatedAt = DateTimeOffset.Now }, DateTimeOffset.Now.AddMinutes(1));
        Assert.Equal(serviceCards, ((StackPanel)scroll.Content).Children.Cast<UIElement>().ToArray());
        Click(dashboardContent, "展开全部模型周期  ▾");
        Assert.Equal(serviceCards, ((StackPanel)scroll.Content).Children.Cast<UIElement>().ToArray());
        Assert.Equal("收起模型周期  ▴", AutomationProperties.GetName(disclosure));
        Assert.Equal(disclosurePosition, disclosure.TranslatePoint(new Point(), dashboardContent));
        Assert.Contains(Descendants(dashboardContent).OfType<TextBlock>(), b => b.Text == "Gemini 4 Argon");
        Assert.Contains(Descendants(dashboardContent).OfType<TextBlock>(), b => b.Text.Contains("后重置 ("));
        Assert.True(scroll.ScrollableHeight > 0);
        Assert.Equal(0, scroll.ScrollableWidth);
        Click(dashboardContent, "收起模型周期  ▴");
        Assert.DoesNotContain(Descendants(dashboardContent).OfType<TextBlock>(), b => b.Text == "Gemini 4 Argon");
        ServiceKind? connection = null;
        dashboard.ConnectRequested += kind => connection = kind;
        var changedServices = reading.Services.Select(s => s.Service == ServiceKind.Grok
            ? s with { State = ServiceConnectionState.NotConfigured, StatusText = "未连接" } : s).ToArray();
        dashboard.Present(new QuotaReading(reading.UpdatedAt, changedServices), DateTimeOffset.Now.AddMinutes(5));
        Layout(dashboardContent, 356, 624);
        Assert.DoesNotContain(Descendants(dashboardContent).OfType<TextBlock>(), t => t.Text == "Gemini 网页版");
        Click(dashboardContent, "连接 Grok");
        Assert.Equal(ServiceKind.Grok, connection);
        dashboard.Present(reading, DateTimeOffset.Now.AddMinutes(5));
        var menu = ((Border)frame.Children[^1]).ContextMenu;
        MenuItem Item(ItemsControl parent, string name) => parent.Items.OfType<MenuItem>().Single(m => Equals(m.Header, name));
        Layout(menu, 168, 246);
        var modes = Item(menu, "显示方式");
        var refresh = Item(menu, "立即刷新额度");
        var lockItem = Item(menu, "锁定位置与尺寸");
        foreach (var entry in new[] { refresh, modes, lockItem })
        {
            var header = Assert.IsType<ContentPresenter>(entry.Template.FindName("HeaderText", entry));
            Assert.InRange(header.TranslatePoint(new Point(), entry).X, 9, 11);
        }
        FrameworkElement Indicator(MenuItem entry, string name) =>
            Assert.IsAssignableFrom<FrameworkElement>(entry.Template.FindName(name, entry));
        Assert.Equal(Visibility.Collapsed, Indicator(refresh, "Check").Visibility);
        Assert.Equal(Visibility.Visible, Indicator(modes, "Arrow").Visibility);
        Capture(menu, "widget-context-menu");
        menu.Placement = PlacementMode.AbsolutePoint;
        menu.HorizontalOffset = -20000;
        menu.VerticalOffset = -20000;
        menu.IsOpen = true;
        modes.IsSubmenuOpen = true;
        menu.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
        foreach (var choice in modes.Items.OfType<MenuItem>())
        {
            choice.ApplyTemplate();
            Assert.Equal(choice.IsChecked ? Visibility.Visible : Visibility.Collapsed, Indicator(choice, "Check").Visibility);
        }
        modes.IsSubmenuOpen = false;
        menu.IsOpen = false;
        void Select(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
        Select(Item(menu, "锁定位置与尺寸"));
        Assert.True(p.WidgetLocked);
        Assert.Equal(Cursors.Arrow, ((Border)frame.Children[^1]).Cursor);
        Assert.All(Descendants(frame).OfType<Thumb>(), t => Assert.False(t.IsEnabled));

        // Exercise real HWND flags without activating a foreground app or moving the user's pointer.
        widget.Left = dashboard.Left = -20000;
        widget.Top = dashboard.Top = -20000;
        dashboard.ShowActivated = false;
        var settingsHwnd = new WindowInteropHelper(settings).EnsureHandle();
        Assert.Equal(0, GetWindowLong(settingsHwnd, -20) & 0x80000); // WS_EX_LAYERED
        var dpi = VisualTreeHelper.GetDpi(settings);
        Assert.Equal(dpi.DpiScaleX >= 1.25 ? TextFormattingMode.Ideal : TextFormattingMode.Display,
            TextOptions.GetTextFormattingMode(settingsContent));
        widget.Show();
        foreach (var layer in new[] { WidgetLayerMode.AlwaysOnTop, WidgetLayerMode.Normal, WidgetLayerMode.DesktopPinned })
        {
            Select(Item(Item(menu, "窗口层级"), WidgetWindow.LayerName(layer)));
            Desktop.ApplyWidgetLayer(widget);
            var style = GetWindowLong(new WindowInteropHelper(widget).Handle, -20);
            Assert.Equal(layer == WidgetLayerMode.AlwaysOnTop, (style & 8) != 0);
            Assert.True((style & 0x80) != 0);
        }
        dashboard.Show();
        static void AssertStationaryOnOpen(FrameworkElement content)
        {
            Assert.True(content.RenderTransform.Value.IsIdentity);
            Assert.False(content.RenderTransform.HasAnimatedProperties);
            Assert.Equal(1d, content.Opacity);
            Assert.False(DependencyPropertyHelper.GetValueSource(content, UIElement.OpacityProperty).IsAnimated);
        }
        AssertStationaryOnOpen(dashboardContent);
        // The dashboard is offscreen and was shown without activation. An external button-down
        // must still dismiss it, without moving the user's mouse or asking Windows to change focus.
        var outsideListener = typeof(TrayPopupWindow).GetField("_outsideClick", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var surface = (Border)dashboardContent.Children[^1];
        System.Drawing.Point OnSurface(double x, double y)
        {
            var point = surface.PointToScreen(new Point(x, y));
            return new System.Drawing.Point((int)Math.Round(point.X), (int)Math.Round(point.Y));
        }
        var midX = surface.ActualWidth / 2;
        var midY = surface.ActualHeight / 2;
        var panelHandle = new WindowInteropHelper(dashboard).Handle;
        void CheckClick(System.Drawing.Point point, IntPtr target, bool shouldStayOpen)
        {
            Assert.IsType<PopupOutsideClick>(outsideListener.GetValue(dashboard)).HandleButtonDown(point, target);
            dashboard.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            Assert.Equal(shouldStayOpen, dashboard.IsVisible);
            if (!shouldStayOpen) dashboard.Show();
        }
        CheckClick(OnSurface(midX, midY), panelHandle, true);
        CheckClick(OnSurface(1, midY), panelHandle, true);
        CheckClick(OnSurface(-2, midY), panelHandle, false);
        CheckClick(OnSurface(midX, surface.ActualHeight + 2), panelHandle, false);
        CheckClick(OnSurface(1, 1), panelHandle, false);
        CheckClick(OnSurface(surface.ActualWidth - 1, surface.ActualHeight - 1), panelHandle, false);
        CheckClick(System.Drawing.Point.Empty, GetDesktopWindow(), false);
        CheckClick(System.Drawing.Point.Empty, FindWindow("Shell_TrayWnd", null), false);
        Click(dashboardContent, "固定监测中心");
        Assert.Null(outsideListener.GetValue(dashboard));
        void Deactivate() => typeof(Window).GetMethod("OnDeactivated", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dashboard, [EventArgs.Empty]);
        Deactivate();
        Assert.True(dashboard.IsVisible);
        Click(dashboardContent, "固定监测中心");
        Deactivate();
        Assert.False(dashboard.IsVisible);
        bool tapped = false;
        var tray = new TrayMenuWindow(() => {}, () => tapped = true, () => {}, () => {}, () => {}, () => {});
        tray.Sync(true, false);
        var trayRoot = (Grid)tray.Content;
        tray.Left = tray.Top = -20000;
        tray.ShowActivated = false;
        tray.Show();
        AssertStationaryOnOpen(trayRoot);
        Assert.IsType<PopupOutsideClick>(outsideListener.GetValue(tray))
            .HandleButtonDown(System.Drawing.Point.Empty, GetDesktopWindow());
        tray.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        Assert.False(tray.IsVisible);
        Layout(trayRoot, 244, 256);
        var entries = Descendants(trayRoot).OfType<Button>().ToArray();
        Assert.Equal(6, entries.Length);
        Assert.Equal("已勾选", AutomationProperties.GetItemStatus(entries[2]));
        Assert.Equal("未勾选", AutomationProperties.GetItemStatus(entries[3]));
        theme.Apply(ThemePreference.Dark);
        trayRoot.UpdateLayout();
        var firstLabel = Descendants(trayRoot).OfType<TextBlock>().Single(t => t.Text == "额度监测中心");
        Assert.Equal(theme.Color("Text.Primary"), ((SolidColorBrush)firstLabel.Foreground).Color);
        Capture(trayRoot, "tray-menu-dark");
        theme.Apply(ThemePreference.Light);
        trayRoot.UpdateLayout();
        Assert.Equal(theme.Color("Text.Primary"), ((SolidColorBrush)firstLabel.Foreground).Color);
        Capture(trayRoot, "tray-menu-light");
        Click(trayRoot, "立即刷新额度");
        Assert.True(tapped);
        tray.Close();
        settings.Close();
        widget.Close();
        dashboard.Close();
    }

    private static void CheckLayerSelector(SettingsWindow settings, Preferences prefs, ThemeManager theme)
    {
        settings.Left = settings.Top = -20000;
        settings.ShowActivated = false;
        settings.Show();
        var root = (Grid)settings.Content;
        var layer = Assert.Single(Descendants(root).OfType<ComboBox>());
        var scroll = Assert.Single(Descendants(root).OfType<ScrollViewer>(), s => s.Content is StackPanel);
        scroll.ScrollToEnd();
        root.UpdateLayout();

        var caption = Assert.Single(Descendants(layer).OfType<ContentPresenter>());
        var row = (Grid)layer.Parent;
        var center = caption.TranslatePoint(new Point(0, caption.ActualHeight / 2), row);
        Assert.InRange(Math.Abs(center.Y - row.ActualHeight / 2), 0, 1);
        Assert.True(layer.ActualHeight < row.ActualHeight && layer.ActualWidth < row.ColumnDefinitions[1].ActualWidth);
        Assert.InRange(Math.Abs(layer.TranslatePoint(new Point(layer.ActualWidth, 0), row).X - row.ActualWidth), 0, 1);
        theme.Apply(ThemePreference.Light);
        Capture(root, "layer-selector-settings");

        void Press(Key key)
        {
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(layer), Environment.TickCount, key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            layer.RaiseEvent(args);
            if (args.Handled) return;
            args.RoutedEvent = Keyboard.KeyDownEvent;
            layer.RaiseEvent(args);
        }
        var popup = (Popup)layer.Template.FindName("PART_Popup", layer);
        var list = (Border)popup.Child;
        foreach (var mode in new[] { ThemePreference.Light, ThemePreference.Dark })
        {
            theme.Apply(mode);
            Press(Key.F4);
            settings.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            Assert.True(popup.IsOpen);
            Assert.Equal(layer.ActualWidth, list.ActualWidth);
            Assert.Equal(theme.Color("Surface.Card"), ((SolidColorBrush)list.Background).Color);
            var options = Descendants(list).OfType<ComboBoxItem>().ToArray();
            Assert.Equal(Enum.GetValues<WidgetLayerMode>().Length, options.Length);
            foreach (var option in options)
            {
                var check = (UIElement)option.Template.FindName("Check", option);
                Assert.Equal(option.IsSelected, check.Visibility == Visibility.Visible);
            }
            Capture(list, "layer-options-" + mode.ToString().ToLowerInvariant());
            Press(Key.Escape);
            settings.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.False(popup.IsOpen);
            Assert.True(settings.IsVisible);
        }
        Press(Key.Down);
        Assert.Equal(WidgetLayerMode.Normal, prefs.LayerMode);
        Press(Key.Escape);
        Assert.False(settings.IsVisible);
        theme.Apply(ThemePreference.Light);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? title);

    private static void Capture(FrameworkElement visual, string name, string variable = "QUOTA_UI_PREVIEW_DIR")
    {
        if (Environment.GetEnvironmentVariable(variable) is not { Length: > 0 } folder) return;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling((visual.ActualWidth + visual.Margin.Left + visual.Margin.Right) * 2),
            (int)Math.Ceiling((visual.ActualHeight + visual.Margin.Top + visual.Margin.Bottom) * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(folder);
        using var stream = File.Create(Path.Combine(folder, name + ".png"));
        png.Save(stream);
    }
}
