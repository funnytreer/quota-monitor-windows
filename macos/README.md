# 额度监控器 · macOS

在菜单栏查看 Codex、Grok、Gemini 和 Claude / GPT 的剩余额度、重置时间及最近更新时间。支持 macOS 14 及以上。

## 构建与运行

安装完整的 Xcode，在仓库根目录执行以下命令。

```sh
cd macos
./scripts/build-local.sh
open .build-app/local/Build/Products/Release/QuotaMonitor.app
```

构建脚本自动查找 `/Applications/Xcode.app`，也支持通过 `DEVELOPER_DIR` 指定 Xcode。运行单元测试使用 `swift test`。

## 连接额度来源

| 来源 | 如何连接 | 可查看的额度 |
| --- | --- | --- |
| Codex | 登录本机 Codex 客户端，在侧边栏选择「Codex → 连接 Codex」 | 客户端返回的额度窗口、重置时刻、可用重置次数和到期时间 |
| Grok | 在「Grok → 连接 Grok」中打开官方用量页并登录 | 共享周额度及重置时刻 |
| Antigravity | 安装并登录 `agy` CLI，在「Antigravity」中刷新 | Gemini 与 Claude / GPT 各自的周额度及 5 小时额度 |

后台默认每 15 分钟刷新。进入设置可选择刷新间隔、登录时自动启动、低额度通知，以及在周额度即将重置且剩余超过指定比例时提醒。主窗口打开时显示程序坞图标，关闭后继续在菜单栏监控。

额度快照、设置和 Grok 登录会话保存在当前 Mac 用户的本地存储中。每位用户通过自己的客户端或官方页面连接账号。

在 **设置 → 菜单栏指针** 选择跟随 Codex、Grok、Gemini、Claude / GPT 的周额度，或选择「总量」查看有效周额度的等权平均。暂无有效读数时，表盘中央显示短横线。

## 桌面小组件

小号和中号组件通过 App Group 读取主应用写入的额度。要使用额度共享，在 Xcode 里为 **QuotaMonitor** 和 **QuotaWidget** 选择开发团队，为两个 target 配置同一个已授权的 `QUOTA_APP_GROUP`，并使用该团队可签名的 bundle identifier。脚本默认生成临时签名的本地应用，可直接使用菜单栏监控；该构建无法向小组件共享额度。

服务图标来自 [Lobe Icons](https://github.com/lobehub/lobe-icons)，随项目分发 [MIT 许可](App/LobeIcons-LICENSE.txt)。
