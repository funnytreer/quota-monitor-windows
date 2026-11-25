# 《额度监控器（QuotaMonitor）》分阶段改造执行计划与派发指南

> **执行约定**：本计划配合 [`docs/redesign-spec.md`](redesign-spec.md) 使用，面向开发代理（如 ChatGPT Astra 6.2）设计。
> **强制纪律**：必须严格按照 **M1 → M2 → M3** 顺序分阶段实施。**每完成一个里程碑，必须停止继续向下开发，先执行构建与测试并输出验收报告**，由用户确认后再进入下一阶段。

---

## 一、总体执行原则

1. **先读护栏再动手**：修改任何代码前，必须通读 `docs/redesign-spec.md` 第 2 节「底层护栏与兼容性约束」。
2. **严禁破坏已验证协议**：
   - 不得改动 `GeminiExtension/manifest.json` 中的 `key` 字段。
   - 不得改动 `App.xaml.cs` 中 `GeminiConnection.IsHostLaunch(e.Args)` 的前置拦截逻辑。
   - 不得改动 `CodexAccountSource` 的 `stdio://` JSON-RPC 协议与 `AntigravityQuotaSource` 的 `agy.exe` 启动参数。
   - 不得改动 `GrokWebClient` / `GrokUsagePage` 的 DOM 抓取脚本与域名白名单。
3. **测试先行/同步**：每个里程碑新增或重构的核心非 UI 逻辑，必须同步补齐 `QuotaMonitor.Tests` 中的 xUnit 单元测试并全部通过。

---

## 二、Milestone 1（M1）：数据底座、主题引擎、透明修复与测试工程

### 2.1 目标
搭好全新架构地基，在不推翻现有窗口运行的前提下，完成统一多周期数据模型、v4→v5 配置迁移、主题热切换引擎、透明算法修复与自动化测试工程。

### 2.2 涉及文件清单
- **新增解决方案与测试工程**：
  - `QuotaMonitor.sln`
  - `QuotaMonitor.Tests/QuotaMonitor.Tests.csproj`（`net8.0-windows`，引用 `xunit`、`Microsoft.NET.Test.Sdk`、`xunit.runner.visualstudio`）
  - 在 `QuotaMonitor.csproj` 中添加 `<InternalsVisibleTo Include="QuotaMonitor.Tests" />`（或通过 `AssemblyInfo` / `<ItemGroup><InternalsVisibleTo ... /></ItemGroup>`）。
- **Core 层新建/整理**：
  - `Core/QuotaModels.cs`（`ServiceKind`、`ServiceConnectionState`、`QuotaBucket`、`ServiceSnapshot`）
  - `Core/Preferences.cs`（v5 字段定义与 `AppearanceVersion <= 4` → `5` 无损迁移）
  - `Core/CountdownFormatter.cs`（相对倒计时 + 绝对时间双格式化）
  - `Core/UsageHistoryStore.cs`（24h 滚动采样、15m 降幅查询、节流落盘 `history.json`）
- **Sources 层增量适配**：
  - `Sources/CodexAccountSource.cs`（原 `CodexResets.cs`）：扩展输出 `chatgpt.5h` + `chatgpt.7d` 双桶，保留原 `CodexResetReading` 解析。
  - `Sources/AntigravityQuotaSource.cs`：输出 `ag.gemini.7d`、`ag.claude.7d` 及全部模型组明细桶 `ag.<group>.<win>`。
- **Platform & UI 主题底座**：
  - `Platform/ThemeManager.cs`：实现 `ThemePreference`（`System` / `Light` / `Dark`）、读取 `AppsUseLightTheme` / `SystemUsesLightTheme`、监听 `SystemEvents.UserPreferenceChanged` 热切换、提供健康度语义色与 WCAG 亮度/平滑透明计算函数。
  - `UI/Themes/ThemeLight.xaml`、`UI/Themes/ThemeDark.xaml`、`UI/Themes/SharedStyles.xaml`（整合原 `App.xaml` 控件样式并支持 `DynamicResource`）。
- **临时兼容桥接**：
  - 在 M2 重构三大窗口前，保持现有 `WidgetWindow` / `DetailsWindow` / `SettingsWindow` 可编译运行，并先在现有 `WidgetWindow.Present` 中**移除 `if (translucent) color = Color.FromRgb(...)` 突变黑底逻辑**，接入 `ThemeManager` 的平滑 Alpha 算法。

### 2.3 本阶段必踩坑位提示（Pitfalls）
- `QuotaMonitor.csproj` 当前使用 `<UseWPF>true</UseWPF>` 和 `<UseWindowsForms>true</UseWindowsForms>`，测试工程若引用主工程，建议目标框架也设为 `net8.0-windows` 并开启 `<UseWPF>true</UseWPF>`。
- WPF 分层窗口（`AllowsTransparency="True"`）下，当底板不透明（`alpha == 1`）时开启 `ClearTypeHint.Enabled`；当半透/全透时切回 `TextRenderingMode.Grayscale` 避免边缘发黑锯齿。
- `Preferences.Load` 迁移时，确保旧文件不存在或字段缺失时不会抛出异常，且能保留 `WidgetX`/`WidgetY`。

### 2.4 M1 验收标准
- **命令验证**：
  - `dotnet build QuotaMonitor.sln` → `0 Error`
  - `dotnet test QuotaMonitor.sln` → 全部测试通过
- **新增单元测试覆盖**：
  1. `CodexAccountSource`：同时存在 `primary`(300m) 和 `secondary`(10080m) 时输出 2 个桶（`chatgpt.5h` + `chatgpt.7d`）；仅存在一个时正确降级。
  2. `AntigravityQuotaSource`：正确映射 `ag.gemini.7d`、`ag.claude.7d` 及其余 `5h`/`weekly` 桶。
  3. `Preferences`：v4 样例 JSON（含自定义色、`HiddenWidgetItems`、位置尺寸、阈值）无损迁移到 v5；越界字段自动 Clamp。
  4. `ThemeManager`：透明度 `0 → 1 → 50 → 100` 计算的 RGB 保持连续不突变，仅 Alpha 线性变化；深/浅自定义色按亮度正确切换前景墨色。
  5. `CountdownFormatter`：`< 1h`、`1–24h`、`> 24h`、已过期/无时间等边界格式化正确。

---

## 三、Milestone 2（M2）：三大核心界面重构（WidgetWindow + DashboardPopover + SettingsWindow）

### 3.1 目标
完成主交互界面的全面重设计，落地「精密微光仪表」视觉系统、3 种小组件形态、340px 托盘控制中心与 4 页签统一配置中心。

### 3.2 涉及文件清单
- **UI/Controls/**：
  - `ServiceMark.cs`（由 `Visuals.ServiceMark` 抽离，支持主题色适配）
  - `QuotaProgressBar.cs`（健康度三色渐变 + 轨道 + 数值动画/等宽字）
  - `QuotaRingGauge.cs`（自适应圆环弧线）
  - `CodexResetPanel.cs`（适配 Light/Dark 主题 Token）
- **UI/Windows/**：
  - `GlassWindow.cs`（适配动态主题边框、阴影与 ClearType 策略）
  - `WidgetWindow.cs`：
    - 实装 `Bars`（取消 `MaxHeight = 92` 压扁限制，支持倒计时微标）、`Rings`（1–3 单行 / 4 项 2×2 / 5–6+ 项 3×N 自适应网格）、`CompactPill`（单行横向胶囊）。
    - 实装 `Themed` / `HudGlass` / `CustomColor` 三种表面模式与平滑透明度。
    - 实装悬停 150ms 淡入轻操作条（刷新 / 控制中心 / 设置 / 锁标）。
    - 实装完整右键菜单（含显示方式、窗口层级、锁定位置与尺寸、恢复默认尺寸、隐藏）。
    - 锁定态禁用 `DragMove` 与 8 边缩放 `Thumb`。
  - `DashboardPopover.cs`（替代 `DetailsWindow`）：
    - `340px` 宽、最大高度 `75%` 工作区、固定顶栏（含 Pin 图钉）+ 滚动卡片区 + 固定底栏。
    - 4 平台独立卡片：完整双周期进度、倒计时 + 绝对时间、15m 消耗速率微标（`↓ X%/15m`）、Codex 重置券、Antigravity 全部模型组折叠展开、未连接/需登录内联操作按钮。
  - `SettingsWindow.cs`：
    - `580 × 460` 左侧导航（4 页签）+ 右侧内容区。
    - 页签 1「外观与桌面」（含顶部实时微缩预览卡）、页签 2「服务与显示项」（4 平台连接管理卡 + 桌面显示项勾选与 `↑/↓` 排序）、页签 3「提醒与通知」（低额度/危急/快消耗/重置恢复/单桶覆盖/免打扰/测试通知）、页签 4「常规与诊断」（自启/刷新间隔/脱敏诊断摘要/复制与打开目录）。
- **Platform/Desktop.cs**：
  - 增量支持 `WidgetLayerMode`（`DesktopPinned` / `Normal` / `AlwaysOnTop`）实时切换，仅在 `DesktopPinned` 时启用 `DesktopLayerMonitor`。
- **App.xaml.cs**：
  - 串联 `ThemeManager`、`UsageHistoryStore`、新 `DashboardPopover` 与 `SettingsWindow`，并将配置中的 `PollingIntervalMinutes` 绑定到 `_poll.Interval`。

### 3.3 M2 验收标准
- **命令验证**：`dotnet build QuotaMonitor.sln`（0 Error）、`dotnet test QuotaMonitor.sln`（全部通过）。
- **UI 与交互自查清单（请在报告中逐项核对）**：
  1. 在设置页「外观与桌面」拖动透明度 `0% → 1% → 50% → 100%`，小组件底色无任何骤黑突变；`100%` 全透时仍可拖拽与右键。
  2. 切换 `跟随系统 / 浅色 / 深色`，小组件、设置窗、控制中心即时同步变色。
  3. 勾选 5 个桌面显示项时：`Bars` 模式行距舒展不被压扁；`Rings` 模式自动呈 `3×2` 网格不挤成一排；`CompactPill` 模式呈单行紧凑胶囊。
  4. 锁定小组件后无法拖动和缩放；切换窗口层级（贴桌面底层 / 普通 / 始终置顶）立即生效。
  5. 控制中心宽度 340px 下长重置时间与 Antigravity 多模型组无截断溢出；点击图钉可固定窗口不失焦关闭；未连接服务卡片内按钮可直接触发对应操作。

---

## 四、Milestone 3（M3）：连接向导、全桶告警引擎、动态托盘与最终验收

### 4.1 目标
补齐 4 大平台的向导闭环、全桶全周期智能提醒（含重置恢复与免打扰）、动态指针托盘图标与最终回归测试。

### 4.2 涉及文件清单
- **Sources/GeminiConnection.cs & UI/Windows/GeminiWizardWindow.cs**：
  - 在 `GeminiConnection` 中增量增加连接状态枚举与状态变更事件（不改协议与注册表路径）。
  - 实现 3 步可视向导 `GeminiWizardWindow`（注册并复制路径 + 打开文件夹 → Chrome 加载与授权引导 + 复制 `chrome://extensions` → 实时握手状态指示）。
- **UI/Windows/GrokConnectionWindow.cs & UI/Windows/AntigravityGuideWindow.cs**：
  - 主题化 `GrokConnectionWindow`（顶部安全锁标与步骤提示条）。
  - 新建 `AntigravityGuideWindow`（检测 `agy.exe` 存在性、提供复制登录命令/打开终端指引/重新检测）。
- **Core/AlertEngine.cs（替代 `Quotas.cs` 中旧 `AlertTracker`）**：
  - 按 `BucketKey` 追踪全平台（包含 Antigravity 全桶与 ChatGPT `5h`）。
  - 支持低额度、消耗过快、**周期重置恢复**三大规则、单桶阈值覆盖、免打扰时段（含跨午夜区间）。
- **Platform/TrayIconRenderer.cs & Platform/NotificationService.cs**：
  - `TrayIconRenderer`：按最低剩余百分比动态旋转表盘指针（`-135°` 到 `+135°`），低/危急态叠加右下角状态圆点；生成 ≤63 字符 Tooltip 摘要。
  - `NotificationService`：优先现代 Windows 通知，并安全降级至 `NotifyIcon.ShowBalloonTip`，确保在无 AUMID 环境下零异常；接入设置页「发送测试通知」。
- **清理与测试补齐**：
  - 删除被替代的旧平铺文件（如确认无引用的旧 `Quotas.cs` / `Visuals.cs` / `QuotaWindows.cs`），消除编译警告。
  - 新增单元测试覆盖：`AlertEngine`（全桶覆盖、重置恢复触发条件、冷却防抖、免打扰跨午夜）、`UsageHistoryStore`（24h 裁剪、15m 降幅计算）、`TrayIconRenderer`（Tooltip 长度 ≤63 约束与指针角度映射）。

### 4.3 最终全量验收矩阵
1. `dotnet build QuotaMonitor.sln -c Release`：0 Error，无新增高危 Warning。
2. `dotnet test QuotaMonitor.sln -c Release`：100% 通过。
3. **回归检查**：
   - `GeminiExtension/manifest.json` 的 `key` 字段与改造前逐字节一致。
   - 模拟 Chrome Native Messaging 参数启动（`QuotaMonitor.exe chrome-extension://ogojeakbiilhhagkmchjdeabgebhdbek/`）不会弹出窗口或阻塞。
   - 使用旧版 v4 `settings.json` 启动，迁移为 v5 后窗口坐标、连接标记与阈值完整保留。

---

## 五、可直接复制给 ChatGPT Astra 6.2 的分段启动 Prompt

> **使用方式**：依次把下面三段 Prompt 发给 ChatGPT Astra 6.2。每发完一段，等它跑完构建测试并汇报结果后，再发下一段。

### Prompt 1：启动 Milestone 1（数据底座 + 主题引擎 + 透明修复）

```text
请阅读本仓库中的 `docs/redesign-spec.md` 和 `docs/redesign-plan.md`，现在我们严格按照计划执行 **Milestone 1（M1）**。

核心要求：
1. 先仔细阅读 `docs/redesign-spec.md` 第 2 节「底层护栏与兼容性约束」，绝对不要破坏现有的 Codex RPC、Antigravity CLI、Grok WebView2、Gemini Native Messaging（尤其是 `GeminiExtension/manifest.json` 的 `key`）和 `Desktop.cs` 底层逻辑。
2. 严格按照 `docs/redesign-plan.md` 第 2 节「Milestone 1」的清单与目标实施：
   - 建立 `QuotaMonitor.sln` 与 `QuotaMonitor.Tests`（xUnit）测试工程；
   - 建立 `Core/` 下的 `QuotaModels.cs`、`Preferences.cs`（v5 + v4 无损迁移）、`CountdownFormatter.cs`、`UsageHistoryStore.cs`；
   - 增量扩展 `CodexAccountSource`（同时输出 `chatgpt.5h` + `chatgpt.7d`）与 `AntigravityQuotaSource`（全组全周期映射为 `QuotaBucket`）；
   - 实现 `Platform/ThemeManager.cs`（支持 System / Light / Dark + `UserPreferenceChanged` 热切换）与 `UI/Themes/` 资源字典；
   - 立即修复现有 `WidgetWindow` 中 `Transparency > 0` 时底色 RGB 乘以 (0.13, 0.16, 0.22) 导致的突变黑底 Bug，改为平滑 Alpha 过渡。
3. 完成后请停止，不要提前做 M2。运行 `dotnet build QuotaMonitor.sln` 和 `dotnet test QuotaMonitor.sln`，汇报：
   - 新建与修改的文件列表；
   - 测试用例数量与完整输出；
   - 是否存在任何偏离规格或需要确认的问题。
```

### Prompt 2：启动 Milestone 2（三大核心界面重构）

```text
Milestone 1 已验收通过，现在继续执行 `docs/redesign-plan.md` 中的 **Milestone 2（M2）**，完整规格见 `docs/redesign-spec.md` 第 3、5 节。

核心要求：
1. 开始编码前，请先对照视觉参考资产确认布局比例与交互细节：`docs/mockups/desktop-widgets-v2.png`、`docs/mockups/settings-control-center-v2.png`、`docs/mockups/dashboard-popover-v2.png` 与交互原型 `docs/prototype.html`。
2. 重构三大核心界面并拆分为 `UI/Controls/` 与 `UI/Windows/`：
   - `WidgetWindow`：实现进度条（移除 `MaxHeight = 92` 压扁限制 + 可选倒计时）、自适应圆环网格（4 项 2×2、5–6 项 3×N）、极简迷你胶囊 3 种形态；支持 `Themed` / `HudGlass` / `CustomColor` 平滑透明；实装悬停 150ms 淡入轻操作条、主题化右键菜单、`WidgetLayerMode` 层级切换与 `WidgetLocked` 锁定保护。
   - `DashboardPopover`（替代旧 `DetailsWindow`）：升级为 340px 宽控制中心，包含固定顶栏（带 Pin 图钉按钮）、4 平台独立卡片（完整双周期、倒计时+绝对时间、15m 消耗速率微标、Codex 重置券、Antigravity 全部模型组折叠展开、未连接/需登录内联操作按钮）和固定底栏。
   - `SettingsWindow`：重构为 580×460 四页签控制中心（外观与桌面含实时微缩预览、服务与显示项含排序、提醒与通知、常规与诊断）。
2. 在 `Platform/Desktop.cs` 中增量支持 `WidgetLayerMode`（贴桌面底层 / 普通 / 始终置顶），并在 `App.xaml.cs` 中串联新窗口、主题热切换与可配置轮询周期。
3. 完成后请停止，不要提前做 M3。运行 `dotnet build` 与 `dotnet test`，并对照 `docs/redesign-plan.md` 第 3.3 节 UI 自查清单逐项汇报结果。
```

### Prompt 3：启动 Milestone 3（连接向导 + 全桶告警 + 动态托盘 + 最终验收）

```text
Milestone 2 已验收通过，现在执行最后一个阶段 **Milestone 3（M3）**，完成全部收尾工作。

核心要求：
1. 连接向导、动态托盘与通知体验请参照 `docs/prototype.html` 中的「连接向导预览」与任务栏动态指针设计，以及 `docs/mockups/desktop-widgets-v2.png` 中的任务栏图标质感：
   - 在 `GeminiConnection` 增量暴露只读连接状态事件，新建 3 步式 `GeminiWizardWindow`；
   - 主题化 `GrokConnectionWindow`；
   - 新建 `AntigravityGuideWindow` 检测 `agy.exe` 并提供登录引导。
2. 告警与托盘升级：
   - 实现全桶 `Core/AlertEngine.cs`（按 `BucketKey` 覆盖全部 4 平台含 Antigravity 与 ChatGPT 5h，支持低额度、消耗过快、周期重置恢复提醒、单桶阈值覆盖与免打扰时段）；
   - 实现 `Platform/TrayIconRenderer.cs`（按最低剩余额度动态旋转指针 + 偏低/危急角标点 + ≤63 字符 Tooltip）；
   - 实现 `Platform/NotificationService.cs`（优先 Windows 通知并安全降级至 `ShowBalloonTip`，接入设置页测试通知按钮）。
3. 补齐 `AlertEngine`、`UsageHistoryStore`、`TrayIconRenderer` 的单元测试，清理冗余旧文件与编译警告。
4. 运行 `dotnet build QuotaMonitor.sln -c Release` 和 `dotnet test QuotaMonitor.sln -c Release`，对照 `docs/redesign-plan.md` 第 4.3 节最终验收矩阵（含 `GeminiExtension/manifest.json` key 未改动确认、v4 配置兼容验证）输出完整最终报告。
```
