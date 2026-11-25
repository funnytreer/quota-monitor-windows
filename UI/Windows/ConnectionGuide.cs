using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace QuotaMonitor;

internal class ConnectionGuide : GlassWindow
{
    private readonly StackPanel _body = new();
    protected readonly TextBlock Feedback = Visuals.Text("", 11, Visuals.Gray);

    protected ConnectionGuide(string title, Service service) : base(title, 356, gutter: 8, native: true)
    {
        SetTextSurface(Plate.Background, true);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        MaxHeight = SystemParameters.WorkArea.Height * .9;
        var layout = new DockPanel { Margin = new Thickness(13, 10, 13, 10) };
        Frame.Child = layout;
        var header = Visuals.Columns(24, -1, 0);
        Visuals.Place(header, ServiceMark.Create(service, Visuals.Ink, 17), 0);
        Visuals.Place(header, Visuals.Text(title, 15, bold: true), 1);
        Visuals.Place(header, Visuals.Button(Visuals.Symbol("\uE711", 11), "关闭", 26, 26, Close), 2);
        header.MouseLeftButtonDown += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); };
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);
        Feedback.TextWrapping = TextWrapping.Wrap;
        DockPanel.SetDock(Feedback, Dock.Bottom);
        layout.Children.Add(Feedback);
        layout.Children.Add(new ScrollViewer { Style = (Style)FindResource("PanelScroll"), Content = _body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    protected StackPanel Section(string title, string description)
    {
        var panel = new StackPanel();
        panel.Children.Add(Visuals.Text(title, 13, bold: true));
        var text = Visuals.Text(description, 11.5, Visuals.Gray);
        text.Margin = new Thickness(0, 4, 0, 3);
        text.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(text);
        var card = Visuals.Card(panel);
        card.Margin = new Thickness(0, 6, 0, 0);
        _body.Children.Add(card);
        return panel;
    }

    protected Button Command(string label, Action run)
    {
        var button = Visuals.ActionButton(label, () =>
        {
            try { Feedback.Text = ""; run(); }
            catch (ExternalException) { Feedback.Text = "Windows 暂时无法完成操作，请重试。"; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { Feedback.Text = "无法访问本机连接文件或设置，请检查后重试。"; }
        });
        button.HorizontalAlignment = HorizontalAlignment.Left;
        return button;
    }

    protected void Copy(string text, string confirmation)
    {
        Clipboard.SetText(text);
        Feedback.Text = confirmation;
    }
}
