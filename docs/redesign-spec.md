# 《额度监控器（QuotaMonitor）》界面与功能重设计规格说明书

> **文档用途**：本规格书作为《额度监控器（QuotaMonitor）》重设计改造的唯一权威设计与工程契约，供后续开发代理（如 ChatGPT Astra 6.2）按阶段落地执行。所有色值、尺寸、数据契约与底层护栏均已锁定，执行时不得自行删改核心约束。

---

## 1. 文档目标与范围（Scope & Non-Goals）

### 1.1 重设计目标
> 2026-11-26 更新：桌面版监测 ChatGPT / Codex、Antigravity 和 Grok。Gemini 与 Claude 的额度均来自 Antigravity。文中涉及独立 Gemini 网页版的扩展、向导、后台采集和配置项仅供旧版兼容参考。
对现有基于 `.NET 8 + WPF` 的《额度监控器（QuotaMonitor）》进行界面、交互、信息架构与核心数据/告警能力的全面升级，打造具备专属桌面遥测仪表气质的 AI 额度监控工具：
1. **统一服务与入口**：将分散在托盘右键菜单、详情行内和无入口状态的 4 大平台连接与监控管理收拢到「托盘控制中心」与「统一配置中心」。
2. **修复关键数据与渲染缺陷**：补回 ChatGPT/Codex 被丢弃的 5 小时额度、消除 Antigravity 与 Gemini 网页版的命名混淆、将 Antigravity 纳入提醒系统、彻底修复桌面小组件 `Transparency > 0` 骤变成黑底的视觉断层。
3. **升级视觉与交互体验**：落地「精密微光仪表 · Precision Telemetry」设计语言，支持「跟随系统 / 浅色 / 深色」主题热切换、3 种小组件形态、悬停微操作条、右键菜单、完整倒计时与消耗速率洞察。

### 1.2 当前数据源
1. **ChatGPT (Codex)**：本地 `codex.exe` stdio JSON-RPC（`5h` + `7d` 双周期 + `rateLimitResetCredits` 重置券）。
2. **Antigravity (`agy` CLI)**：本地 `agy.exe -p /usage` JSON 输出（多模型组 `5h` + `weekly` 周期）。
3. **Grok (WebView2)**：独立 WebView2 Profile 后台/交互读取 `grok.com/?_s=usage`（共享周额度）。

> 架构上须通过统一的 `QuotaBucket` 模型保持扩展性，但**本轮不新增第 5 个平台**。

### 1.3 明确非目标（Non-Goals）
- 不引入云端账号系统、跨设备云同步或远程遥测上传。
- 不重写已验证稳定的底层采集协议（Codex RPC、Grok DOM 抓取、Gemini NamedPipe、agy CLI 调用）。
- 不在本轮制作 MSIX/NSIS 安装包或自动更新器。

---

## 2. 底层护栏与兼容性约束（Hard Guardrails — 严禁破坏）

> **核心原则**：以下文件中涉及的系统互操作、协议握手、注册表键、Profile 路径与防误触机制经过实机验证，**只允许增量适配，严禁破坏性重写**。

### 2.1 启动与单实例 (`App.xaml.cs`)
- `OnStartup` 最顶部**必须**保留 `GeminiConnection.IsHostLaunch(e.Args)` 判断：检测到 `chrome-extension://` 启动参数时，直接执行 `await GeminiConnection.RunHostAsync(e.Args[0])` 并调用 `Shutdown()` 返回，绝不创建托盘图标、不触发单实例激活锁。
- 保留 `EventWaitHandle` 命名事件 `Local\QuotaMonitor.Activate`，二次启动时唤醒已有小组件并退出。

### 2.2 Codex 数据源 (`CodexResets.cs` / `CodexAccountSource`)
- 保留 `OpenAI.Codex_2p2nqsd0c76g0` MSIX 包路径定位（`GetPackagesByPackageFamily` / `GetPackagePathByFullName`）与 `PATH` 环境变量回退逻辑。
- 保留 `codex app-server --listen stdio://` 握手协议（`initialize` → `initialized` → `account/rateLimits/read`）与 `codex login` 登录流。
- 保留 `RequiresLogin` 错误关键字匹配及 `rateLimitResetCredits` 解析逻辑。
- **允许且必须做的增量改动**：扩展 `ParseWorkQuota`（或新增 `ParseWorkQuotas`），同时提取 `primary`（通常为 `5h` / `300m`）与 `secondary`（通常为 `7d` / `10080m`）两个额度窗口并映射为独立 `QuotaBucket`，不再丢弃其中一个。

### 2.3 Antigravity 数据源 (`AntigravityQuotaSource.cs`)
- 保留 `%LOCALAPPDATA%\agy\bin\agy.exe` 路径与启动参数：`--settings {"disableSlashCommands":false,"useG1Credits":false} --print-timeout 12s --output-format json -p /usage`，以及 20 秒超时整树清理。
- **允许且必须做的增量改动**：保留现有 `groups` / `buckets` JSON 解析，将所有模型组下的 `5h` 与 `weekly` 桶完整映射为统一 `QuotaBucket`（区分摘要桶 `ag.gemini.7d`、`ag.claude.7d` 与详情展开桶），以便全量接入小组件、详情面板与提醒引擎。

### 2.4 Grok 数据源 (`GrokWebClient.cs` & `GrokUsagePage.cs`)
- 保留独立 WebView2 用户数据目录 `%LOCALAPPDATA%\QuotaMonitor\GrokWebView`。
- 保留 `GrokUsagePage.PageScript`、`GrokUsagePage.Parse`、安全导航白名单（`HealthyGrokPage`、`IsSignInPage`、`CanNavigate`）及无界面 `HwndSource` 后台读取方式。
- `GrokConnectionWindow` 仅做视觉壳体与主题适配，不可破坏弹窗登录（`NewWindowRequested`）与 `ClearBrowsingDataAsync` 断开清理逻辑。

### 2.5 Gemini 网页版扩展与桥接 (`GeminiConnection.cs` & `GeminiExtension/`)
- **绝对禁止修改 `GeminiExtension/manifest.json` 中的 `key` 字段**（该公钥决定扩展 ID `ogojeakbiilhhagkmchjdeabgebhdbek`；改动会导致 Native Messaging 握手彻底失效）。
- 保留 `Host = "com.quotamonitor.gemini"`、`Origin = "chrome-extension://ogojeakbiilhhagkmchjdeabgebhdbek"`、注册表键 `HKCU\Software\Google\Chrome\NativeMessagingHosts\com.quotamonitor.gemini`、4 字节小端长度前缀 NamedPipe 帧协议。
- 仅允许向 `GeminiConnection` 增量暴露只读连接状态/事件（如 `IsConnected` / `ConnectionStateChanged`），供新连接向导实时显示握手状态。

### 2.6 桌面层、DPI 定位与自启 (`Desktop.cs` & `StartupRegistration.cs`)
- 保留 `SHELLDLL_DefView` / `WorkerW` 桌面图标层探测与 `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`、`WM_MOUSEACTIVATE` / `WM_WINDOWPOSCHANGING` 护栏，以及多 DPI 下的 `GetWindowRect` / `SetWindowPos` / 边缘吸附逻辑。
- 仅允许在此基础上增量扩展 `WidgetLayerMode`（贴桌面底层 / 普通窗口 / 始终置顶）切换策略，切勿在非 `DesktopPinned` 模式下强行覆盖 Z-order。
- 保留 `StartupRegistration` 对 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\QuotaMonitor` 的读写与 260 字符校验。

### 2.7 配置文件向后兼容 (`settings.json`)
- 必须无损读取现有 `%LOCALAPPDATA%\QuotaMonitor\settings.json`（`AppearanceVersion <= 4`），并平滑迁移至 `AppearanceVersion = 5`。
- 迁移后必须完整保留用户的：`WidgetX`/`WidgetY`/`WidgetWidth`/`WidgetHeight`、`WidgetVisible`、`GrokConnected`、`GeminiEnabled`、`GeminiHasReadUsage`、`HiddenWidgetItems`、旧 `Background`/`Transparency`、`LowEnabled`/`LowThreshold`、`FastEnabled`/`FastThreshold`。

---

## 3. 专属视觉设计系统（「精密微光仪表 · Precision Telemetry」）

### 3.1 主题模式与系统热切换
支持三种主题模式枚举 `ThemePreference`：
- `System`（**默认**）：跟随 Windows 系统深浅色模式。
- `Light`：手动锁定浅色（「霜白瓷面 · Frost Light」）。
- `Dark`：手动锁定深色（「曜石暗幕 · Obsidian Dark」）。

**Windows 深浅色检测与热切换规范**：
- 读取注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize`：
  - 窗口与弹窗主题读取 `AppsUseLightTheme`（`0` = Dark，非 `0` 或缺失 = Light）。
  - 任务栏托盘图标墨色继续读取 `SystemUsesLightTheme`（`0` = 白色图标，非 `0` = 深色图标），并在高对比度模式下回退系统前景色。
- 监听 `Microsoft.Win32.SystemEvents.UserPreferenceChanged`，当 `ThemePreference == System` 时，无需重启即可热更新全部已打开窗口（`WidgetWindow`、`DashboardPopover`、`SettingsWindow`、向导窗）与托盘图标。

### 3.2 精确色值 Token 表

| Token 名称 | Light（霜白瓷面） | Dark（曜石暗幕） | 用途说明 |
| :--- | :--- | :--- | :--- |
| `Surface.Window` | `#F7F9FC` | `#141820` | 设置窗、详情控制中心主背景 |
| `Surface.Card` | `#FFFFFF` | `#1C222D` | 平台卡片、分组容器背景 |
| `Surface.Subtle` | `#EBF0F7` | `#262E3D` | 按钮次级底、徽标底、折叠区底 |
| `Surface.Hover` | `#E1E8F3` | `#2E384B` | 悬停态底色 |
| `Border.Hairline` | `#D8E1EE` | `#2F3A4C` | 卡片描边、分割线、输入框边框 |
| `Text.Primary` | `#141923` | `#F1F5FB` | 标题、主数值、服务名 |
| `Text.Secondary` | `#5B6575` | `#A1AEC2` | 副文本、周期标签、描述文字 |
| `Text.Muted` | `#838D9D` | `#748299` | 次要时间戳、未连接提示 |
| `Accent.Primary` | `#0958D9` | `#4C9BFF` | 主操作按钮、选中态边框/高亮 |
| `Accent.PrimarySoft` | `#E6F0FF` | `#1D304D` | 选中按钮底、Switch 辅助底 |
| `Widget.PlateDefault` | `#F4F8FE` | `#171C26` | 小组件 `Themed` 模式基准底色 |
| `Widget.BorderDefault` | `#CCFFFFFF` | `#1FFFFFFF` | 小组件外框 1px 高光描边 |
| `Widget.ShadowColor` | `#1E3554` (18%) | `#000000` (42%) | 小组件外层投影 |

### 3.3 进度条健康度语义色（已锁定）
所有进度条与百分比数值均依据剩余比例 `Remaining` 与阈值实时着色：

| 状态档位 | 判定条件 | Light 填充 / 字色 / 轨道 | Dark 填充 / 字色 / 轨道 |
| :--- | :--- | :--- | :--- |
| **充裕（Normal）** | `Remaining >= LowThreshold` | 渐变 `#1677FF → #0958D9` / `#0958D9` / `#DCE7F7` | 渐变 `#4C9BFF → #2F81F7` / `#6CB2FF` / `#2A3649` |
| **偏低（Low）** | `CriticalThreshold < Remaining < LowThreshold` | `#D97706` / `#B45309` / `#FDE8CD` | `#F59E0B` / `#FBBF24` / `#3D311E` |
| **危急（Critical）** | `Remaining <= CriticalThreshold`（默认 `10%`） | `#DC2626` / `#B91C1C` / `#FADBD8` | `#EF4444` / `#F87171` / `#402226` |
| **无读数 / 未连接** | `Remaining == null` | — / `#838D9D` / `#E5EAF2` | — / `#748299` / `#262E3D` |

### 3.4 小组件材质与平滑透明度算法（v2 纯净无贴片规范）
> **严禁规范**：**禁止**使用任何 `ReadingBackdrop` 局部灰白贴片块（包括行文字底板、圆环内实心底、`检查于 HH:mm` 底块、胶囊单格底块），**禁止**在 `Transparency > 0` 时对底色 RGB 做乘法暗化突变。

定义 `WidgetSurfaceStyle` 枚举与 `WidgetTextContrast`（`Auto` / `Light` / `Dark`）：
1. **`Themed`（跟随主题，默认）**：底色基准取当前有效主题的 `Widget.PlateDefault`（浅色 `#F5F8FD` / 深色 `#10151F`）。
2. **`HudGlass`（无界通透浮标）**：弱化底板存在感（最大底板不透明度压缩至 `0.28 * (1 - Transparency/100)`），保留 `1px` 细腻玻璃描边。
3. **`CustomColor`（自定义底色）**：使用用户选择的 HEX 颜色（`Preferences.CustomBackground`），按 WCAG 相对亮度公式 `L = 0.2126 * R + 0.7152 * G + 0.0722 * B < 128` 判断浅/深前景。

**平滑 Alpha 与高透明文字护底**：
```text
plateAlpha = (100 - Clamp(Transparency, 0, 100)) / 100.0
```
- **高透明文字可读性（替代贴片）**：新增 `WidgetTextContrast`（`自动` / `亮白` / `深墨`）。`自动` 模式下，当 `Transparency >= 45` 或 `HudGlass` 时默认优先使用高对比亮白文字（`#F8FAFC`）+ 轻柔文字阴影（`DropShadowEffect: BlurRadius=5, ShadowDepth=1, Opacity=0.62`）；当选用亮白文字体系时，进度条同步切换为高亮度通透遥测色（Normal `#38BDF8 → #3B82F6`、Low `#FBBF24`、Critical `#FB7185`）与通透轨道（`#2EFFFFFF`）。
- 外框阴影与玻璃描边随 `plateAlpha` 平滑线性衰减，并始终保留 `#01000000` 透明命中层确保 100% 透明时仍可拖拽。

### 3.5 排版、圆角与间距规范
- **字体栈**：主界面 `Segoe UI Variable Text, Microsoft YaHei UI`；等宽数值/时间/诊断 `Cascadia Mono, Consolas`；图标 `Segoe Fluent Icons, Segoe MDL2 Assets`（优先检测 Fluent Icons，回退 MDL2）。
- **等宽数字对齐**：所有百分比、倒计时、时间、版本号均须开启 `Typography.NumeralAlignment="Tabular"`，禁止刷新时横向跳动。
- **字号层级**：
  - 窗口标题：`14px SemiBold`
  - 大百分比（详情主卡 / 标准进度条）：`16–18px SemiBold`
  - 服务名 / 分组标题：`12.5–13px SemiBold`
  - 正文 / 次级条目：`12px Regular`
  - 倒计时 / 状态说明 / 辅助说明：`10.5–11px Regular`
- **圆角与栅格**：小组件 / 主窗口 `12px`（迷你胶囊模式 `18px`）；卡片 `10px`；按钮/输入框/标签 `6px`；基础间距采用 `4px` 栅格（常用 `8 / 12 / 16`）。

---

### 3.6 可视参考资产（2× 高清定稿视觉稿与交互原型）
实施 UI 重构时，除严格遵循第 3.2–3.5 节的精确色值 Token 与排版尺寸外，请同时参照以下由原型直接 2× 无损渲染的视觉资产对齐布局比例、视觉层级与交互细节：
- **桌面小组件三形态（65% 高透明无贴片 + 零悬停按钮）+ 任务栏图标**：[`docs/mockups/desktop-widgets-v2.png`](mockups/desktop-widgets-v2.png)
- **统一配置中心（Frost Light / Obsidian Dark Fluent 2 双主题对比）**：[`docs/mockups/settings-control-center-v2.png`](mockups/settings-control-center-v2.png)
- **340px 托盘控制中心（Frost Light / Obsidian Dark 双主题对比）**：[`docs/mockups/dashboard-popover-v2.png`](mockups/dashboard-popover-v2.png)
- **自定义 Fluent 2 托盘右键菜单与锐化动态托盘图标**：[`docs/mockups/tray-menu-v2.png`](mockups/tray-menu-v2.png)
- **高保真可交互 HTML 原型**：[`docs/prototype.html`](prototype.html)（可在浏览器直接打开体验完整交互与实时参数联动）

---

## 4. 统一数据模型与 `Preferences` v5 契约

### 4.1 规范化 `ServiceKind` 与 `BucketKey` 命名约定
彻底解决旧版中 `Antigravity` 与 `Gemini 网页版` 都叫 `Gemini` 的歧义：

| `BucketKey` | 所属 `ServiceKind` | 桌面短名（`ShortName`） | 完整显示名（`DisplayName`） | 窗口时长 | 说明 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| `chatgpt.5h` | `ChatGPT` | `ChatGPT 5h` | `ChatGPT · 5 小时额度` | `300` | Codex `primary` 桶（**新增补回**） |
| `chatgpt.7d` | `ChatGPT` | `ChatGPT 周` | `ChatGPT · 每周额度` | `10080` | Codex `secondary` 桶 |
| `ag.gemini.7d` | `Antigravity` | `Gemini` | `Antigravity · Gemini 周` | `10080` | 对应 `gemini-weekly` / `Gemini Models`（桌面不加 `AG·` 前缀，由图标区分） |
| `ag.claude.7d` | `Antigravity` | `Claude` | `Antigravity · Claude 周` | `10080` | 对应 `3p-weekly` / `Claude and GPT models`（桌面不加 `AG·` 前缀） |
| `ag.<group>.<win>` | `Antigravity` | `AG·<Group>` | `Antigravity · <Group> (<win>)` | `300`/`10080` | 详情页展开显示全部模型组与周期 |
| `grok.7d` | `Grok` | `Grok 周` | `Grok · 共享周额度` | `10080` | Grok 网页版共享周额度 |
| `gemini.5h` | `GeminiWeb` | `Gemini 网页 5h` | `Gemini 网页版 · 5 小时额度` | `300` | Chrome 扩展 `Current` |
| `gemini.7d` | `GeminiWeb` | `Gemini 网页 周` | `Gemini 网页版 · 每周额度` | `10080` | Chrome 扩展 `Weekly` |

> 默认桌面小组件显示集合（首次安装）：`["chatgpt.7d", "grok.7d", "ag.gemini.7d", "ag.claude.7d"]`；用户可在设置中自由勾选 `chatgpt.5h`、`gemini.5h`、`gemini.7d` 等并调整排序。

### 4.2 核心数据实体定义（`Core/QuotaModels.cs`）
```csharp
internal enum ServiceKind { ChatGPT, Antigravity, Grok, GeminiWeb }

internal enum ServiceConnectionState
{
    NotConfigured,   // 未配置/未连接（如 Grok 未连、Gemini 未启用）
    Connecting,      // 正在连接/等待握手
    Connected,       // 正常已连接并读到有效数据
    LoginRequired,   // 需要登录或授权
    Unavailable      // 临时不可用/超时/未找到 CLI（保留上次成功读数）
}

internal sealed record QuotaBucket(
    string BucketKey,
    ServiceKind Service,
    string ShortName,
    string DisplayName,
    string WindowLabel,          // e.g. "5 小时" / "每周" / "共享周"
    int? Remaining,              // 0..100, null 表示暂无读数
    DateTimeOffset? ResetsAt,
    int? WindowMinutes,
    string? ResetText = null,    // 当源仅提供文本重置时间（如 Gemini 网页）时使用
    string? Status = null,
    DateTimeOffset? LastSuccessAt = null,
    bool IsWidgetEligible = true // false 表示仅在详情展开中展示（如 AG 细分子周期）
);

internal sealed record ServiceSnapshot(
    ServiceKind Service,
    ServiceConnectionState State,
    string StatusText,
    DateTimeOffset? LastSuccessAt,
    IReadOnlyList<QuotaBucket> Buckets,
    CodexResetReading? CodexResets = null,
    IReadOnlyList<AntigravityGroup>? AntigravityGroups = null
);
```

### 4.3 `Preferences` v5 契约与 v4 → v5 无损迁移

**新增/更新枚举与字段**：
- `DisplayMode`：`Bars`、`Rings`、`CompactPill`
- `ThemePreference`：`System`（默认）、`Light`、`Dark`
- `WidgetSurfaceStyle`：`Themed`（默认）、`HudGlass`、`CustomColor`
- `WidgetLayerMode`：`DesktopPinned`（默认，贴桌面图标层）、`Normal`（普通桌面窗口）、`AlwaysOnTop`（始终置顶）

**`Preferences` 字段完整清单**：
- `AppearanceVersion`：当前版本常量 `5`
- `ThemeMode`：`ThemePreference`（默认 `System`）
- `SurfaceStyle`：`WidgetSurfaceStyle`（默认 `Themed`）
- `CustomBackground`：`string`（默认 `"#DFEAFC"`，校验 `#RRGGBB`）
- `Transparency`：`int`（`0..100`，默认 `0`）
- `Mode`：`DisplayMode`（默认 `Bars`）
- `ShowCountdownOnWidget`：`bool`（默认 `true`，控制条形模式是否附带紧凑倒计时）
- `LayerMode`：`WidgetLayerMode`（默认 `DesktopPinned`）
- `WidgetLocked`：`bool`（默认 `false`）
- `WidgetVisible`：`bool`（默认 `true`）
- `WidgetX` / `WidgetY`：`int?`
- `WidgetWidth` / `WidgetHeight`：`int`（默认 `228 × 116`，最小 `174 × 40`，最大 `1000 × 800`）
- `WidgetScale`：`double`（仅允许 `0.9`、`1.0`、`1.15`、`1.3`，默认 `1.0`，用于 4K 大屏无损缩放小组件尺寸与字号）
- `WidgetBucketOrder`：`List<string>`（默认 `["chatgpt.7d", "chatgpt.5h", "ag.gemini.7d", "ag.claude.7d", "grok.7d", "gemini.5h", "gemini.7d"]`）
- `HiddenBuckets`：`HashSet<string>`（默认隐藏 `["chatgpt.5h", "gemini.7d"]` 或仅按旧设置迁移）
- `PollingIntervalMinutes`：`int`（允许值 `1, 3, 5, 10, 15`，默认 `5`）
- `LowEnabled`：`bool`（默认 `true`），`LowThreshold`：`int`（`1..100`，默认 `20`），`CriticalThreshold`：`int`（`1..50`，默认 `10`）
- `PerBucketLowThresholds`：`Dictionary<string, int>`（可选单桶覆盖）
- `FastEnabled`：`bool`（默认 `true`），`FastThreshold`：`int`（`1..100`，默认 `15`）
- `ResetRecoveryEnabled`：`bool`（默认 `true`，周期重置恢复提醒）
- `QuietHoursEnabled`：`bool`（默认 `false`），`QuietStart`：`string`（`"23:00"`），`QuietEnd`：`string`（`"08:00"`）
- `GrokConnected`、`GeminiEnabled`、`GeminiHasReadUsage`：完整保留。

**v4 → v5 自动迁移规则（`Preferences.Load`）**：
1. 若反序列化时 `AppearanceVersion < 5`：
   - 将旧 `Background` 复制到 `CustomBackground`；若旧 `Background` 不是默认值 `#DFEAFC`，将 `SurfaceStyle` 设为 `WidgetSurfaceStyle.CustomColor`，否则设为 `Themed`（或若旧 `Transparency > 0` 可保持平滑透明的 `Themed`）。
   - 将旧 `HiddenWidgetItems` 逐项映射为 `HiddenBuckets`：
     - `WidgetItem.Work` → `chatgpt.7d`
     - `WidgetItem.Grok` → `grok.7d`
     - `WidgetItem.AntigravityGemini` → `ag.gemini.7d`
     - `WidgetItem.AntigravityClaude` → `ag.claude.7d`
     - `WidgetItem.GeminiWeb` → `gemini.5h`
   - 默认把新增的 `chatgpt.5h` 与 `gemini.7d` 加入 `HiddenBuckets`（避免老用户升级后小组件突然暴增行数，用户可随时在设置里勾选开启）。
   - `ThemeMode` 设为 `System`，`LayerMode` 设为 `DesktopPinned`。

---

## 5. 四大界面组件级规格

### 5.1 桌面小组件（`WidgetWindow`）

**三种布局形态（纯净无贴片）**：
1. **精简仪表条（`Bars`）**：
   - 彻底移除旧版 `_readings.MaxHeight = 92` 强制压扁限制；每行保持紧凑均匀高度（约 `30px`），底部不留多余空白，`检查于 HH:mm` 仅作透明轻量文本居右下展示。
   - 每行包含：`[14px 服务图标]` + `[短名]` + `[可选 紧凑倒计时 如 1d 12h]` + `[等宽百分比]` + `[低/危急警示图标]` + `[4px 圆角进度条]`。
2. **自适应圆环网格（`Rings`）**：
   - 1–3 项：单排 `UniformGrid`（1×N）；4 项：`2×2`；5–6+ 项：`3×N`。
   - 圆环内部**完全镂空透明（`Fill="Transparent"`，已取消圆环实心底色）**，描边宽度 `4.5px`，圆环正中**只显示等宽百分比**，服务名标签固定在圆环**正下方**（不与圆环描边碰撞）。
3. **极简迷你胶囊（`CompactPill`）**：
   - 高度约 `34–36px` 的单层统一磨砂胶囊（`CornerRadius = 18`），内部无任何分块底色，各项之间用 `1px × 12px` 20% 透明度竖线分隔；Antigravity 子项在图标旁显示微标 `G` / `C` 以区分 Gemini 与 Claude。
   - 该模式下**隐藏右下角缩放斜杠（`_resizeMark`）**，保持胶囊边缘纯净。

**零悬停工具栏原则与右键菜单**：
- **严禁任何悬停按钮浮层**：鼠标移入或停留在桌面小组件上时，**不得弹出任何刷新、控制中心或设置按钮**；所有设置与控制中心入口统一从**系统托盘图标**进入（右键托盘菜单或左键托盘图标）。
- **主题化右键菜单（`ContextMenu`）**：保留轻量右键快捷操作（刷新额度、打开控制中心、小组件设置、切换显示方式/窗口层级、锁定位置与尺寸、恢复默认尺寸、隐藏小组件）。

### 5.2 托盘控制中心（`DashboardPopover`，替代旧 `DetailsWindow`）
从 `188px` 窄浮窗升级为 **`340px` 宽度的现代化控制中心**（最大高度为当前屏幕工作区高度的 `75%`）：

```text
┌──────────────────────────────────────────────┐
│  额度控制中心             [📌] [↻] [⚙]      │ ← 固定顶栏
├──────────────────────────────────────────────┤
│  ╭─ ◉ ChatGPT / Codex ────────────── 已连接 ─╮ │
│  │  5 小时额度    ████████████░░░     78%   │ │
│  │  2h 14m 后重置 (16:46)   ↓ 8%/15m        │ │
│  │  每周额度      █████████░░░░░░     61%   │ │
│  │  3d 18h 后重置 (11/29 09:00)             │ │
│  │  ──────────────────────────────────────  │ │
│  │  可用重置券 [ 2 次 ]  到期: 12/01 · 12/15 │ │
│  ╰──────────────────────────────────────────╯ │
│  ╭─ ✦ Antigravity ───────────── [展开全部 ▾]╮ │
│  │  Gemini 周     ███████░░░░░░░░     54%   │ │
│  │  Claude 周     ██░░░░░░░░░░░░░     14% ⚠ │ │
│  ╰──────────────────────────────────────────╯ │
│  ╭─ ⦸ Grok ───────────────────────── 未连接 ─╮ │
│  │  未连接 Grok 官网用量页       [立即连接 →]│ │
│  ╰──────────────────────────────────────────╯ │
├──────────────────────────────────────────────┤
│  已检查 14:32 · 下次刷新 4m       [显示/隐藏] │ ← 固定底栏
└──────────────────────────────────────────────┘
```

**关键交互细节**：
- **固定图钉（`📌`）与点击外部自动收起**：默认未固定时，点击控制中心以外的任意位置（其他应用窗口、桌面空白处、任务栏、本应用小组件）立即自动收起；点击图钉固定后保持可见，直到再次点击图钉、按 `Esc` 或点击关闭。
- **Windows 可靠失焦护栏（严禁失灵）**：
  1. **弹出即设为前景**：`Desktop.ShowNearTray` 展示 `DashboardPopover` 或 `TrayMenuWindow` 时，必须调用 Win32 `SetForegroundWindow(hwnd)` 配合 `Activate()`，确保从 WinForms `NotifyIcon` 回调打开时真正获得前景焦点，否则点击桌面或任务栏不会触发 `Deactivated`；
  2. **双重失焦兜底 + 跨窗联动**：同时监听 `Deactivated` 与 `WM_ACTIVATEAPP (wParam == 0)`；由于 `WidgetWindow` 带 `WS_EX_NOACTIVATE` 不抢焦点，点击桌面小组件时需显式联动关闭未固定的 `DashboardPopover` 与 `TrayMenuWindow`；
  3. **250ms 托盘防抖**：`Deactivated` 收起时记录 `LastDismissedAt`（`Environment.TickCount64`）；若在 `250ms` 内收到同一托盘图标点击事件，视为用户意图关闭，禁止闪回重开。
- **相对倒计时 + 绝对时间双显**：由 `CountdownFormatter` 统一生成，如 `< 1h` 显示 `42m 后重置 (15:14)`，`1–24h` 显示 `2h 14m 后重置 (16:46)`，`> 24h` 显示 `3d 5h 后重置 (11/28 19:00)`。
- **消耗速率微标**：查询 `UsageHistoryStore` 近 15 分钟最大降幅，若 `drop >= 3` 则在对应行显示琥珀/灰色微标 `↓ {drop}%/15m`。
- **内联可操作按钮**：
  - ChatGPT `LoginRequired` → 内联 `登录 Codex` 按钮；
  - Grok `NotConfigured` / `LoginRequired` → 内联 `连接 Grok` / `重新登录` 按钮；
  - Antigravity `Unavailable`（未找到 CLI 或未登录）→ 内联 `登录指引` 按钮。
- **精细化视觉细节**：顶栏/底栏与滚动卡片区之间加 `1px Border.Hairline` 分割线；顶栏工具按钮（固定/刷新/设置）统一为 `28×28` 柔和圆角底按钮；`↓ X%/15m` 采用微胶囊底标；`展开全部模型周期` 采用柔和胶囊按钮；底栏右侧为带眼睛图标的紧凑显隐按钮；托盘面板和右键菜单首次显示即位于最终位置，使用最终不透明度。

### 5.3 统一配置中心（`SettingsWindow` · Windows 11 Fluent 2）
重构为 **`640 × 500`**（最小 `580 × 450`）无边框自定义标题栏窗口，采用 **Fluent 2 双色分栏架构**：左侧导航栏（`168px`，浅色 `#F1F4F9` / 深色 `#11151C`）+ `1px` 竖直分割线 + 右侧内容画布（浅色 `#F8FAFC` / 深色 `#161B24`）。
- **导航栏选中态**：无蓝色外框线，采用柔和底色填充 + 左边缘 `3px × 16px` 圆角强调竖条。
- **真·一体式分段控制器（Segmented Control）**：所有多选一控件均采用凹槽圆角底轨（`#E9EEF5` / `#111620`，`Padding=3`）+ 单一凸起选中滑块（`#FFFFFF` / `#273142`），严禁使用多个带蓝框的独立按钮。
- **卡片节奏**：`16px` 内边距、内部行高约 `44px`、行间以 `1px` `Border.Hairline` 分隔；深色模式下透明度滑块未填充轨道使用清晰可见的 `#334155`。

1. **页签一：外观与桌面**
   - **单框沉浸式壁纸预览区**：单层 `Height=152, CornerRadius=12` 预览舞台（无双层套娃边框、无单调灰渐变），内置「切换深/浅壁纸」胶囊按钮，实时居中展示当前配置的小组件效果。
   - **主题模式**：分段控制 `跟随系统` / `浅色` / `深色`。
   - **显示方式**：分段控制 `进度条` / `圆环网格` / `迷你胶囊`；`进度条` 下附开关 `显示重置倒计时`（已移除 `圆环填充` 选项，圆环始终镂空透明）。
   - **小组件尺寸**：分段控制 `紧凑 90%` / `标准 100%` / `舒适 115%` / `大号 130%`（对应 `Preferences.WidgetScale`，4K 显示器推荐 `115%`）。
   - **背景材质**：分段控制 `主题默认` / `无界通透` / `自定义颜色`；仅在 `自定义颜色` 时展开色卡（浅灰/浅蓝/浅绿/淡紫 + 调色器 `+`）。
   - **高透明文字**：分段控制 `自动` / `亮白` / `深墨`；下方为连续平滑的 `背景透明度` 滑块（`0%–100%`）。
   - **桌面行为**：`窗口层级` 下拉（贴桌面底层 / 普通窗口 / 始终置顶）、`锁定位置与尺寸` 开关、`恢复默认尺寸与位置` 按钮。

2. **页签二：服务与显示项**
   - **服务连接管理区**：ChatGPT / Codex、Antigravity 和 Grok 各有一张连接卡片，显示连接状态、上次成功时间和对应操作。
   - **桌面显示项与排序区**：ChatGPT 每周与 5 小时额度、Antigravity 的 Gemini 与 Claude 每周额度、Grok 共享周额度。每项可单独勾选并上下排序。

3. **页签三：提醒与通知**
   - **低额度提醒**：总开关 + 默认阈值（`1–100%`，默认 `20%`）+ 危急高亮阈值（`1–50%`，默认 `10%`）；下方可展开「按项目单独设置阈值」。
   - **消耗过快提醒**：开关 + `15 分钟内下降 ≥ N 个百分点`。
   - **额度重置恢复提醒（新增）**：开关（当曾触发过低额度/耗尽的项目在周期重置后恢复充裕时发出通知）。
   - **免打扰与测试**：免打扰时段开关（起止时间如 `23:00 – 08:00`）+ `发送测试通知` 按钮。

4. **页签四：常规与诊断**
   - **常规**：`开机自启动` 开关、`自动刷新频率`（1 / 3 / 5 / 10 / 15 分钟）。
   - **诊断与维护**：等宽字体展示脱敏运行摘要（版本、主题状态、4 大数据源状态与 CLI/扩展注册状态、配置文件路径），提供 `复制诊断信息`、`打开配置目录` 按钮。

### 5.4 连接向导统一化
- **`GrokConnectionWindow`**：适配 Light/Dark 主题边框与按钮；顶部显示安全域锁标 + `grok.com` 地址 + 一行式操作指引；底部清晰区分 `断开并清除登录` 与 `读取并开始监测`。
- **`GeminiWizardWindow`（替代旧 `GeminiConnectionWindow`）**：升级为 **3 步可视向导**：
  - Step 1：**准备本机连接** — 点击「注册本机桥并复制扩展目录」，状态变绿，附「打开文件夹」快捷按钮。
  - Step 2：**在 Chrome 加载并授权** — 清晰图文步骤 + 一键复制 `chrome://extensions` 按钮，提示点击扩展图标允许访问 `gemini.google.com`。
  - Step 3：**实时连接状态** — 订阅 `GeminiConnection` 状态事件，实时显示「等待 Chrome 连接 / 已握手，等待授权 / 已连接并读到用量」。
- **`AntigravityGuideWindow`（新增轻量引导弹窗）**：自动检测 `%LOCALAPPDATA%\agy\bin\agy.exe` 是否存在；存在时提供一键复制登录命令（或打开终端运行 `agy` 登录提示）与「立即重新检测」按钮。

### 5.5 系统托盘右键菜单（`TrayMenuWindow`，替代 WinForms `ContextMenuStrip`）
彻底淘汰系统原生 WinForms `ContextMenuStrip` 灰白菜单，替换为自定义无边框 WPF 菜单窗口 **`TrayMenuWindow`**（继承 `GlassWindow`，`Width = 228`、`CornerRadius = 12`、`Padding = 6`、`Topmost = True`、`ShowInTaskbar = False`），完整接入 `ThemeManager` 浅/深主题热切换。

**精简菜单结构（`36px` 行高、`7px` 圆角悬停高亮、`16px` 单色图标、`12.5px` 文本）**：
1. `额度控制中心`（`\uE8A5`，SemiBold）
2. `立即刷新额度`（`\uE72C`）
3. `显示桌面小组件`（`\uE890`，带勾选态 `✓`）
4. `锁定小组件位置`（`\uE72E`，带勾选态 `✓`）
5. `设置中心…`（`\uE713`）
6. `1px Border.Hairline` 分割线（`Margin = 8,4`）
7. `退出`（`\uE7E8`，平时保持常规墨色，仅在悬停或键盘聚焦时切换柔和危险红 `#DC2626` / `#F87171` + 淡红底 `#FEE2E2` / `#3B1E22`）

**交互与定位规范**：
- **左/右键互斥分工**：左键点击托盘图标切换 `DashboardPopover`（若菜单打开则先关闭菜单）；右键点击托盘图标弹出 `TrayMenuWindow`（若控制中心打开则先关闭控制中心）。
- **键盘与失焦（点击外部自动收起）**：支持 `↑ / ↓` 方向键循环切换焦点、`Enter / Space` 触发、`Esc` 关闭；点击菜单外任意区域（含桌面、任务栏、其他窗口及本应用 `WidgetWindow`）立即自动收起（同样遵循 `SetForegroundWindow` 获焦、`WM_ACTIVATEAPP` 兜底与 `250ms` 托盘防抖）。
- **多屏多 DPI 安全定位**：复用 `Desktop.PlaceNearTray` 算法锚定在鼠标/托盘附近且严格钳位在当前显示器工作区内，确保永不截断。

---

## 6. 核心功能升级规范

### 6.1 轻量化消耗历史（`Core/UsageHistoryStore.cs`）
- 内存按 `BucketKey` 维护最近 `24 小时` 的 `(DateTimeOffset Timestamp, int Remaining)` 采样队列；每次有有效读数且变化或超过 5 分钟时记录，节流异步持久化到 `%LOCALAPPDATA%\QuotaMonitor\history.json`（单文件体积通常 < 20 KB）。
- 提供 `GetRecentDrop(string bucketKey, TimeSpan window, DateTimeOffset now)`，供控制中心展示近 15 分钟下降速率。

### 6.2 全桶提醒引擎（`Core/AlertEngine.cs`）
- 追踪字典键从旧版 `Service` 升级为 `string BucketKey`，天然覆盖 `ChatGPT (5h/7d)`、`Antigravity (全桶)`、`Grok (7d)`、`GeminiWeb (5h/7d)`。
- 提醒类型：
  1. **低额度提醒（`Low`）**：当 `Remaining < EffectiveLowThreshold(bucketKey)` 且上次非低额度时触发（使用独立阈值覆盖优先）。
  2. **消耗过快提醒（`FastDrop`）**：15 分钟滑动窗口内峰值下降 `>= FastThreshold` 时触发，同一桶 15 分钟冷却一次。
  3. **重置恢复提醒（`ResetRecovered`，新增）**：当某桶上一轮记录曾处于低额度状态（`WasLow == true` 或 `LastRemaining <= LowThreshold`），且本次观测到周期重置（`ResetsAt` 向后推进或 `ResetText` 变化）且 `Remaining >= EffectiveLowThreshold(bucketKey)` 时触发。
- **免打扰逻辑**：启用 `QuietHoursEnabled` 时（支持跨午夜如 `23:00–08:00`），仅静默弹窗通知，内部状态照常更新，避免免打扰结束后补发陈旧告警。

### 6.3 通知渠道（`Platform/NotificationService.cs`）
- 优先尝试发送带分组与明确标题/正文的 Windows 通知；考虑到非打包 Win32 `.NET` 应用在未注册 AUMID 时原生 Toast API 可能抛异常，**必须实现 `try/catch` 安全回退至 `NotifyIcon.ShowBalloonTip`**，保证在任何 Windows 10/11 环境下 100% 稳定不崩溃。

### 6.4 动态托盘图标与摘要（`Platform/TrayIconRenderer.cs`）
- **锐化 270° 开口仪表盘设计（替代旧版 7 点闭环）**：在 `32×32` 逻辑坐标系下使用 `Graphics.DrawArc` 绘制 **270° 开口圆弧**（`StartAngle = 135°`、`SweepAngle = 270°`，线宽 `2.8f`、`LineCap.Round`）；仅在 `size >= 24`（高 DPI 放大）时在 `-135° / 0° / +135°` 绘制 3 个极简主刻度，`16–20px` 小尺寸下保持纯净无杂点。
- **动态指针**：遍历所有已启用且有读数的 `QuotaBucket`，取最低 `Remaining` 值（无读数时取默认中间位），将尖细指针（配 `2.2px` 中心轴点）从 `-135°`（0%）线性旋转到 `+135°`（100%）。
- **镂空高亮告警角标**：当任意可见/已启用桶处于偏低或危急状态时，先使用 `CompositingMode.SourceCopy` + `Color.Transparent` 在右下角挖出 `10.5px` 透明隔离环，再绘制 `8px` 高对比度实心状态圆点（偏低琥珀 `#F59E0B`、危急红 `#EF4444`），避免与表盘弧线糊成一团。
- **Tooltip 摘要**：格式化为多服务精简摘要（如 `ChatGPT 74% · Gemini 81% · Grok 92%`），严格截断在 Windows `NotifyIcon.Text` 上限 **63 个字符**以内，防止抛出 `ArgumentOutOfRangeException`。

---

## 7. 推荐目录与模块结构

保持 `QuotaMonitor.csproj` 单应用工程轻量编译，同时在项目根目录新增 `QuotaMonitor.sln` 与 `QuotaMonitor.Tests`（xUnit 单元测试工程）：

```text
额度监控器/
├── QuotaMonitor.sln
├── QuotaMonitor.csproj              (保留单 EXE 输出、WebView2 引用与 GeminiExtension 复制)
├── app.manifest / QuotaMonitor.ico
├── docs/
│   ├── redesign-spec.md             (本规格书)
│   └── redesign-plan.md             (分阶段执行计划与验收清单)
├── GeminiExtension/                 (严禁修改 manifest.json 中的 key)
├── Core/
│   ├── QuotaModels.cs               (ServiceKind, QuotaBucket, ServiceSnapshot)
│   ├── Preferences.cs               (v5 配置与 v4->v5 迁移)
│   ├── UsageHistoryStore.cs         (24h 轻量化采样与降幅计算)
│   ├── AlertEngine.cs               (全桶低额度/快消耗/重置恢复/免打扰)
│   └── CountdownFormatter.cs        (相对倒计时与绝对时间格式化)
├── Sources/
│   ├── CodexAccountSource.cs        (保留 RPC，扩展输出 5h + 7d 双桶)
│   ├── AntigravityQuotaSource.cs    (保留 agy CLI，映射全模型组桶)
│   ├── GrokWebClient.cs & GrokUsagePage.cs
│   └── GeminiConnection.cs          (增量暴露连接状态事件)
├── Platform/
│   ├── Desktop.cs                   (保留底层定位与 DPI，增量支持 LayerMode)
│   ├── ThemeManager.cs              (System/Light/Dark + 热切换 + 亮度计算)
│   ├── TrayIconRenderer.cs          (动态指针与告警角标)
│   ├── NotificationService.cs       (Toast + BalloonTip 安全降级)
│   └── StartupRegistration.cs
├── UI/
│   ├── Themes/
│   │   ├── ThemeLight.xaml
│   │   ├── ThemeDark.xaml
│   │   └── SharedStyles.xaml
│   ├── Controls/
│   │   ├── ServiceMark.cs           (矢量品牌徽标)
│   │   ├── QuotaProgressBar.cs      (健康度三色渐变进度条)
│   │   ├── QuotaRingGauge.cs        (自适应圆环仪表)
│   │   └── CodexResetPanel.cs       (紧凑重置券卡片)
│   └── Windows/
│       ├── GlassWindow.cs
│       ├── WidgetWindow.cs
│       ├── DashboardPopover.cs
│       ├── SettingsWindow.cs
│       ├── GrokConnectionWindow.cs
│       ├── GeminiWizardWindow.cs
│       └── AntigravityGuideWindow.cs
└── QuotaMonitor.Tests/
    ├── QuotaMonitor.Tests.csproj    (xUnit, net8.0-windows)
    └── ...                          (解析、迁移、告警、透明算法、倒计时测试)
```

---

## 8. 4K HiDPI 与 240Hz 高刷新率专项优化规范

针对 4K 高分辨率（150%–200% 系统缩放，常伴随多屏混合 DPI）与 240Hz 高刷新率主力显示器，必须执行以下 6 项硬性优化：

### 8.1 窗口合成架构与 240Hz 零掉帧拖拽
- **设置窗与向导弹窗脱离软分层渲染**：`SettingsWindow`、`GrokConnectionWindow`、`GeminiWizardWindow`、`AntigravityGuideWindow` 等非半透明常规窗口**不得使用 `AllowsTransparency="True"`**（软件分层窗口在大尺寸 4K 区域下拖动会出现明显帧率下降与发粘）。改为原生 WPF **`WindowChrome`**（`CaptionHeight=0`、`GlassFrameThickness=0`、`ResizeBorderThickness=6`）+ Windows 11 DWM 原生圆角（`DwmSetWindowAttribute(..., DWMWA_WINDOW_CORNER_PREFERENCE = 2 /* DWMWCP_ROUND */)`）+ 原生硬件加速 DWM 阴影 + 随主题切换的 `DWMWA_USE_IMMERSIVE_DARK_MODE`，确保在 4K 240Hz 下拖动、缩放、滚动 100% 跑满硬件刷新率。
- **半透明小组件与浮层性能锁定**：仍需 `AllowsTransparency="True"` 的 `WidgetWindow`、`DashboardPopover`、`TrayMenuWindow`：
  - 所有 `DropShadowEffect` 强制设置 `RenderingBias = RenderingBias.Performance`；
  - 所有只读 `SolidColorBrush`、`LinearGradientBrush`、`StreamGeometry` 创建后立即调用 `.Freeze()` 消除跨帧依赖跟踪；
  - `_clock.Tick` 与刷新增量刷新必须做脏检查（仅当读数或显示文本实际变化时更新 UI，禁止每分钟/每秒整树 `Children.Clear()` 重建）。

### 8.2 解除 WPF 60 FPS 锁帧，动态匹配 240Hz 刷新率
- WPF 默认将 `Timeline.DesiredFrameRate` 锁定在 `60`。新增 `Platform/DisplayMetrics.cs`：
  - 通过 Win32 `MonitorFromWindow` + `GetMonitorInfoW` + `EnumDisplaySettingsW(ENUM_CURRENT_SETTINGS)` 读取窗口当前所在显示器的真实 `dmDisplayFrequency`（钳位至 `60..240`）。
  - 对 `QuotaProgressBar` 的进度变化动画显式调用 `Timeline.SetDesiredFrameRate(animation, refreshRate)`，保证在 240Hz 屏幕上丝滑无撕裂阶梯感。

### 8.3 4K 多 DPI 锐利渲染（100% / 125% / 150% / 175% / 200%）
- **工程级 DPI 声明**：在 `QuotaMonitor.csproj` 中补充 `<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>`（与 `app.manifest` 的 `PerMonitorV2` 协同），确保内嵌 WinForms 组件（`ColorDialog`、`NotifyIcon`）在 4K 屏与副屏间不模糊。
- **统一文本渲染模式**：消除 `GlassWindow` 构造函数 `TextFormattingMode.Display` 与 `SetTextSurface` 中 `Ideal` 的冲突。当窗口 `DpiScaleX >= 1.25`（包括 4K 150% / 175% / 200%）时统一使用 **`TextFormattingMode.Ideal`** + `UseLayoutRounding="True"` + `SnapsToDevicePixels="True"`，避免 4K 屏下中英文混排字距挤压及 `1px` 细线在 150%/175% 缩放下变粗发虚。
- **圆环零二次缩放**：`QuotaRingGauge` 取消外层 `Viewbox` 位图式二次缩放，直接按目标 DIP 直径计算并 `.Freeze()` 精确的 `StreamGeometry` 圆弧，保证 `4.5px` 描边在 4K 屏上绝对锐利。
- **托盘图标 4× 超采样**：`TrayIconRenderer` 在高 DPI 任务栏（`24px / 32px / 48px`）下先在 **`size * 4` 离屏画布**绘制 270° 开口圆弧、指针与镂空角标，再使用 `InterpolationMode.HighQualityBicubic` 下采样到目标尺寸，彻底消除锯齿。

### 8.4 4K 大屏字号下限与无损显示缩放
- 淘汰 `9–10 DIP` 过小字号：桌面小组件副文本（倒计时/检查时间）下限 **`11 DIP`**，服务名 **`12.5 DIP`**，百分比 **`14–15 DIP`**。
- 支持 `Preferences.WidgetScale`（`0.9 / 1.0 / 1.15 / 1.3`），通过按比例重算内边距、行高与字体大小（非整窗位图拉伸）实现 4K 大屏清晰放大。

### 8.5 跨 DPI 坐标换算与 `OnDpiChanged`
- 修正 `Platform/Desktop.cs` 中硬编码的物理像素常量：默认边距 `14`、`27` 与托盘水平锚点偏移 `36` 必须乘以当前显示器 `DpiScaleX` / `DpiScaleY`（4K 200% 下对应 `28` / `54` / `72` 物理像素），避免窗口与托盘对齐偏移。
- 在 `GlassWindow`（及 `WidgetWindow`）重写 `OnDpiChanged`，在 4K 主屏与 1080p 副屏间拖移或用户更改系统缩放比例时，即时更新 `TextFormattingMode`、最小/最大尺寸约束并重新贴合边界。

### 8.6 零微卡顿后台守护（重构 `DesktopLayerMonitor`）
- 废弃每 `250ms` 在 UI 线程调用 `EnumWindows` 遍历系统全部顶层窗口的高频轮询（在 240Hz 桌面及全屏游戏场景下易引发周期性微卡顿）。
- 改为：**缓存 `DesktopIconHost` 句柄**（仅在 `IsWindow` 失效或 `DisplaySettingsChanged` 时重查）+ **事件驱动**（`SetWinEventHook` 监听 `EVENT_SYSTEM_FOREGROUND` 仅在切窗时复核 Z-order）+ 拖拽/缩放期间暂停的 **低频自愈兜底（≥ `2000ms`）**。
