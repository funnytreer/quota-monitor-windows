using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace QuotaMonitor;

internal readonly record struct TraySummary(int? Minimum, QuotaHealth Badge, string Tooltip)
{
    public float Angle => (Minimum ?? 50) * 2.7f - 135f;
}

internal static class TrayIconRenderer
{
    internal static TraySummary Summarize(IReadOnlyList<ServiceSnapshot> services, Preferences p)
    {
        int? minimum = null;
        var badge = QuotaHealth.Unknown;
        var parts = new List<string>();
        foreach (var service in services.Where(s => s.State == ServiceConnectionState.Connected))
        {
            QuotaBucket? lowest = null;
            foreach (var b in service.Buckets)
            {
                if (b.Status is { Length: > 0 } || b.Remaining is not int n || n < 0 || n > 100) continue;
                if (lowest == null || n < lowest.Remaining) lowest = b;
                if (!minimum.HasValue || n < minimum) minimum = n;
                var health = ThemeManager.Health(n, p.PerBucketLowThresholds.GetValueOrDefault(b.BucketKey, p.LowThreshold), p.CriticalThreshold);
                if (health > badge) badge = health;
            }
            if (lowest != null) parts.Add($"{lowest.ShortName} {lowest.Remaining}%");
        }
        string text = parts.Count == 0 ? "额度监控器 · 暂无可用读数" : string.Join(" · ", parts);
        if (text.Length > 63)
        {
            int end = StringInfo.ParseCombiningCharacters(text).Last(i => i <= 62);
            text = text[..end].TrimEnd() + "…";
        }
        return new(minimum, badge, text);
    }

    internal static Icon Create(TraySummary summary)
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        int size = Forms.SystemInformation.SmallIconSize.Width;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393) && taskbar != IntPtr.Zero)
        {
            uint dpi = GetDpiForWindow(taskbar);
            if (dpi > 0) size = GetSystemMetricsForDpi(49 /* SM_CXSMICON */, dpi);
        }
        var ink = Forms.SystemInformation.HighContrast ? SystemColors.WindowText :
            ThemeManager.UsesLightTheme("SystemUsesLightTheme") ? Color.FromArgb(32, 32, 32) : Color.White;
        return Draw(summary, size > 0 ? size : 16, ink);
    }

    internal static Icon Draw(TraySummary summary, int size, Color ink)
    {
        using var canvas = new Bitmap(size * 4, size * 4);
        using (var g = Graphics.FromImage(canvas))
        using (var pen = new Pen(ink, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        using (var brush = new SolidBrush(ink))
        using (var needle = new GraphicsPath())
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.ScaleTransform(canvas.Width / 32f, canvas.Height / 32f);
            g.DrawArc(pen, 3, 3, 26, 26, 135, 270);
            for (int a = -90; size >= 24 && a <= 90; a += 90)
            {
                double rad = a * Math.PI / 180;
                g.FillEllipse(brush, 14.8f + 8 * (float)Math.Sin(rad), 14.8f - 8 * (float)Math.Cos(rad), 2.4f, 2.4f);
            }
            needle.AddBezier(15, 7, 15, 5.7f, 17, 5.7f, 17, 7);
            needle.AddLine(17, 7, 18.2f, 16);
            needle.AddBezier(18.2f, 16, 18.2f, 19.2f, 13.8f, 19.2f, 13.8f, 16);
            needle.CloseFigure();
            using var rotation = new Matrix();
            rotation.RotateAt(summary.Angle, new PointF(16, 16));
            needle.Transform(rotation);
            g.FillPath(brush, needle);
            if (summary.Badge is QuotaHealth.Low or QuotaHealth.Critical)
            {
                using var dot = new SolidBrush(summary.Badge == QuotaHealth.Low ? Color.FromArgb(245,158,11) : Color.FromArgb(239,68,68));
                using var hole = new SolidBrush(Color.Transparent);
                g.CompositingMode = CompositingMode.SourceCopy;
                g.FillEllipse(hole, 20.75f, 20.75f, 10.5f, 10.5f);
                g.CompositingMode = CompositingMode.SourceOver;
                g.FillEllipse(dot, 22, 22, 8, 8);
            }
        }
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(canvas, new Rectangle(0, 0, size, size), 0, 0, canvas.Width, canvas.Height, GraphicsUnit.Pixel);
        }
        var handle = bitmap.GetHicon();
        try { using var original = Icon.FromHandle(handle); return (Icon)original.Clone(); }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? name);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
