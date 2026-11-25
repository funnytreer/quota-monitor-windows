# 《额度监控器（QuotaMonitor）》v2 视觉与高透明体验专项打磨计划

> **目标**：针对现有 M2 实机截图中的 5 类突出视觉问题做精准外科手术式打磨，让桌面小组件（尤其在中高透明度下）与设置窗口达到旗舰级 Windows 11 Fluent 2 质感。
> **对照资产**：[`docs/mockups/desktop-widgets-v2.png`](mockups/desktop-widgets-v2.png)、[`docs/mockups/settings-control-center-v2.png`](mockups/settings-control-center-v2.png)、[`docs/mockups/dashboard-popover-v2.png`](mockups/dashboard-popover-v2.png)、[`docs/mockups/tray-menu-v2.png`](mockups/tray-menu-v2.png)、[`docs/prototype.html`](prototype.html)、[`docs/redesign-spec.md`](redesign-spec.md)。

---

## 一、实机问题清单与精确修复方案

### 1. 彻底移除小组件鼠标悬停工具栏与设置入口
- **现状问题**：鼠标移入小组件时右上角弹出带白底边框的 `[↻] [📄] [⚙]` 浮层，遮挡第一行数据与 `74%` 数值，且在迷你胶囊上显得突兀。
- **修复方案（`UI/Windows/WidgetWindow.cs`）**：
  - **完全删除 `AddTools(chrome)` 方法及其调用**，删除 `_lock` 悬停显示块与 `toolbar` 动画。
  - 小组件保持**零悬停浮层、零按钮入口**——所有设置、控制中心与手动刷新入口统一从**系统托盘图标**进入（右键托盘菜单保留全部入口；小组件右键仍可保留轻量上下文菜单或按需操作）。

### 2. 彻底清除高透明度下的灰白贴片块（`ReadingBackdrop`）
- **现状问题**：`Transparency >= 45%` 时，`ThemeManager.Surface` / `WidgetSurface.ReadingBackdrop` 在每行文字、每个胶囊格、圆环底部标签和 `检查于 13:20` 背后都垫了一块不协调的灰白圆角矩形，破坏通透感。
- **修复方案**：
  - **`Core/Preferences.cs`**：新增 `WidgetTextContrast` 枚举（`Auto`、`Light`、`Dark`，默认值 `Auto`）。将原 `RingFillColor` 标记为废弃并忽略（JSON 中若存在旧字段不报错）。
  - **`Platform/ThemeManager.cs`**：
    - 删除 `WidgetSurface.InkProtection` 与 `WidgetSurface.ReadingBackdrop`；
    - 新增 `ResolveLightInk(WidgetSurfaceStyle style, Color customBg, int transparency, bool systemDark, WidgetTextContrast contrast)`：
      - `Light` → 强制浅白文字体系；
      - `Dark` → 强制深墨文字体系；
      - `Auto` → 当 `Transparency >= 45` 或 `HudGlass` 时，默认跟随系统深浅（若未强制浅色壁纸场景则优先采用高对比亮白文字 + 柔和暗影，保证深色/彩色壁纸上通透清晰）。
    - 高透明亮白文字（`lightInk == true` 且 `Transparency >= 40` 或 `HudGlass`）时，进度条自动选用高亮度遥测色（Normal `#38BDF8 → #3B82F6`、Low `#FBBF24`、Critical `#FB7185`）与通透轨道（`#2EFFFFFF`）。
  - **`UI/Windows/WidgetWindow.cs`**：
    - 删除所有 `backdrop`、`labelPadding`、`_timePlate.Background`、`cell.Background`、行容器 `Border.Background`；
    - 保留唯一一层光滑磨砂底板（`Plate.Background`）与 `1px` 上亮下淡玻璃描边；
    - 当 `Transparency >= 40` 或 `HudGlass` 时，为文字与图标附加轻柔 `DropShadowEffect`（亮白字配黑色 `BlurRadius=5, ShadowDepth=1, Opacity=0.62`；深墨字配白色轻晕），彻底取代丑陋的贴片底板。

### 3. 三种小组件形态专项精修
- **进度条模式（`Bars`）**：
  - 修复底部出现多余空白的间距问题（`Grid.RowDefinitions` 使用 `Auto` 并精确匹配内容高度，不再多留空行）。
  - 行内元素清晰对齐：`图标 + 名称` 左对齐，`紧凑倒计时 + 百分比 + 警示标` 右对齐，下方 `4px` 圆角进度条。
- **圆环网格模式（`Rings`，`UI/Controls/QuotaRingGauge.cs`）**：
  - 彻底删除 `RingFace` / `RingFillColor` 带来的圆环内实心灰白底，所有圆环内部**完全镂空透明（`Fill = Brushes.Transparent`）**。
  - 将 `StrokeThickness` 从 `7` 减细至 **`4.5`**，视觉更轻盈精致。
  - 删除 `insideCaption`（文字不再塞进圆环内部导致撞框），圆环内**只居中显示百分比**，服务名标签统一固定在圆环**正下方**并保证充足间距。
- **迷你胶囊模式（`CompactPill`）**：
  - 单层统一圆角胶囊，内部无任何分块底色，各项之间用 `1px × 12px` 的 `22%` 透明度竖线分隔。
  - 对同属 Antigravity 图标的项（`ag.gemini.7d` / `ag.claude.7d`）在图标旁增加极简微标 `G` / `C`，一眼区分 `81%` 和 `100%`。
  - 在 `CompactPill` 模式下**强制隐藏 `_resizeMark`**，消除右下角斜杠刺破胶囊圆角的问题。

### 4. 设置窗口（`SettingsWindow`）Fluent 2 级升级
- **现状问题**：左侧导航有突兀的蓝框；右侧单选按钮组是一排带蓝框的独立按钮；预览区是双层套娃边框配灰扑扑的渐变底；深色模式下透明度滑块轨道几乎隐身。
- **修复方案（`UI/Windows/SettingsWindow.cs`、`UI/Themes/ThemeLight.xaml`、`ThemeDark.xaml`、`SharedStyles.xaml`）**：
  - 窗口尺寸微调为 **`640 × 500`**（最小 `580 × 450`），改为 Fluent 2 **双色分栏**：左侧 `168px` 导航栏使用略深一阶的 `Surface.Sidebar`（Light `#F1F4F9` / Dark `#11151C`）+ 右侧 `1px` 分割线 + 右侧主画布 `Surface.Window`（Light `#F8FAFC` / Dark `#161B24`）。
  - **左侧导航栏**：移除蓝框 `BorderBrush`，选中态改为柔和填充 `Accent.PrimarySoft` + 左侧边缘一条 **`3px × 16px` 圆角竖条指示器**。
  - **真·一体式分段控制器（Segmented Control）**：重写 `Options<T>`，外层为一个凹槽圆角容器（`Surface.Tray`：`#E9EEF5` / `#111620`，`Padding=3`，`CornerRadius=8`），内部选项无边框，仅选中项显示一块微凸起的滑块底色（`#FFFFFF` / `#273142`）+ 轻微阴影。
  - **沉浸式单框壁纸预览**：移除双层嵌套卡片与单调灰渐变，改为单层 `Height=152, CornerRadius=12` 的预览舞台，内置**深蓝星空 / 浅色雾蓝壁纸切换小按钮**，直接居中展示当前配置的小组件效果（无任何多余按钮）。
  - **表单分组规范**：卡片内采用 `16px` 内边距、每行固定约 `44px` 节奏、行间以 `1px` `Border.Hairline` 分隔。移除 `圆环填充` 设置行，新增 **`高透明文字（自动 / 亮白 / 深墨）`** 分段控制。
  - **滑块高对比修复**：`ThemeDark.xaml` 中滑块未填充轨道改为可见度明确的 `#334155`（Light 为 `#CBD5E1`），拇指圆点改为 `#F8FAFC` 配描边，确保深浅主题下都清晰好拖。

### 5. 系统托盘区域全面精细化（托盘右键菜单 + 托盘图标 + 控制中心微调）
- **自定义 Fluent 2 托盘右键菜单（`UI/Windows/TrayMenuWindow.cs` + `App.xaml.cs`）**：
  - 彻底移除 `App.xaml.cs` 中老旧的 `Forms.ContextMenuStrip`（系统灰白菜单）；
  - 新建无边框 `TrayMenuWindow`（`Width = 228`、`CornerRadius = 12`、`Padding = 6`、完整接入 `ThemeManager` 浅/深主题，支持 `↑/↓`、`Enter`、`Esc` 与失焦关闭）；
  - 精简为 6 项核心菜单：`额度控制中心`、`立即刷新额度`、`显示桌面小组件`（✓）、`锁定小组件位置`（✓）、`设置中心…`、分割线、`退出`（仅悬停/聚焦时显示危险红）；
  - 托盘左键切换 `DashboardPopover`、右键弹出 `TrayMenuWindow`，两者互斥关闭，并用 `Desktop.PlaceNearTray` 安全定位在当前显示器工作区内。
- **锐化动态托盘图标（`Platform/TrayIconRenderer.cs`）**：
  - 由原来的 7 点闭环改为 **270° 开口圆弧仪表盘**（`StartAngle = 135°`、`SweepAngle = 270°`、`LineCap.Round`），`size <= 20`（100%–125% DPI）时自动隐藏刻度点保持边缘锐利，`size >= 24` 时仅保留 3 个主刻度；
  - 告警圆点改用 `CompositingMode.SourceCopy` + `Color.Transparent` 先挖出 `10.5px` 透明隔离环，再绘制 `8px` 琥珀/红状态圆点，避免糊成一团。
- **控制中心细节精修（`UI/Windows/DashboardPopover.cs`）**：
  - 顶栏/底栏加 `1px Border.Hairline` 分隔；顶栏按钮（固定/刷新/设置）统一 `28×28` 圆角柔和底；`↓ X%/15m` 改用胶囊徽标；`展开全部模型周期` 改用柔和胶囊按钮；底栏显隐按钮加眼睛图标；托盘面板和右键菜单首次显示即位于最终位置，使用最终不透明度。
- **托盘浮层点击外部可靠自动收起（`Desktop.cs` + `DashboardPopover.cs` + `TrayMenuWindow.cs` + `App.xaml.cs`）**：
  - `Desktop.ShowNearTray` 展示浮层时调用 Win32 `SetForegroundWindow(hwnd)` 配合 `Activate()`，确保真正获焦；
  - `TrayMenuWindow` 与未固定的 `DashboardPopover` 同时监听 `Deactivated` 与 `WM_ACTIVATEAPP (wParam == 0)`，并在点击 `WidgetWindow`（因 `WS_EX_NOACTIVATE` 不抢焦点）时显式联动收起；
  - 记录失焦收起时间戳，对 `250ms` 内的同图标点击做防抖，避免“点托盘图标想关闭却闪回重开”。

### 6. 4K HiDPI 与 240Hz 高刷新率专项优化（详见 `docs/redesign-spec.md` 第 8 节）
- **原生 WindowChrome + 240Hz 硬件合成（`UI/Windows/SettingsWindow.cs` & 向导弹窗）**：
  - 移除 `AllowsTransparency="True"` 软分层渲染，改用 `WindowChrome` + Win11 DWM 原生圆角（`DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND`）与原生暗色标题栏属性（`DWMWA_USE_IMMERSIVE_DARK_MODE`），消除 4K 240Hz 拖动/缩放发粘。
- **动态 240Hz 帧率匹配（新建 `Platform/DisplayMetrics.cs`）**：
  - 通过 `EnumDisplaySettingsW` 读取窗口所在显示器真实刷新率（钳位 `60..240`），为进度条动画设置 `Timeline.SetDesiredFrameRate`，解除 WPF 默认 60fps 锁帧。
- **半透明窗口性能锁定与脏检查（`WidgetWindow.cs` / `DashboardPopover.cs` / `TrayMenuWindow.cs`）**：
  - `DropShadowEffect` 统一 `RenderingBias = RenderingBias.Performance`；只读画刷与 `StreamGeometry` 调用 `.Freeze()`；`_clock.Tick` 与刷新实行脏检查，仅在数据或倒计时文本变化时更新 UI。
- **4K 锐利渲染（`QuotaMonitor.csproj` / `GlassWindow.cs` / `QuotaRingGauge.cs` / `TrayIconRenderer.cs`）**：
  - `QuotaMonitor.csproj` 补充 `<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>`；
  - `GlassWindow` 与 `WidgetWindow` 在 `DpiScaleX >= 1.25` 时统一使用 `TextFormattingMode.Ideal`，并重写 `OnDpiChanged`；
  - `QuotaRingGauge` 取消 `Viewbox` 二次缩放，直接生成精确 DIP 直径圆弧；
  - `TrayIconRenderer` 采用 `size * 4` 超采样 + `HighQualityBicubic` 下采样；
  - 桌面小组件副文本字号下限 ≥ `11 DIP`，并在 `Core/Preferences.cs` 与 `SettingsWindow` 新增 `WidgetScale`（`0.9 / 1.0 / 1.15 / 1.3`）用于 4K 大屏无损放大。
- **DPI 偏移修正与零卡顿桌面守护（`Platform/Desktop.cs`）**：
  - 默认位置偏移 `14`、`27` 与托盘水平锚点偏移 `36` 均乘以 `DpiScaleX` / `DpiScaleY`；
  - 重构 `DesktopLayerMonitor`：缓存 `DesktopIconHost` HWND + `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` 事件驱动 + ≥ `2000ms` 低频自愈（拖拽期间暂停），彻底消灭 250ms `EnumWindows` 轮询引起的 240Hz 微卡顿。

---

## 二、可直接复制给 ChatGPT Astra 6.2 的执行 Prompt

```text
请阅读本仓库更新后的 `docs/redesign-spec.md`（第 3.4、3.6、4.1、4.3、5.1、5.2、5.3、5.5、6.4 节及新增第 8 节）和专项清单 `docs/visual-polish-plan.md`，先对照最新 2× 高清视觉稿 `docs/mockups/desktop-widgets-v2.png`、`docs/mockups/settings-control-center-v2.png`、`docs/mockups/dashboard-popover-v2.png`、`docs/mockups/tray-menu-v2.png` 和交互原型 `docs/prototype.html`，对现有的 M2 界面代码执行一次 **v2 视觉、托盘与 4K 240Hz 精细化升级**。

核心改动要求（请严格逐项落地）：
1. **彻底删除小组件悬停工具栏**：在 `UI/Windows/WidgetWindow.cs` 中完全移除 `AddTools` 悬停按钮栏（刷新/控制中心/设置入口统一从托盘图标进入，鼠标移入小组件不得出现任何按钮或浮层）。
2. **彻底清除所有灰白贴片与圆环实心底**：
   - 在 `Platform/ThemeManager.cs` 删除 `InkProtection` / `ReadingBackdrop`；
   - 在 `Core/Preferences.cs` 新增 `WidgetTextContrast`（Auto/Light/Dark）与 `WidgetScale`（0.9/1.0/1.15/1.3），弃用 `RingFillColor`；
   - 在 `WidgetWindow.cs` 移除所有行贴片、时间底板和胶囊单格底色，改用单层磨砂底板 + 高透明下柔和文字阴影（`DropShadowEffect`）+ 高亮通透进度条配色，并按 `WidgetScale` 无损放大布局与字号（副文本 ≥ 11 DIP）；
   - 在 `UI/Controls/QuotaRingGauge.cs` 让圆环内部完全透明镂空，线宽改为 `4.5`，取消 `Viewbox` 二次缩放改为精确 DIP 半径，删除 `insideCaption`，只保留百分比在圆环内、标签在圆环正下方；
   - 在 `Core/DesktopItems.cs` 中移除桌面小组件标签的 `AG·` 前缀（`ag.gemini.7d` 显示为 `Gemini`，`ag.claude.7d` 显示为 `Claude`，由 Antigravity 图标区分于 Gemini 网页版）。
   - 在 `CompactPill` 模式下用 `1px` 竖线分隔各项，为 Antigravity 两项增加 `G` / `C` 区分标记，并隐藏右下角 `_resizeMark`。
3. **升级 `SettingsWindow` 为 Windows 11 Fluent 2 风格（640×500）**：
   - 脱离 `AllowsTransparency`，改用 `WindowChrome` + Win11 DWM 原生圆角与暗色标题栏，跑满 4K 240Hz 拖拽；
   - 左侧 `168px` 双色分栏 + 左侧 `3px` 竖条选中指示（无蓝色外边框）；
   - 将 `Options<T>` 重构为真·一体式凹槽分段控制器（无独立蓝框按钮）；
   - 单框沉浸壁纸预览舞台（带深/浅壁纸切换，无双重边框与灰渐变）；
   - 移除「圆环填充」，新增「小组件尺寸（90%/100%/115%/130%）」与「高透明文字」分段控制，并提升深色模式下透明度滑块的轨道对比度。
4. **系统托盘区域全面精细化（对照 `tray-menu-v2.png` 与 `dashboard-popover-v2.png`）**：
   - 新建 `UI/Windows/TrayMenuWindow.cs` 替代 `App.xaml.cs` 中的 WinForms `ContextMenuStrip`，实现带浅/深主题、键盘导航和 6 项精简菜单的 Fluent 2 托盘右键菜单，并与 `DashboardPopover` 左右键互斥弹出；
   - 在 `Platform/TrayIconRenderer.cs` 将图标升级为 270° 开口圆弧仪表盘（`size <= 20` 隐藏刻度点）+ 4× 超采样下采样 + `CompositingMode.SourceCopy` 透明隔离环告警角标；
   - 在 `UI/Windows/DashboardPopover.cs` 补齐顶/底栏分割线、28×28 柔和圆角工具按钮、胶囊 `↓ X%/15m` 速率标、柔和展开胶囊，托盘面板和右键菜单在最终位置直接显示；
   - **点击外部自动收起**：在 `Desktop.ShowNearTray` 调用 Win32 `SetForegroundWindow(hwnd)`，对 `TrayMenuWindow` 和未固定的 `DashboardPopover` 监听 `Deactivated` + `WM_ACTIVATEAPP` + `WidgetWindow` 点击联动收起，并增加 `250ms` 托盘点击防抖防止关闭后闪回。
5. **4K HiDPI 与 240Hz 专项优化（按 `docs/redesign-spec.md` 第 8 节）**：
   - 在 `QuotaMonitor.csproj` 补充 `<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>`；
   - 新增 `Platform/DisplayMetrics.cs` 读取当前显示器刷新率并为动画设置 `Timeline.SetDesiredFrameRate`；所有只读画刷/`StreamGeometry` 调用 `.Freeze()`，`DropShadowEffect` 设 `RenderingBias.Performance`；
   - `GlassWindow` 统一 4K 高 DPI 下使用 `TextFormattingMode.Ideal` 并重写 `OnDpiChanged`；
   - `Platform/Desktop.cs` 修正硬编码物理像素偏移为 DPI 缩放值，并将 `DesktopLayerMonitor` 重构为缓存 HWND + `SetWinEventHook` 事件驱动 + ≥2000ms 低频自愈。
6. 同步更新 `QuotaMonitor.Tests`（补充 `WidgetScale` 持久化、DPI 偏移换算、刷新率钳位、4× 超采样图标与主题/样式断言），运行 `dotnet build QuotaMonitor.sln` 和 `dotnet test QuotaMonitor.sln` 确保 0 Error 且全部通过，完成后汇报修改文件与测试结果。
```
