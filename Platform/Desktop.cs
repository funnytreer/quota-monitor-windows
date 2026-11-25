using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace QuotaMonitor;

internal static class Desktop
{
    private const int ExtendedStyle = -20;
    private const int ToolWindow = 0x00000080;
    private const int AppWindow = 0x00040000;
    private const int NoActivate = 0x08000000;
    private static IntPtr _iconHost, _shell;

    public static void ConfigureUtilityWindow(Window window, bool noActivate)
    {
        var handle = new WindowInteropHelper(window).Handle;
        int style = (GetWindowLong(handle, ExtendedStyle) | ToolWindow) & ~AppWindow;
        if (noActivate)
        {
            style |= NoActivate;
            HwndSource.FromHwnd(handle)?.AddHook(KeepWidgetInBackground);
        }
        SetWindowLong(handle, ExtendedStyle, style);
        const uint UpdateFrameWithoutMoving = 0x0037;
        SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, UpdateFrameWithoutMoving);
    }

    private static IntPtr KeepWidgetInBackground(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int MouseActivate = 0x0021;
        const int WindowPositionChanging = 0x0046;
        const uint NoZOrder = 0x0004;
        if (message == MouseActivate)
        {
            handled = true;
            return new IntPtr(3);
        }
        if (message == WindowPositionChanging && lParam != IntPtr.Zero &&
            HwndSource.FromHwnd(hwnd)?.RootVisual is WidgetWindow { LayerMode: WidgetLayerMode.DesktopPinned })
        {
            var position = Marshal.PtrToStructure<WindowPosition>(lParam);
            // Moving or resizing the widget must not promote it above other programs.
            if ((position.Flags & NoZOrder) == 0)
            {
                position.Flags |= NoZOrder;
                Marshal.StructureToPtr(position, lParam, false);
            }
        }
        return IntPtr.Zero;
    }

    public static void ShowWidget(WidgetWindow window, Preferences preferences)
    {
        window.Opacity = 0;
        window.Show();
        var screen = preferences.WidgetX is int x && preferences.WidgetY is int y && TryGetBounds(window, out var bounds)
            ? Forms.Screen.FromRectangle(new Drawing.Rectangle(x, y, bounds.Width, bounds.Height))
            : Forms.Screen.PrimaryScreen ?? Forms.Screen.FromPoint(Forms.Control.MousePosition);
        var area = screen.WorkingArea;
        if (Forms.Screen.FromHandle(new WindowInteropHelper(window).Handle).DeviceName != screen.DeviceName)
            Move(window, area, area.Location);
        var dpi = VisualTreeHelper.GetDpi(window);
        var position = new Drawing.Point(preferences.WidgetX ?? area.Left + DisplayMetrics.Pixels(14, dpi.DpiScaleX),
            preferences.WidgetY ?? area.Top + DisplayMetrics.Pixels(27, dpi.DpiScaleY));
        Move(window, area, position, WidgetWindow.WidgetGutter);
        SnapWidgetToEdges(window);
        ApplyWidgetLayer(window);
        window.Opacity = 1;
    }

    public static void ShowNearTray(TrayPopupWindow window)
    {
        window.TrayAnchor = Forms.Control.MousePosition;
        var screen = Forms.Screen.FromPoint(window.TrayAnchor);
        var area = screen.WorkingArea;
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (Forms.Screen.FromHandle(hwnd).DeviceName != screen.DeviceName)
            Move(window, area, area.Location);

        // Measure on the target monitor while the HWND is hidden, including the scroll height limit.
        var scale = HwndSource.FromHwnd(hwnd)!.CompositionTarget.TransformToDevice;
        window.MaxHeight = .75 * area.Height / scale.M22;
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(window.Width, window.MaxHeight));
        // Fix the dashboard viewport before showing it so expanding a section can only scroll its body.
        if (window.SizeToContent == SizeToContent.Manual) window.Height = content.DesiredSize.Height;
        var size = new Drawing.Size((int)Math.Ceiling(window.Width * scale.M11),
            (int)Math.Ceiling(content.DesiredSize.Height * scale.M22));
        SetBounds(window, TrayBounds(screen, window.TrayAnchor, size, scale.M11));
        if (!window.IsVisible) window.Show();
        window.UpdateLayout();
        PlaceNearTray(window);
        SetForegroundWindow(hwnd);
        if (!window.IsActive) window.Activate();
    }

    public static void PlaceNearTray(TrayPopupWindow window)
    {
        if (!window.IsVisible || !TryGetBounds(window, out var current)) return;
        var screen = Forms.Screen.FromPoint(window.TrayAnchor);
        window.MaxHeight = .75 * screen.WorkingArea.Height / VisualTreeHelper.GetDpi(window).DpiScaleY;
        var target = TrayBounds(screen, window.TrayAnchor, current.Size, VisualTreeHelper.GetDpi(window).DpiScaleX);
        if (target.Location != current.Location) Move(window, screen.WorkingArea, target.Location);
    }

    private static Drawing.Rectangle TrayBounds(Forms.Screen screen, Drawing.Point anchor, Drawing.Size size, double scale)
    {
        var area = screen.WorkingArea;
        int x = Math.Clamp(anchor.X - DisplayMetrics.Pixels(36, scale), area.Left, Math.Max(area.Left, area.Right - size.Width));
        bool aboveDesktop = area.Top > screen.Bounds.Top && anchor.Y < area.Top;
        return new Drawing.Rectangle(x, aboveDesktop ? area.Top : Math.Max(area.Top, area.Bottom - size.Height), size.Width, size.Height);
    }

    public static void ShowSettings(SettingsWindow window, WidgetWindow? widget)
    {
        Drawing.Rectangle widgetBounds = default;
        bool nearWidget = widget?.IsVisible == true && TryGetBounds(widget, out widgetBounds);
        var screen = nearWidget ? Forms.Screen.FromRectangle(widgetBounds) : Forms.Screen.FromPoint(Forms.Control.MousePosition);
        var area = screen.WorkingArea;
        window.Opacity = 0;
        window.Show();
        if (TryGetBounds(window, out var bounds))
        {
            var position = new Drawing.Point(area.Left + (area.Width - bounds.Width) / 2,
                area.Top + (area.Height - bounds.Height) / 2);
            if (nearWidget && area.Right - widgetBounds.Right >= bounds.Width - GlassWindow.Gutter)
            {
                position.X = widgetBounds.Right - GlassWindow.Gutter / 2;
                position.Y = widgetBounds.Top - 20;
            }
            Move(window, area, position);
        }
        window.Opacity = 1;
    }

    public static void SnapWidgetToEdges(WidgetWindow window)
    {
        if (!TryGetBounds(window, out var bounds)) return;
        var screen = Forms.Screen.FromRectangle(bounds);
        var dpi = VisualTreeHelper.GetDpi(window);
        var position = SnapPosition(bounds, screen.WorkingArea, dpi);
        if (position != bounds.Location) Move(window, screen.WorkingArea, position, WidgetWindow.WidgetGutter);
    }

    internal static Drawing.Point SnapPosition(Drawing.Rectangle bounds, Drawing.Rectangle area, DpiScale dpi)
    {
        area = WithShadowSpace(area, WidgetWindow.WidgetGutter, dpi);
        int right = Math.Max(area.Left, area.Right - bounds.Width);
        int bottom = Math.Max(area.Top, area.Bottom - bounds.Height);
        int x = Snap(bounds.Left, area.Left, right, (int)Math.Round(10 * dpi.DpiScaleX));
        int y = Snap(bounds.Top, area.Top, bottom, (int)Math.Round(10 * dpi.DpiScaleY));
        return new Drawing.Point(x, y);

        static int Snap(int position, int start, int end, int distance)
        {
            if (position <= start + distance) return start;
            if (position >= end - distance) return end;
            return position;
        }
    }

    private static Drawing.Rectangle WithShadowSpace(Drawing.Rectangle area, int gutter, DpiScale dpi)
    {
        area.Inflate((int)Math.Round(gutter * dpi.DpiScaleX), (int)Math.Round(gutter * dpi.DpiScaleY));
        return area;
    }

    private static void Move(Window window, Drawing.Rectangle area, Drawing.Point position, int gutter = 0)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (!TryGetBounds(window, out var bounds)) return;
        const uint NoResizeNoActivateNoZOrder = 0x0015;
        Place(bounds);
        // WPF can resize the HWND when it crosses onto a monitor with a different DPI.
        if (TryGetBounds(window, out var moved) && moved.Size != bounds.Size)
            Place(moved);

        void Place(Drawing.Rectangle rect)
        {
            var limits = WithShadowSpace(area, gutter, VisualTreeHelper.GetDpi(window));
            int x = Math.Clamp(position.X, limits.Left, Math.Max(limits.Left, limits.Right - rect.Width));
            int y = Math.Clamp(position.Y, limits.Top, Math.Max(limits.Top, limits.Bottom - rect.Height));
            SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, NoResizeNoActivateNoZOrder);
        }
    }

    public static bool TryGetBounds(Window window, out Drawing.Rectangle bounds)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero && GetWindowRect(handle, out var rectangle))
        {
            bounds = Drawing.Rectangle.FromLTRB(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
            return true;
        }
        bounds = default;
        return false;
    }

    public static void SetBounds(Window window, Drawing.Rectangle bounds)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        const uint NoActivateNoZOrder = 0x0014;
        SetWindowPos(handle, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height, NoActivateNoZOrder);
    }

    private static IntPtr DesktopIconHost(IntPtr shell)
    {
        if (shell == IntPtr.Zero || FindWindowEx(shell, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
            return shell;

        // Explorer can host the desktop icons in a separate WorkerW window.
        GetWindowThreadProcessId(shell, out uint shellProcess);
        var host = shell;
        var className = new StringBuilder(32);
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint process);
            if (process != shellProcess || !IsWindowVisible(window)) return true;
            GetClassName(window, className, className.Capacity);
            if (className.ToString() != "WorkerW" ||
                FindWindowEx(window, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero) return true;
            host = window;
            return false;
        }, IntPtr.Zero);
        return host;
    }

    public static void ApplyWidgetLayer(WidgetWindow window)
    {
        window.Topmost = window.LayerMode == WidgetLayerMode.AlwaysOnTop;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !window.IsVisible) return;
        const uint keepBoundsAndFocus = 0x0413;
        if (window.LayerMode == WidgetLayerMode.DesktopPinned)
        {
            SetWindowPos(hwnd, new IntPtr(-2), 0, 0, 0, 0, keepBoundsAndFocus);
            PlaceInDesktopLayer(window);
        }
        else SetWindowPos(hwnd, window.Topmost ? new IntPtr(-1) : IntPtr.Zero, 0, 0, 0, 0, keepBoundsAndFocus);
    }

    private static void PlaceInDesktopLayer(WidgetWindow window)
    {
        if (!window.IsVisible || window.LayerMode != WidgetLayerMode.DesktopPinned || window.IsMovingOrResizing) return;
        var widget = new WindowInteropHelper(window).Handle;
        if (widget == IntPtr.Zero) return;
        var shell = GetShellWindow();
        if (_shell != shell || !IsWindow(_iconHost))
        {
            _iconHost = DesktopIconHost(shell);
            _shell = shell;
        }
        var iconHost = _iconHost;
        const int PreviousWindow = 3;
        const int TopmostStyle = 0x0008;
        // Bypass the widget's normal guard against Z-order changes.
        const uint KeepSizePositionAndFocus = 0x0413;
        var aboveDesktop = iconHost == IntPtr.Zero ? new IntPtr(1) : GetWindow(iconHost, PreviousWindow);
        if (aboveDesktop == widget) return;
        if (aboveDesktop != IntPtr.Zero && (GetWindowLong(aboveDesktop, ExtendedStyle) & TopmostStyle) != 0)
            aboveDesktop = IntPtr.Zero;
        SetWindowPos(widget, aboveDesktop, 0, 0, 0, 0, KeepSizePositionAndFocus);
    }

    internal sealed class DesktopLayerMonitor : IDisposable
    {
        private readonly WidgetWindow _window;
        private readonly DispatcherTimer _timer = new(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        private readonly WinEventProc _foreground;
        private IntPtr _hook;
        private bool _disposed;

        public DesktopLayerMonitor(WidgetWindow window)
        {
            _window = window;
            _foreground = (_, _, _, _, _, _, _) => PlaceInDesktopLayer(_window);
            _timer.Tick += (_, _) => PlaceInDesktopLayer(_window);
            _window.IsVisibleChanged += OnVisibilityChanged;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            Watch();
        }

        private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => Watch();

        private void Watch()
        {
            _timer.IsEnabled = !_disposed && _window.IsVisible;
            if (_timer.IsEnabled && _hook == IntPtr.Zero)
                _hook = SetWinEventHook(3, 3, IntPtr.Zero, _foreground, 0, 0, 0); // EVENT_SYSTEM_FOREGROUND, out of context
            else if (!_timer.IsEnabled && _hook != IntPtr.Zero)
            {
                UnhookWinEvent(_hook);
                _hook = IntPtr.Zero;
            }
        }

        private void OnDisplayChanged(object? sender, EventArgs args) => _window.Dispatcher.InvokeAsync(() =>
        {
            if (_disposed) return;
            _iconHost = IntPtr.Zero;
            PlaceInDesktopLayer(_window);
        });

        public void Dispose()
        {
            _disposed = true;
            Watch();
            _window.IsVisibleChanged -= OnVisibilityChanged;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        }
    }

    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int obj, int child, uint thread, uint time);
    [DllImport("user32")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint process, uint thread, uint flags);
    [DllImport("user32")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32")] private static extern bool SetForegroundWindow(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangle { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition
    {
        public IntPtr Window, After;
        public int X, Y, Width, Height;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr handle, out Rectangle rectangle);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? title);

    private delegate bool EnumWindowProc(IntPtr window, IntPtr state);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowProc callback, IntPtr state);
}
