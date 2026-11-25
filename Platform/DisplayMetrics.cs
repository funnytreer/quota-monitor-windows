using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;

namespace QuotaMonitor;

internal static class DisplayMetrics
{
    internal static int Pixels(double dip, double scale) => (int)Math.Round(dip * scale, MidpointRounding.AwayFromZero);
    internal static int ClampRefreshRate(int value) => Math.Clamp(value, 60, 240);

    internal static int RefreshRate(IntPtr hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, 2 /* nearest monitor */);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        var mode = new DisplayModeInfo { Size = (ushort)Marshal.SizeOf<DisplayModeInfo>() };
        return monitor != IntPtr.Zero && GetMonitorInfoW(monitor, ref info) &&
            EnumDisplaySettingsW(info.Device, -1, ref mode) ? ClampRefreshRate(mode.Frequency) : 60;
    }

    internal static void MatchDisplay(Timeline animation, FrameworkElement element)
    {
        var hwnd = (PresentationSource.FromVisual(element) as HwndSource)?.Handle ?? IntPtr.Zero;
        Timeline.SetDesiredFrameRate(animation, RefreshRate(hwnd));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public System.Drawing.Rectangle Bounds, WorkArea;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    // Fixed-width public DEVMODEW layout. Only the size and display-frequency fields are used.
    [StructLayout(LayoutKind.Explicit, Size = 220)]
    private struct DisplayModeInfo
    {
        [FieldOffset(68)] public ushort Size;
        [FieldOffset(184)] public int Frequency;
    }

    [DllImport("user32")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32", ExactSpelling = true)] private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool EnumDisplaySettingsW(string device, int mode, ref DisplayModeInfo settings);
}
