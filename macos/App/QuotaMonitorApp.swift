import SwiftUI
import UserNotifications
import CoreServices

@main
enum QuotaMonitorEntry {
    @MainActor static func main() {
        // Ignore native-messaging launches left over from the retired browser integration.
        if CommandLine.arguments.dropFirst().first?.hasPrefix("chrome-extension://") == true { return }
        QuotaMonitorApp.main()
    }
}

struct QuotaMonitorApp: App {
    static let overviewWindowID = "overview-window"
    @NSApplicationDelegateAdaptor(DashboardDockController.self) private var dockController

private enum MenuGaugeIcon {
        private static func symbol(_ percent: Int) -> NSImage? {
            NSImage(systemSymbolName: "gauge.with.dots.needle.\(percent)percent", accessibilityDescription: nil)?
                .withSymbolConfiguration(.init(pointSize: 18, weight: .medium))
        }

        private static let upright = symbol(50)
        private static let angled = symbol(33)

        private static let needleImage: CGImage? = {
            guard let upright else { return nil }
            // Pre-render before rotation, as AppKit can sample a rotated symbol at 1x.
            var rect = CGRect(origin: .zero, size: upright.size).applying(CGAffineTransform(scaleX: 8, y: 8))
            let template = NSImage(size: upright.size, flipped: false) { bounds in
                upright.draw(in: bounds)
                return true
            }
            return template.cgImage(forProposedRect: &rect, context: nil, hints: nil)
        }()

        static func image(percent: Int?) -> NSImage? {
            guard let upright, let angled, let needleImage else { return nil }
            let native = percent.flatMap { [0, 33, 50, 67, 100].contains($0) ? symbol($0) : nil }
            let image = NSImage(size: upright.size, flipped: false) { bounds in
                if let native {
                    native.draw(in: bounds)
                    return true
                }
                guard let context = NSGraphicsContext.current?.cgContext else { return false }
                // Work in the original symbol's coordinate system so its ring, ticks and
                // tapered needle retain the same proportions at every backing scale.
                context.saveGState()
                defer { context.restoreGState() }
                context.translateBy(x: bounds.minX, y: bounds.minY)
                let side = bounds.width
                func rect(_ x: CGFloat, _ y: CGFloat, _ w: CGFloat, _ h: CGFloat) -> CGRect {
                    CGRect(x: x * side, y: y * side, width: w * side, height: h * side)
                }
                let face = rect(0, 0, 1, 1)
                let pivot = CGPoint(x: 0.485 * side, y: 0.485 * side)
                let needleBounds = rect(0.40, 0.35, 0.17, 0.40)
                let topTick = rect(0.435, 0.66, 0.10, 0.10)
                let rotation = percent.map { CGFloat(50 - min(100, max(0, $0))) * .pi * 1.5 / 100 }

                context.saveGState()
                if let rotation {
                    // The native dial omits the dot underneath the tip.
                    let step = CGFloat.pi / 4
                    let nearestTick = (rotation / step).rounded() * step
                    if abs(rotation - nearestTick) < step / 3 {
                        let x = 0.485 - 0.224 * sin(nearestTick)
                        let y = 0.485 + 0.224 * cos(nearestTick)
                        context.addRect(face)
                        context.addEllipse(in: rect(x - 0.048, y - 0.048, 0.096, 0.096))
                        context.clip(using: .evenOdd)
                    }
                }
                context.saveGState()
                context.addRect(face)
                context.addRect(needleBounds)
                context.clip(using: .evenOdd)
                upright.draw(in: face)
                context.restoreGState()
                // The upright symbol has no top dot. Borrow it from the angled symbol.
                context.clip(to: topTick)
                angled.draw(in: face)
                context.restoreGState()

                if let rotation {
                    context.translateBy(x: pivot.x, y: pivot.y)
                    context.rotate(by: rotation)
                    context.translateBy(x: -pivot.x, y: -pivot.y)
                    context.clip(to: needleBounds)
                    context.interpolationQuality = .high
                    context.draw(needleImage, in: face)
                }
                return true
            }
            image.isTemplate = true
            return image
        }
    }

    @StateObject private var store = QuotaStore()

    var body: some Scene {
        // Start monitoring even when a login launch creates no dashboard or menu content.
        let store = self.store
        WindowGroup("额度监控器", id: Self.overviewWindowID) {
            DashboardView(store: store)
                .handlesExternalEvents(preferring: ["*"], allowing: ["*"])
                .onOpenURL { url in
                    dockController.requestDashboard()
                    store.open(url)
                    NSApp.activate(ignoringOtherApps: true)
                }
        }
        .handlesExternalEvents(matching: ["*"])
        .windowStyle(.titleBar)
        .defaultSize(width: 900, height: 600)
        .commands { CommandGroup(replacing: .newItem) {} }

        MenuBarExtra {
            MenuView(store: store, requestDashboard: dockController.requestDashboard)
        } label: {
            let reading = store.menuGaugePercent.map { "剩余 \($0)%" } ?? "暂无读数"
            Label {
                Text("额度监控器 · \(store.menuGaugeSource.title) · \(reading)")
            } icon: {
                if let icon = MenuGaugeIcon.image(percent: store.menuGaugePercent) {
                    Image(nsImage: icon)
                } else {
                    Image(systemName: "gauge.with.dots.needle.33percent")
                }
            }
        }
        .menuBarExtraStyle(.window)
    }
}

extension NSWindow {
    var isQuotaDashboard: Bool {
        identifier?.rawValue.hasPrefix(QuotaMonitorApp.overviewWindowID) == true
    }
}

@MainActor
private final class DashboardDockController: NSObject, NSApplicationDelegate, UNUserNotificationCenterDelegate {
    private var openDashboards = Set<ObjectIdentifier>()
    private var menuOnlyLaunch = false

    private func recordLaunchContext() {
        if let event = NSAppleEventManager.shared().currentAppleEvent, event.eventID == kAEOpenApplication,
           event.paramDescriptor(forKeyword: keyAEPropData)?.enumCodeValue == keyAELaunchedAsLogInItem {
            menuOnlyLaunch = true
        }
    }

    func requestDashboard() { menuOnlyLaunch = false }

    func applicationShouldOpenUntitledFile(_ sender: NSApplication) -> Bool {
        recordLaunchContext()
        return !menuOnlyLaunch
    }

    func applicationWillFinishLaunching(_ notification: Notification) {
        UNUserNotificationCenter.current().delegate = self
        let center = NotificationCenter.default
        center.addObserver(self, selector: #selector(windowOpened(_:)),
                           name: NSWindow.didBecomeKeyNotification, object: nil)
        center.addObserver(self, selector: #selector(windowClosed(_:)),
                           name: NSWindow.willCloseNotification, object: nil)
    }

    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter,
                                             willPresent notification: UNNotification) async -> UNNotificationPresentationOptions {
        [.banner, .list, .sound]
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        recordLaunchContext()
        for window in NSApp.windows where window.isQuotaDashboard && (window.isVisible || window.isMiniaturized) {
            dashboardOpened(window)
        }
    }

    @objc private func windowOpened(_ notification: Notification) {
        guard let window = notification.object as? NSWindow, window.isQuotaDashboard else { return }
        dashboardOpened(window)
    }

    private func dashboardOpened(_ window: NSWindow) {
        recordLaunchContext()
        if menuOnlyLaunch {
            window.orderOut(nil)
            DispatchQueue.main.async { [weak window] in
                if self.menuOnlyLaunch { window?.close() }
            }
            return
        }
        openDashboards.insert(ObjectIdentifier(window))
        if NSApp.activationPolicy() != .regular { NSApp.setActivationPolicy(.regular) }
    }

    @objc private func windowClosed(_ notification: Notification) {
        guard let window = notification.object as? NSWindow,
              openDashboards.remove(ObjectIdentifier(window)) != nil else { return }
        // Let AppKit finish closing the window before changing the activation policy.
        DispatchQueue.main.async {
            if self.openDashboards.isEmpty && NSApp.activationPolicy() == .regular {
                NSApp.setActivationPolicy(.accessory)
            }
        }
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        requestDashboard()
        guard let window = sender.windows.first(where: { $0.isQuotaDashboard && ($0.isVisible || $0.isMiniaturized) }) else {
            return true
        }
        if window.isMiniaturized { window.deminiaturize(nil) }
        window.makeKeyAndOrderFront(nil)
        return false
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }
}

struct MenuView: View {
    @ObservedObject var store: QuotaStore
    let requestDashboard: () -> Void
    @Environment(\.openWindow) private var openWindow

    private var lastRefreshAt: Date? {
        QuotaSummaryItem.items.compactMap { item -> Date? in
            guard let reading = item.reading(in: store.snapshot), reading.source == .official else { return nil }
            return reading.lastSuccessAt
        }.max()
    }

    var body: some View {
        TimelineView(.everyMinute) { context in
            VStack(alignment: .leading, spacing: 0) {
                HStack {
                    Label("额度速览", systemImage: "gauge.with.dots.needle.33percent")
                        .font(.title3.bold())
                    Spacer()
                    RefreshButton(store: store, now: context.date, compact: true, lastRefreshAt: lastRefreshAt)
                }
                .fixedSize(horizontal: false, vertical: true)
                .padding(.bottom, 10)

                Divider()
                ViewThatFits(in: .vertical) {
                    quotaRows(at: context.date).fixedSize(horizontal: false, vertical: true)
                    ScrollView(.vertical) { quotaRows(at: context.date) }
                        .scrollBounceBehavior(.basedOnSize)
                }
                Divider()

                HStack {
                    Button("打开主窗口", systemImage: "arrow.up.right") { open(.overview) }
                    Spacer()
                    Button("设置", systemImage: "gearshape") { open(.settings) }
                        .labelStyle(.iconOnly)
                    Button("退出额度监控器", systemImage: "power") { NSApp.terminate(nil) }
                        .labelStyle(.iconOnly)
                }
                .buttonStyle(.borderless)
                .fixedSize(horizontal: false, vertical: true)
                .padding(.top, 10)
                .layoutPriority(1)
            }
            .padding(EdgeInsets(top: 12, leading: 16, bottom: 16, trailing: 16))
            .frame(width: 360)
            .frame(maxHeight: panelHeightLimit)
            // Report the capped content height even when the menu window first proposes zero.
            .fixedSize(horizontal: false, vertical: true)
            .onAppear { store.refresh(.visible, feed: .antigravity) }
        }
    }

    private var panelHeightLimit: CGFloat {
        let screen = NSScreen.screens.first { $0.frame.contains(NSEvent.mouseLocation) } ?? NSScreen.main
        return min(560, (screen?.visibleFrame.height ?? 600) - 24)
    }

    private func quotaRows(at now: Date) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            ForEach(QuotaSummaryItem.items, id: \.self) { item in
                if item != QuotaSummaryItem.items.first { Divider() }
                let service = item.service
                let quota = item.reading(in: store.snapshot)
                Button {
                    show(service.primaryFeed, quota: quota)
                } label: {
                    HStack(alignment: .top, spacing: 12) {
                        ServiceTitle(item: item, quota: quota, compact: true)
                            .frame(width: 130, alignment: .leading)
                        VStack(alignment: .leading, spacing: 6) {
                            if let quota {
                                ReadingView(quota: quota, now: now, size: 25, compact: true)
                            } else if store.enabled(service.primaryFeed) {
                                Text("暂无读数").foregroundStyle(.secondary)
                            } else {
                                Label("设置来源", systemImage: "plus.circle").foregroundStyle(.secondary)
                            }
                            if service == .codex, store.codexEnabled {
                                CodexResetSummary(credits: store.snapshot.codexResetCredits)
                            }
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.vertical, 12)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityHint("打开额度详情")
            }
        }
    }

    private func show(_ feed: Feed, quota: Quota?) {
        open(QuotaStore.Screen(rawValue: feed.rawValue) ?? .overview, quota: quota)
    }

    private func open(_ screen: QuotaStore.Screen, quota: Quota? = nil) {
        requestDashboard()
        store.show(screen, quota: quota)
        if let window = NSApp.windows.first(where: {
            $0.isQuotaDashboard && ($0.isVisible || $0.isMiniaturized)
        }) {
            if window.isMiniaturized { window.deminiaturize(nil) }
            window.makeKeyAndOrderFront(nil)
        } else {
            openWindow(id: QuotaMonitorApp.overviewWindowID)
        }
        NSApp.activate(ignoringOtherApps: true)
    }
}

struct CodexResetSummary: View {
    let credits: CodexResetCredits?

    var body: some View {
        VStack(alignment: .leading, spacing: 3) {
            if let credits {
                let count = credits.availableCount.formatted()
                Label("可用重置 \(count) 次", systemImage: "arrow.counterclockwise")
                if let expiration = credits.nextExpiryLabel { Text(expiration) }
                if let status = credits.statusLabel { Text(status) }
            } else {
                Label("可用重置次数暂未提供", systemImage: "arrow.counterclockwise")
            }
        }
        .font(.caption)
        .foregroundStyle(.secondary)
        .accessibilityElement(children: .combine)
    }
}

struct RefreshButton: View {
    @ObservedObject var store: QuotaStore
    var feed: Feed?
    let now: Date
    var compact = false
    var lastRefreshAt: Date?

    private var sources: [Feed] { (feed.map { [$0] } ?? Feed.allCases.filter(\.isMonitored)).filter(store.enabled) }
    private var loading: Bool { sources.contains(where: store.isRefreshing) }
    private var ready: Bool { sources.contains { store.canRefresh($0, at: now) } }

    private var feedback: String? {
        if loading { return "正在刷新…" }
        if !store.online { return "当前离线" }
        let failed = sources.filter { store.message($0) != nil }
        if let first = failed.first, let retry = store.nextRetry(first), retry > now {
            return "\(first.name) 可于 \(retry.formatted(.dateTime.hour().minute())) 重试"
        }
        if !failed.isEmpty {
            return failed.map(\.name).joined(separator: "、") + "读取失败"
        }
        if feed != nil, let latest = store.snapshot.quotas.filter({ $0.feed == feed && $0.source == .official }).map(\.lastSuccessAt).max() {
            return "更新于 \(latest.formatted(.dateTime.hour().minute()))"
        }
        return nil
    }

    private var lastRefreshLabel: String? {
        guard let lastRefreshAt else { return nil }
        let format = Calendar.current.isDate(lastRefreshAt, inSameDayAs: now)
            ? Date.FormatStyle.dateTime.hour().minute()
            : .dateTime.month().day().hour().minute()
        return "刷新于 \(lastRefreshAt.formatted(format))"
    }

    var body: some View {
        if !sources.isEmpty {
            let lastRefreshLabel = lastRefreshLabel
            let feedback = feedback
            let loading = loading
            HStack(spacing: 8) {
                if lastRefreshLabel != nil || feedback != nil {
                    VStack(alignment: .trailing, spacing: 2) {
                        if let lastRefreshLabel {
                            Text(lastRefreshLabel).help("速览中最近一次成功读取额度的时间")
                        }
                        if let feedback { Text(feedback) }
                    }
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                }
                Button {
                    store.refresh(.manual, feed: feed)
                } label: {
                    if compact {
                        Image(systemName: "arrow.clockwise")
                            .opacity(loading ? 0 : 1)
                            .overlay {
                                if loading { ProgressView().controlSize(.mini) }
                            }
                    } else if loading {
                        ProgressView().controlSize(.small)
                            .frame(width: 17, height: 17)
                    } else {
                        Label("刷新", systemImage: "arrow.clockwise")
                    }
                }
                .buttonStyle(.bordered)
                .controlSize(.small)
                .disabled(!ready)
                .accessibilityLabel(feed.map { "刷新\($0.name)额度" } ?? "刷新所有已连接额度")
                .accessibilityValue(loading ? "正在刷新" : "")
                .help(feed.map { "刷新\($0.name)额度" } ?? "刷新所有已连接额度")
            }
            .frame(height: compact ? 30 : nil)
        }
    }
}

struct ServiceTitle: View {
    let item: QuotaSummaryItem
    let quota: Quota?
    var compact = false

    var body: some View {
        HStack(alignment: .top, spacing: 9) {
            ServiceIcon(service: item.service, group: item.antigravityGroup, size: 24)
            VStack(alignment: .leading, spacing: 3) {
                Text(item.title).font(.headline)
                if let subtitle = item.subtitle(for: quota) {
                    Group {
                        if compact, item.antigravityGroup == .partners {
                            ViewThatFits(in: .horizontal) {
                                Text(subtitle).fixedSize()
                                Text("共享周额度")
                            }
                            .help(subtitle)
                        } else {
                            Text(subtitle).lineLimit(2)
                        }
                    }
                    .font(.caption).foregroundStyle(.secondary)
                }
            }
        }
        .accessibilityElement(children: .combine)
    }
}

struct ReadingView: View {
    let quota: Quota
    let now: Date
    let size: Double
    var compact = false

    var body: some View {
        let (number, meaning) = quota.numberAndMeaning(at: now)
        VStack(alignment: .leading, spacing: compact ? 4 : 7) {
            ViewThatFits(in: .horizontal) {
                HStack(alignment: .firstTextBaseline, spacing: 5) {
                    figure(number)
                    Text(meaning).font(.caption).foregroundStyle(.secondary)
                }
                VStack(alignment: .leading, spacing: 1) {
                    figure(number)
                    Text(meaning).font(.caption).foregroundStyle(.secondary)
                }
            }
            if let percent = quota.remainingPercent {
                ProgressView(value: percent, total: 100)
                    .progressViewStyle(.linear)
                    .tint(.blue)
                    .accessibilityHidden(true)
            }
            if let reset = quota.resetLabel(at: now) {
                Label(reset + (quota.waitingForReset(at: now) ? "" : " 重置"), systemImage: "clock")
                    .font(.caption).foregroundStyle(.secondary)
            }
            if let status = quota.statusLabel(at: now) {
                Label(status, systemImage: "exclamationmark.circle").font(.caption).foregroundStyle(.secondary)
            }
            if quota.source == .manual || quota.status != .current || quota.waitingForReset(at: now) {
                Text("\(quota.source == .manual ? "手动记录" : "上次读取") · \(quota.lastSuccessAt.formatted(.dateTime.month().day().hour().minute()))")
                    .font(.caption2).foregroundStyle(.secondary)
            }
        }
        .accessibilityElement(children: .combine)
    }

    private func figure(_ value: String) -> some View {
        Text(value)
            .font(.system(size: size, weight: .semibold)).monospacedDigit()
            .lineLimit(1).minimumScaleFactor(compact && quota.remainingPercent != nil ? 1 : 0.75)
    }
}
