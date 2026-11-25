using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace QuotaMonitor;

internal sealed class GeminiWizardWindow : ConnectionGuide
{
    private readonly GeminiConnection _bridge;
    private readonly TextBlock _registration = Visuals.Text("", 11);
    private readonly TextBlock _status = Visuals.Text("", 12, bold: true);
    private readonly Button _disconnect;
    private bool _prepared;

    public GeminiWizardWindow(GeminiConnection bridge, bool enabled, Action<bool> apply)
        : base("连接 Gemini 网页版", Service.Gemini)
    {
        _bridge = bridge;
        _prepared = enabled && GeminiConnection.IsRegistered();
        var local = Section("1　准备本机连接", "将扩展目录复制到剪贴板，供 Chrome 加载。");
        local.Children.Add(_registration);
        var files = new WrapPanel();
        files.Children.Add(Command("注册并复制扩展目录", () =>
        {
            apply(true);
            _prepared = true;
            UpdateStatus();
            Copy(GeminiConnection.ExtensionPath, "已复制扩展目录。");
        }));
        files.Children.Add(Command("打开文件夹", () => Process.Start(new ProcessStartInfo(GeminiConnection.ExtensionPath) { UseShellExecute = true })));
        local.Children.Add(files);

        var chrome = Section("2　在 Chrome 加载并授权", "打开扩展管理页，开启「开发者模式」，选择「加载已解压的扩展程序」，粘贴目录。点击扩展图标，允许访问 gemini.google.com。");
        chrome.Children.Add(Command("复制 chrome://extensions", () => Copy("chrome://extensions", "已复制 Chrome 扩展管理页地址。")));
        var live = Section("3　检查连接", "成功读到用量后，可在设置中选择要在桌面显示的 Gemini 网页版额度。");
        _status.TextWrapping = TextWrapping.Wrap;
        live.Children.Add(_status);
        _disconnect = Command("断开本机连接", () =>
        {
            apply(false);
            _prepared = false;
            UpdateStatus();
        });
        live.Children.Add(_disconnect);
        bridge.StateChanged += OnStateChanged;
        Closed += (_, _) => bridge.StateChanged -= OnStateChanged;
        UpdateStatus();
        if (enabled) _disconnect.Visibility = Visibility.Visible;
    }

    private void OnStateChanged(GeminiBridgeState _) => UpdateStatus();

    private void UpdateStatus()
    {
        _registration.Text = _prepared ? "✓ 本机连接已注册" : "尚未准备";
        _registration.Foreground = _prepared ? Visuals.ThemeBrush("Status.Connected") : Visuals.Gray;
        _disconnect.Visibility = _prepared ? Visibility.Visible : Visibility.Collapsed;
        _status.Text = !_prepared ? "请先准备本机连接" : _bridge.State switch
        {
            GeminiBridgeState.Handshaking => "已握手，等待读取用量",
            GeminiBridgeState.NeedsPermission => "已握手，请在 Chrome 扩展图标中授权",
            GeminiBridgeState.NeedsSignIn => "已握手，请在 Chrome 登录 Gemini",
            GeminiBridgeState.Connected => "已连接，已读到用量",
            GeminiBridgeState.ReadFailed => "已连接，暂时读不到用量",
            _ => "等待 Chrome 连接"
        };
        _status.Foreground = _prepared && _bridge.State == GeminiBridgeState.Connected ? Visuals.ThemeBrush("Status.Connected") : Visuals.Ink;
    }
}
