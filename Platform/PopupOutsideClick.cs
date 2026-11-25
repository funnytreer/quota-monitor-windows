using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Point = System.Drawing.Point;

namespace QuotaMonitor;

// Clicking the desktop or taskbar need not change the foreground window.
internal sealed class PopupOutsideClick : IDisposable
{
    private readonly IntPtr _window;
    private readonly Border _panel;
    private readonly Dispatcher _dispatcher;
    private readonly Action<Point> _dismiss;
    private readonly MouseProc _callback;
    private IntPtr _hook;
    private bool _pending;

    internal PopupOutsideClick(Window window, Border panel, Action<Point> dismiss)
    {
        _window = new WindowInteropHelper(window).Handle;
        _panel = panel;
        _dispatcher = window.Dispatcher;
        _dismiss = dismiss;
        _callback = OnMouse;
        const int whMouseLowLevel = 14;
        _hook = SetWindowsHookEx(whMouseLowLevel, _callback, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Trace.TraceWarning("监测中心外部点击监听未启用，错误码 {0}", Marshal.GetLastWin32Error());
    }

    private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
    {
        // Left, right, middle or X button down. Leave the event with its original target.
        if (code >= 0 && !_pending && message.ToInt64() is 0x201 or 0x204 or 0x207 or 0x20B)
        {
            var point = Marshal.PtrToStructure<Point>(data);
            HandleButtonDown(point, WindowFromPoint(point));
        }
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    internal void HandleButtonDown(Point point, IntPtr target)
    {
        const uint root = 2;
        if (GetAncestor(target, root) == _window && IsOnPanel(point)) return;
        _pending = true;
        _dispatcher.InvokeAsync(() => { if (_hook != IntPtr.Zero) _dismiss(point); }, DispatcherPriority.Input);
    }

    private bool IsOnPanel(Point point)
    {
        // The native window also contains a shadow and transparent rounded corners.
        var local = _panel.PointFromScreen(new System.Windows.Point(point.X, point.Y));
        var bounds = new Rect(_panel.RenderSize);
        if (!bounds.Contains(local)) return false;
        var radius = _panel.CornerRadius.TopLeft;
        return new RectangleGeometry(bounds, radius, radius).FillContains(local);
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private delegate IntPtr MouseProc(int code, IntPtr message, IntPtr data);
    [DllImport("user32", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int id, MouseProc callback, IntPtr module, uint thread);
    [DllImport("user32")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? module);
}
