# 额度监控器 · Windows

在 Windows 桌面和系统托盘查看 AI 服务的剩余额度与重置时间。桌面小组件提供进度条、圆环和迷你胶囊三种布局，托盘里的「额度监测中心」显示各服务的详细周期。

## 目前支持哪些服务？

当前版本支持 **ChatGPT / Codex、Grok 和 Antigravity** 三个额度来源。

| 额度来源 | 如何连接 | 可以查看 |
| --- | --- | --- |
| ChatGPT / Codex | 通过本机已登录的 Codex 客户端读取 | Codex 返回的 5 小时或每周剩余额度、重置时间，以及可用重置次数和到期日期 |
| Grok | 在监控器的连接窗口登录 grok.com | 账号用量页显示的共享周额度和重置时间 |
| Antigravity | 在本机安装并登录 `agy` CLI | 桌面显示 Gemini 和 Claude 模型组的每周额度。托盘可展开查看 `agy /usage` 返回的各模型组、5 小时或每周额度及重置时间 |

桌面上的 **Gemini** 和 **Claude** 都来自 Antigravity。独立的 Gemini 网页版、Claude 网页版订阅额度目前均未接入。具体可见的周期取决于账号和服务实际返回的数据。

## 使用

在 Windows 上安装 .NET 8 SDK 后，从项目根目录运行：

```powershell
dotnet run --project QuotaMonitor.csproj -c Release
```

单击托盘图标打开额度监测中心。右键托盘图标进入「设置」，可选择桌面显示项、配置低额度提醒和开机自启动。

## 本地数据

设置和额度历史保存在当前 Windows 用户的 `%LOCALAPPDATA%\QuotaMonitor` 目录中。Grok 登录会话由本机 WebView2 用户资料保存。连接各服务时使用自己的账号。

品牌图标来自 [Lobe Icons](https://github.com/lobehub/lobe-icons)，随项目保留了[图标许可](UI/Assets/Brands/LICENSE.txt)。
