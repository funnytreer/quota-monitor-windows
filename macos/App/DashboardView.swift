import SwiftUI
import ServiceManagement

private typealias ViewState<Value> = State<Value>

struct DashboardView: View {
    @ObservedObject var store: QuotaStore

    private var screen: QuotaStore.Screen { store.selection ?? .overview }

    var body: some View {
        HStack(spacing: 0) {
            VStack(alignment: .leading, spacing: 5) {
                ForEach(QuotaStore.Screen.allCases) { item in
                    Button {
                        store.show(item)
                    } label: {
                        Label {
                            Text(item.title)
                        } icon: {
                            if let service = item.feed?.service {
                                ServiceIcon(service: service, size: 20)
                            } else {
                                Image(systemName: item == .overview ? "gauge.with.dots.needle.33percent" : "gearshape")
                                    .frame(width: 20, height: 20)
                            }
                        }
                            .font(.system(size: 13, weight: screen == item ? .semibold : .regular))
                            .foregroundStyle(screen == item ? Color.accentColor : Color.primary)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .padding(.horizontal, 10)
                            .padding(.vertical, 9)
                            .background {
                                if screen == item {
                                    RoundedRectangle(cornerRadius: 8).fill(Color.accentColor.opacity(0.14))
                                }
                            }
                            .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .accessibilityAddTraits(screen == item ? .isSelected : [])
                }
                Spacer()
            }
            .frame(width: 164)
            .padding(.horizontal, 10)
            .padding(.top, 17)
            .background(Color(nsColor: .controlBackgroundColor))

            Divider()

            TimelineView(.everyMinute) { context in
                VStack(alignment: .leading, spacing: 0) {
                    HStack(spacing: 12) {
                        Text(screen.feed?.name ?? screen.title).font(.title2.bold())
                        Spacer(minLength: 8)
                        if screen == .overview || screen.feed != nil {
                            RefreshButton(store: store, feed: screen.feed, now: context.date)
                        }
                        DashboardWindowMenu()
                    }
                    .padding(.horizontal, 24)
                    .padding(.vertical, 14)
                    Divider()
                    ScrollView {
                        Group {
                            switch screen {
                            case .overview: OverviewView(store: store, now: context.date)
                            case .settings: SettingsPanel(store: store)
                            default:
                                if let feed = screen.feed {
                                    FeedView(store: store, feed: feed, now: context.date).id(feed)
                                }
                            }
                        }
                        .frame(maxWidth: 810, alignment: .leading)
                        .padding(24)
                        .frame(maxWidth: .infinity)
                    }
                }
            }
            .background(Color(nsColor: .windowBackgroundColor))
        }
        .frame(minWidth: 550, minHeight: 390)
        .sheet(isPresented: $store.grokLoginPresented, onDismiss: { store.grokClient.endLogin() }) {
            GrokConnectionSheet(store: store)
        }
    }
}

private struct DashboardWindowMenu: View {
    private var window: NSWindow? {
        NSApp.windows.first {
            $0.isQuotaDashboard && $0.isVisible
        }
    }

    var body: some View {
        Menu {
            Button("关闭窗口", systemImage: "xmark") { window?.performClose(nil) }
            Button("最小化窗口", systemImage: "minus") { window?.miniaturize(nil) }
            Button("切换全屏", systemImage: "arrow.up.left.and.arrow.down.right") { window?.toggleFullScreen(nil) }
        } label: {
            Label("窗口", systemImage: "macwindow")
                .font(.callout)
        }
        .menuIndicator(.hidden)
        .menuStyle(.borderlessButton)
        .accessibilityLabel("窗口操作")
        .help("窗口操作")
    }
}

private struct GrokConnectionSheet: View {
    @ObservedObject var store: QuotaStore

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("连接 Grok").font(.title2.bold())
            Text("请在下方官方页面登录并确认能看到周用量，然后开始自动监测。")
                .foregroundStyle(.secondary)
            HStack {
                Button("打开登录页") { store.grokClient.navigate(signIn: true) }
                Button("打开用量页") { store.grokClient.navigate(signIn: false) }
            }
            GrokWebsite(client: store.grokClient, signInFirst: !store.grokEnabled)
                .frame(minWidth: 620, minHeight: 410)
                .clipShape(RoundedRectangle(cornerRadius: 8))
            HStack {
                Button("取消", role: .cancel) { store.grokLoginPresented = false }
                    .keyboardShortcut(.cancelAction)
                Spacer()
                Button("开始自动监测") { store.finishGrokLogin() }
                    .buttonStyle(.borderedProminent)
            }
        }
        .padding(18)
        .frame(idealWidth: 760, idealHeight: 540)
    }
}

private struct OverviewView: View {
    @ObservedObject var store: QuotaStore
    let now: Date

    private var upcoming: [Quota] {
        store.snapshot.monitoredQuotas.filter { ($0.resetsAt ?? .distantPast) > now }
            .sorted { ($0.resetsAt ?? .distantFuture) < ($1.resetsAt ?? .distantFuture) }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 235), spacing: 24, alignment: .topLeading)],
                      alignment: .leading, spacing: 18) {
                ForEach(QuotaSummaryItem.items, id: \.self) { item in
                    card(item)
                }
            }

            VStack(alignment: .leading, spacing: 9) {
                Text("接下来").font(.title3.bold())
                Divider()
                if upcoming.isEmpty {
                    Text("暂无已知的重置时间").foregroundStyle(.secondary)
                }
                ForEach(upcoming) { quota in
                    Button {
                        if let screen = QuotaStore.Screen(rawValue: quota.feed.rawValue) {
                            store.show(screen, quota: quota)
                        }
                    } label: {
                        HStack(spacing: 10) {
                            Label(quota.resetLabel(at: now) ?? "", systemImage: "calendar")
                                .frame(minWidth: 105, alignment: .leading)
                            Text("\(quota.service.name) · \(quota.summaryTitle)").foregroundStyle(.secondary)
                            Spacer()
                            Image(systemName: "chevron.right").font(.caption2).foregroundStyle(.secondary)
                        }
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    Divider()
                }
            }
            HStack {
                if let newest = store.snapshot.monitoredQuotas.filter({ $0.source == .official }).map(\.lastSuccessAt).max() {
                    Label("最新读数 \(newest.formatted(.dateTime.month().day().hour().minute()))", systemImage: "clock")
                        .font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                Button("设置", systemImage: "gearshape") { store.show(.settings) }
                    .buttonStyle(.borderless)
            }
        }
    }

    private func card(_ item: QuotaSummaryItem) -> some View {
        let service = item.service
        let quota = item.reading(in: store.snapshot)
        let feed = service.primaryFeed
        return VStack(alignment: .leading, spacing: 12) {
            ServiceTitle(item: item, quota: quota)
            if let quota {
                ReadingView(quota: quota, now: now, size: 44)
            } else {
                Text("暂无读数").font(.title3).foregroundStyle(.secondary)
            }
            if service == .codex, store.codexEnabled {
                CodexResetSummary(credits: store.snapshot.codexResetCredits)
            }
            Button(quota == nil && !store.enabled(feed) ? "设置来源" : "查看详情", systemImage: "arrow.right") {
                if let screen = QuotaStore.Screen(rawValue: feed.rawValue) {
                    store.show(screen, quota: quota)
                }
            }
            .buttonStyle(.borderless)
        }
        .frame(maxWidth: .infinity, minHeight: 130, alignment: .topLeading)
    }
}

private struct FeedView: View {
    @ObservedObject var store: QuotaStore
    let feed: Feed
    let now: Date
    @ViewState private var editor: QuotaDraft?
    @ViewState private var confirmDisconnect = false

    private var quotas: [Quota] {
        let items = store.snapshot.quotas.filter { $0.feed == feed }
        if feed == .grok, items.contains(where: { $0.source == .official }) {
            return items.filter { $0.source == .official }
        }
        return items
    }

    var body: some View {
        ScrollViewReader { proxy in
            VStack(alignment: .leading, spacing: 17) {
                Text(description).foregroundStyle(.secondary)
                flowActions
                if store.enabled(feed) {
                    if !store.online { Label("当前离线", systemImage: "wifi.slash").foregroundStyle(.secondary) }
                    if let message = store.message(feed) {
                        Label(message, systemImage: "exclamationmark.circle").foregroundStyle(.secondary)
                    }
                    if let retry = store.nextRetry(feed), retry > now {
                        Text("可重试于 \(retry.formatted(.dateTime.hour().minute()))").foregroundStyle(.secondary)
                    }
                }

                Divider()
                if feed == .codex, store.codexEnabled {
                    CodexResetDetail(credits: store.snapshot.codexResetCredits)
                }
                if quotas.isEmpty {
                    VStack(alignment: .leading, spacing: 5) {
                        Text("暂无读数").font(.title2.bold())
                        Text(emptyMessage)
                            .foregroundStyle(.secondary)
                    }
                }
                ForEach(quotas) { quota in
                    VStack(alignment: .leading, spacing: 9) {
                        HStack {
                            Text(quota.title).font(.title3.bold())
                            Spacer()
                            if quota.service.supportsPinning {
                                Group {
                                    if store.snapshot.pinned(quota.service)?.widgetKey == quota.widgetKey {
                                        Label("已固定", systemImage: "pin.fill")
                                            .font(.caption).foregroundStyle(.secondary)
                                    } else {
                                        Button("固定", systemImage: "pin") { store.pin(quota) }
                                    }
                                }
                                .help("固定到菜单栏和使用默认项的组件")
                            }
                        }
                        ReadingView(quota: quota, now: now, size: 38)
                        Text("\(quota.source.name) · \(quota.lastSuccessAt.formatted(.dateTime.year().month().day().hour().minute()))")
                            .font(.caption).foregroundStyle(.secondary)
                        if quota.lastAttemptAt > quota.lastSuccessAt {
                            Text("最近尝试 \(quota.lastAttemptAt.formatted(.dateTime.month().day().hour().minute()))")
                                .font(.caption).foregroundStyle(.secondary)
                        }
                        if quota.source == .manual {
                            HStack {
                                Button("编辑") { editor = QuotaDraft(feed: feed, quota: quota) }
                                Button("移除", role: .destructive) { store.deleteManual(quota) }
                            }
                        }
                    }
                    .padding(14)
                    .background(.quaternary.opacity(0.35), in: RoundedRectangle(cornerRadius: 9))
                    .overlay {
                        if store.selectedQuota == quota.widgetKey {
                            RoundedRectangle(cornerRadius: 9).strokeBorder(.blue.opacity(0.6))
                        }
                    }
                    .id(quota.widgetKey)
                }
                if store.enabled(feed) || !quotas.isEmpty {
                    Button(store.enabled(feed) ? "断开 \(feed.name)" : "清除此来源的记录", role: .destructive) {
                        confirmDisconnect = true
                    }
                }
            }
            .onChange(of: store.selectedQuota, initial: true) { _, selected in
                if let selected { proxy.scrollTo(selected, anchor: .center) }
            }
        }
        .sheet(item: $editor) { draft in ManualEditor(draft: draft, save: store.saveManual) }
        .confirmationDialog(store.enabled(feed) ? "断开 \(feed.name)？" : "清除此来源的记录？", isPresented: $confirmDisconnect) {
            Button("清除该来源的记录", role: .destructive) { store.disconnect(feed) }
        } message: {
            Text("此来源的本地和桌面小组件读数将被清除。")
        }
    }

    private var description: String {
        switch feed {
        case .codex:
            if quotas.contains(where: { $0.source == .official }) { return "已读取 Codex 订阅额度" }
            return "使用官方 Codex 已有登录读取订阅额度。"
        case .legacyChat, .legacyGeminiWeb: return ""
        case .grok: return "登录后从 Grok 官方用量页自动获取周额度和重置时间。"
        case .antigravity: return "来自 Antigravity CLI。优先显示 Gemini 与 Claude / GPT 的每周剩余，随后是各组的 5 小时额度。"
        }
    }

    private var emptyMessage: String {
        switch feed {
        case .codex: "连接 Codex 或添加一条手动记录。"
        case .legacyChat, .legacyGeminiWeb: ""
        case .grok: store.enabled(feed) ? "成功读取后会自动显示。" : "连接并登录 Grok 后开始读取。"
        case .antigravity: store.enabled(feed) ? "成功读取后会自动显示两组周额度。" : "连接本机已登录的 Antigravity CLI。"
        }
    }

    private var flowActions: some View {
        ViewThatFits(in: .horizontal) {
            HStack { actions }
            VStack(alignment: .leading) { actions }
        }
    }

    @ViewBuilder private var actions: some View {
        if feed == .codex {
            Button(store.codexEnabled ? "重新连接" : "连接 Codex", systemImage: "link") { store.connectCodex() }
                .buttonStyle(.borderedProminent)
                .disabled(store.refreshing || !store.online || (store.retryAt ?? .distantPast) > now)
            if store.codexMessage != nil {
                Button("打开官方客户端") { store.login() }
            }
        }
        if feed == .grok {
            Button(store.grokEnabled ? "打开连接窗口" : "连接 Grok", systemImage: "link") {
                store.presentGrokLogin()
            }
            .buttonStyle(.borderedProminent)
        }
        if feed == .antigravity {
            Button(store.antigravityEnabled ? "刷新 CLI 额度" : "连接 Antigravity CLI", systemImage: "arrow.clockwise") {
                store.connectAntigravity()
            }
            .buttonStyle(.borderedProminent)
            .disabled(store.antigravityRefreshing || !store.online)
        } else {
            Link(destination: feed.url) { Label("查看官方用量", systemImage: "arrow.up.right") }
        }
        if feed == .codex {
            Button("手动记录", systemImage: "plus") { editor = QuotaDraft(feed: feed) }
        }
    }
}

private struct CodexResetDetail: View {
    let credits: CodexResetCredits?

    var body: some View {
        VStack(alignment: .leading, spacing: 7) {
            HStack {
                Label("可用重置次数", systemImage: "arrow.counterclockwise").font(.headline)
                Spacer()
                if let credits {
                    Text("\(credits.availableCount) 次").font(.title3.bold()).monospacedDigit()
                }
            }
            if let credits {
                ForEach(credits.expirations) { expiration in
                    Text("\(expiration.count) 次于 \(expiration.date.formatted(.dateTime.year().month().day().hour().minute())) 到期")
                        .font(.subheadline).foregroundStyle(.secondary)
                }
                if credits.countWithoutExpiry > 0 {
                    Text("\(credits.countWithoutExpiry) 次的到期时间未提供")
                        .font(.subheadline).foregroundStyle(.secondary)
                }
                HStack(spacing: 4) {
                    if let status = credits.statusLabel { Text(status) }
                    Text("更新于 \(credits.lastSuccessAt.formatted(.dateTime.month().day().hour().minute()))")
                }
                .font(.caption).foregroundStyle(.secondary)
            } else {
                Text("Codex 暂未返回可用重置次数。")
                    .font(.subheadline).foregroundStyle(.secondary)
            }
        }
        .padding(14)
        .background(.quaternary.opacity(0.35), in: RoundedRectangle(cornerRadius: 9))
    }
}

private struct LoginItemSetting: View {
    @ViewState private var status: SMAppService.Status = .notRegistered
    @ViewState private var failure: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Toggle("登录时自动启动", isOn: Binding(get: { status == .enabled || status == .requiresApproval },
                                                set: setEnabled))
                .toggleStyle(.switch)
            if status == .requiresApproval {
                HStack {
                    Text("等待系统允许。").foregroundStyle(.secondary)
                    Button("打开登录项设置") { SMAppService.openSystemSettingsLoginItems() }
                        .buttonStyle(.link)
                }
            }
            if let failure {
                Text(failure).font(.callout).foregroundStyle(.secondary)
            }
        }
        .onAppear { status = SMAppService.mainApp.status }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            status = SMAppService.mainApp.status
            failure = nil
        }
    }

    private func setEnabled(_ enable: Bool) {
        let service = SMAppService.mainApp
        failure = nil
        do {
            switch (enable, service.status) {
            case (false, .enabled), (false, .requiresApproval): try service.unregister()
            case (true, .notRegistered), (true, .notFound): try service.register()
            default: break
            }
        } catch {
            let current = service.status
            if enable != (current == .enabled || current == .requiresApproval) {
                failure = "无法更改登录项。\(error.localizedDescription)"
            }
        }
        status = service.status
    }
}

private struct SettingsPanel: View {
    @ObservedObject var store: QuotaStore

    var body: some View {
        VStack(alignment: .leading, spacing: 17) {
            LoginItemSetting()
            Divider()
            VStack(alignment: .leading, spacing: 8) {
                Picker("菜单栏指针", selection: $store.menuGaugeSource) {
                    ForEach(MenuGaugeSource.allCases, id: \.self) { source in
                        Text(source.title).tag(source)
                    }
                }
                .pickerStyle(.menu)
                Text(store.menuGaugeSource == .average ? "按有读数的项目等权计算周额度平均剩余。" : "指针显示所选项目的每周剩余比例。")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Divider()
            VStack(alignment: .leading, spacing: 12) {
                Label("刷新", systemImage: "arrow.clockwise").font(.headline)
                Picker("后台间隔", selection: $store.interval) {
                    ForEach([5, 15, 30], id: \.self) { Text("\($0) 分钟").tag($0) }
                }
                .pickerStyle(.segmented)
            }
            .frame(maxWidth: 440, alignment: .leading)

            Divider()
            VStack(alignment: .leading, spacing: 12) {
                Label("低额度通知", systemImage: "bell").font(.headline)
                Toggle("剩余额度过低时通知", isOn: Binding(get: { store.notifications }, set: store.setNotifications))
                    .toggleStyle(.switch)
                if store.notifications {
                    Stepper(value: $store.threshold, in: 5...50, step: 5) {
                        Text("剩余 \(Int(store.threshold))% 时提醒")
                    }
                }
            }
            .frame(maxWidth: 440, alignment: .leading)

            Divider()
            VStack(alignment: .leading, spacing: 12) {
                Label("重置前余量提醒", systemImage: "clock.badge.exclamationmark").font(.headline)
                Toggle("周额度快重置时提醒", isOn: Binding(get: { store.weeklyReminders }, set: store.setWeeklyReminders))
                    .toggleStyle(.switch)
                Stepper("提前 \(store.reminderDays) 天", value: $store.reminderDays, in: 1...7)
                HStack {
                    Slider(value: Binding(get: { Double(store.reminderThreshold) },
                                          set: { store.reminderThreshold = Int($0.rounded()) }), in: 0...99) {
                        Text("剩余超过")
                    }
                    .accessibilityValue("\(store.reminderThreshold)%")
                    Text("\(store.reminderThreshold)%").monospacedDigit()
                        .frame(width: 38, alignment: .trailing)
                }
                Text("刷新时检查，每项周额度每个周期提醒一次。")
                    .font(.caption).foregroundStyle(.secondary)
                if let message = store.reminderPermissionMessage {
                    Text(message).font(.callout).foregroundStyle(.secondary)
                }
            }
            .frame(maxWidth: 440, alignment: .leading)

            Divider()
            VStack(alignment: .leading, spacing: 6) {
                Label("桌面小组件", systemImage: "rectangle.on.rectangle").font(.headline)
                Text(store.sharedStorageAvailable ? "额度共享已就绪" : "当前构建的签名未启用额度共享。")
                    .foregroundStyle(.secondary)
                if let error = store.storageError { Text(error).foregroundStyle(.secondary) }
            }
        }
    }
}

private struct QuotaDraft: Identifiable {
    let id = UUID()
    let feed: Feed
    let original: Quota?
    var name: String
    var kind: Kind
    var value: String
    var unit: String
    var includeReset: Bool
    var reset: Date

    enum Kind: String, CaseIterable, Identifiable {
        case resetOnly = "仅重置时间"
        case used = "已用百分比"
        case remaining = "剩余次数"
        var id: String { rawValue }
    }

    init(feed: Feed, quota: Quota? = nil) {
        self.feed = feed
        original = quota
        name = quota?.displayName ?? (feed == .grok ? "本周共享额度" : "")
        kind = quota?.usedPercent != nil ? .used : (quota?.remainingCount != nil ? .remaining : (feed == .grok && quota == nil ? .used : .resetOnly))
        value = quota?.usedPercent.map { $0.formatted(.number.grouping(.never)) }
            ?? quota?.remainingCount.map(String.init) ?? ""
        unit = quota?.unit ?? "次"
        includeReset = quota?.resetsAt != nil
        reset = quota?.resetsAt ?? .now
    }

    var reading: Quota? {
        let title = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !title.isEmpty else { return nil }
        let input = value.trimmingCharacters(in: .whitespacesAndNewlines)
        let percent = Double(input) ?? (try? Double(input, format: .number))
        let count = Int(input)
        let resetTime = kind == .resetOnly || includeReset ? reset : nil
        if kind == .used, percent.map({ (0...100).contains($0) }) != true { return nil }
        if kind == .remaining, count.map({ $0 >= 0 }) != true { return nil }
        let now = Date.now
        return Quota(service: feed.service, feed: feed, accountRef: "manual-\(feed.rawValue)",
                     scopeId: original?.scopeId ?? id.uuidString, windowId: "record",
                     displayName: title, usedPercent: kind == .used ? percent : nil,
                     remainingCount: kind == .remaining ? count : nil,
                     unit: unit.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? "次" : unit,
                     resetsAt: resetTime, source: .manual, lastSuccessAt: now, lastAttemptAt: now)
    }
}

private struct ManualEditor: View {
    @ViewState var draft: QuotaDraft
    let save: (Quota) -> Void
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(alignment: .leading, spacing: 17) {
            Text("记录\(draft.feed.name)额度").font(.title2.bold())
            Form {
                TextField("额度范围", text: $draft.name, prompt: Text("例如 GPT-6 Pro 周额度"))
                Picker("读数类型", selection: $draft.kind) {
                    ForEach(QuotaDraft.Kind.allCases) { Text($0.rawValue).tag($0) }
                }
                if draft.kind == .used {
                    TextField("已用百分比", text: $draft.value, prompt: Text("0–100"))
                }
                if draft.kind == .remaining {
                    TextField("剩余次数", text: $draft.value, prompt: Text("例如 12"))
                    TextField("单位", text: $draft.unit)
                }
                if draft.kind != .resetOnly {
                    Toggle("记录重置时间", isOn: $draft.includeReset)
                }
                if draft.kind == .resetOnly || draft.includeReset {
                    DatePicker("重置时间", selection: $draft.reset, displayedComponents: [.date, .hourAndMinute])
                }
            }
            HStack {
                Spacer()
                Button("取消", role: .cancel) { dismiss() }.keyboardShortcut(.cancelAction)
                Button("保存") {
                    if let quota = draft.reading { save(quota); dismiss() }
                }
                .keyboardShortcut(.defaultAction)
                .disabled(draft.reading == nil)
            }
        }
        .padding(22)
        .frame(width: 405)
    }
}
