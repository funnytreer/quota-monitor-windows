using System;
using System.Windows;
using System.Windows.Interop;
using Point = System.Drawing.Point;

namespace QuotaMonitor;

internal class TrayPopupWindow : GlassWindow
{
    internal Point TrayAnchor { get; set; }

    private PopupOutsideClick? _outsideClick;
    private long? _dismissedAt;
    private System.Drawing.Point _dismissedAtPointer;
    protected virtual bool KeepOpen => false;

    protected TrayPopupWindow(string title, int width) : base(title, width, gutter: 8)
    {
        Topmost = true;
        SetTextSurface(Plate.Background, true);
        Deactivated += (_, _) => DismissFromOutside();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _dismissedAt = null;
            WatchOutsideClicks();
        };
        Closed += (_, _) => _outsideClick?.Dispose();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(OnWindowMessage);
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x001C && wParam == IntPtr.Zero) DismissFromOutside(); // WM_ACTIVATEAPP
        return IntPtr.Zero;
    }

    internal void DismissFromOutside() => Dismiss(System.Windows.Forms.Control.MousePosition);

    private void Dismiss(System.Drawing.Point pointer)
    {
        if (KeepOpen || !IsVisible) return;
        // The shell can take focus on mouse-down, before NotifyIcon delivers its click on mouse-up.
        _dismissedAt = Environment.TickCount64;
        _dismissedAtPointer = pointer;
        Hide();
    }

    protected void WatchOutsideClicks()
    {
        if (IsVisible && !KeepOpen) _outsideClick ??= new PopupOutsideClick(this, Frame, Dismiss);
        else
        {
            _outsideClick?.Dispose();
            _outsideClick = null;
        }
    }

    internal bool WasDismissedByTrayClick()
    {
        var dismissed = _dismissedAt;
        _dismissedAt = null;
        var pointer = System.Windows.Forms.Control.MousePosition;
        return dismissed.HasValue && Environment.TickCount64 - dismissed.Value <= 250
            && Math.Abs(pointer.X - _dismissedAtPointer.X) <= 3 && Math.Abs(pointer.Y - _dismissedAtPointer.Y) <= 3;
    }
}
