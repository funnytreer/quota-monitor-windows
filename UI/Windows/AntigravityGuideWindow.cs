using System;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace QuotaMonitor;

internal sealed class AntigravityGuideWindow : ConnectionGuide
{
    private readonly Button _copy, _check;
    private readonly Func<Task> _refresh;
    private readonly TextBlock _installation = Visuals.Text("", 12, bold: true);
    private readonly TextBlock _account = Visuals.Text("", 12);

    public AntigravityGuideWindow(Func<Task> refresh) : base("连接 Antigravity", Service.Antigravity)
    {
        _refresh = refresh;
        var cli = Section("本机 Antigravity CLI", "在 PowerShell 粘贴登录命令，按 agy 的提示登录 Google 账号。");
        cli.Children.Add(_installation);
        _copy = Command("复制登录命令", () =>
            Copy(@"& ""$env:LOCALAPPDATA\agy\bin\agy.exe""", "已复制，可在 PowerShell 中运行。"));
        cli.Children.Add(_copy);
        var quota = Section("额度读取", "登录完成后重新检测，即可刷新 Gemini、Claude 等模型的用量。");
        _account.TextWrapping = System.Windows.TextWrapping.Wrap;
        quota.Children.Add(_account);
        _check = Command("立即重新检测", Recheck);
        quota.Children.Add(_check);
        UpdateInstallation();
    }

    private async void Recheck()
    {
        _check.IsEnabled = false;
        try { UpdateInstallation(); await _refresh(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { Feedback.Text = "无法读取 agy 的状态，请重试。"; }
        finally { _check.IsEnabled = true; UpdateInstallation(); }
    }

    private void UpdateInstallation()
    {
        bool installed = AntigravityQuotaSource.IsInstalled;
        _installation.Text = installed ? "✓ 已检测到 agy.exe" : "未找到 agy.exe，请先安装 Antigravity CLI。";
        _installation.Foreground = installed ? Visuals.ThemeBrush("Status.Connected") : Visuals.Gray;
        _copy.IsEnabled = installed;
    }

    public void Present(AntigravityReading reading)
    {
        _account.Text = reading.Status ?? (reading.Groups.Count > 0 ? $"已读到 {reading.Groups.Count} 个模型组的额度" : "等待读取");
        _account.Foreground = reading.Status == null && reading.Groups.Count > 0 ? Visuals.ThemeBrush("Status.Connected") : Visuals.Gray;
    }
}
