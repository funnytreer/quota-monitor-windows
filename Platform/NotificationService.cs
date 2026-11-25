using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using Windows.UI.Notifications;
using Forms = System.Windows.Forms;

namespace QuotaMonitor;

internal sealed class NotificationService(Forms.NotifyIcon tray, Dispatcher dispatcher, Action openDashboard)
{
    internal void Show(string title, string body, bool warning)
    {
        int fallbackSent = 0;
        void Balloon()
        {
            if (Interlocked.Exchange(ref fallbackSent, 1) != 0 || !tray.Visible || dispatcher.HasShutdownStarted) return;
            tray.ShowBalloonTip(5000, title, body, warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
        }
        Deliver(() =>
        {
            var notifier = GetCurrentProcessExplicitAppUserModelID(out var id) == 0 && !string.IsNullOrEmpty(id)
                ? ToastNotificationManager.CreateToastNotifier(id) : ToastNotificationManager.CreateToastNotifier();
            if (notifier.Setting != NotificationSetting.Enabled) return;
            var xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
            var fields = xml.GetElementsByTagName("text");
            fields[0].AppendChild(xml.CreateTextNode(title));
            fields[1].AppendChild(xml.CreateTextNode(body));
            var toast = new ToastNotification(xml) { Group = "QuotaMonitor" };
            toast.Activated += (_, _) => dispatcher.InvokeAsync(openDashboard);
            toast.Failed += (_, _) => dispatcher.InvokeAsync(() => Deliver(Balloon, () => { }));
            notifier.Show(toast);
        }, Balloon);
    }

    internal static void Deliver(Action primary, Action fallback)
    {
        try { primary(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceWarning("Windows notification unavailable ({0}).", ex.GetType().Name);
            try { fallback(); }
            catch (Exception error) when (error is not OutOfMemoryException)
            { Trace.TraceWarning("Tray notification unavailable ({0}).", error.GetType().Name); }
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] out string? id);
}
