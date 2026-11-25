using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace QuotaMonitor;

internal static class NativeChrome
{
    internal static void Attach(Window window)
    {
        WindowChrome.SetWindowChrome(window, new WindowChrome { CaptionHeight = 0, GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0), ResizeBorderThickness = new Thickness(6), UseAeroCaptionButtons = false });
        window.SourceInitialized += (_, _) => ApplyTheme(window);
    }

    internal static void ApplyTheme(Window window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || WindowChrome.GetWindowChrome(window) == null) return;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int round = 2, dark = ((SolidColorBrush)window.FindResource("Surface.Window")).Color.R < 128 ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
    }

    [DllImport("dwmapi")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
