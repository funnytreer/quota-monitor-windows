import AppKit
import Network
import SwiftUI
import UserNotifications
import WidgetKit

@MainActor
final class QuotaStore: ObservableObject {
    @Published private(set) var snapshot = Snapshot()
    @Published private(set) var refreshing = false
    @Published private(set) var codexEnabled = false
    @Published private(set) var codexMessage: String?
    @Published private(set) var retryAt: Date?
    @Published private(set) var grokEnabled = false
    @Published private(set) var grokRefreshing = false
    @Published private(set) var grokMessage: String?
    @Published private(set) var grokRetryAt: Date?
    @Published var grokLoginPresented = false
    @Published private(set) var antigravityEnabled = false
    @Published private(set) var antigravityRefreshing = false
    @Published private(set) var antigravityMessage: String?
    private let antigravityClient = AntigravityClient()
    private var antigravitySchedule = RefreshSchedule()
    private var antigravityTask: Task<Void, Never>?
    private var antigravityHandledResets: [Quota.Identity: Date] = [:]
    @Published private(set) var online = true
    @Published private(set) var sharedStorageAvailable = false
    @Published private(set) var storageError: String?
    @Published var selection: Screen? = .overview
    @Published var selectedQuota: String?
    @Published private(set) var menuGaugePercent: Int?
    @Published var menuGaugeSource = MenuGaugeSource.average {
        didSet {
            defaults.set(menuGaugeSource.rawValue, forKey: "menuGaugeSource")
            updateMenuGauge()
        }
    }
    @Published var interval = 15 {
        didSet {
            defaults.set(interval, forKey: "interval")
            schedule.interval = Double(interval * 60)
            grokSchedule.interval = schedule.interval
            antigravitySchedule.interval = schedule.interval
        }
    }
    @Published var notifications = false {
        didSet { defaults.set(notifications, forKey: "notifications") }
    }
    @Published var threshold = 20.0 {
        didSet { defaults.set(threshold, forKey: "threshold") }
    }
    @Published private(set) var weeklyReminders = false {
        didSet { defaults.set(weeklyReminders, forKey: "weeklyReminders") }
    }
    @Published var reminderDays = 2 {
        didSet { defaults.set(reminderDays, forKey: "reminderDays") }
    }
    @Published var reminderThreshold = 50 {
        didSet { defaults.set(reminderThreshold, forKey: "reminderThreshold") }
    }
    @Published private(set) var reminderPermissionMessage: String?

    enum Screen: String, CaseIterable, Identifiable {
        case overview, codex, grok, antigravity, settings
        var id: String { rawValue }
        var feed: Feed? { Feed(rawValue: rawValue) }
        var title: String {
            switch self {
            case .overview: "额度总览"
            case .settings: "设置"
            default: feed?.name ?? ""
            }
        }
    }

    private let defaults = UserDefaults.standard
    private let client = CodexClient()
    private var grokProfile = UUID()
    private var grokAccount = UUID()
    private(set) lazy var grokClient = GrokWebClient(profile: grokProfile)
    private var grokSchedule = RefreshSchedule()
    private var grokTask: Task<Void, Never>?
    private var grokRequest: UUID?
    private var grokHandledReset: Date?
    private let monitor = NWPathMonitor()
    private var wakeObserver: NSObjectProtocol?
    private var timer: Timer?
    private var schedule = RefreshSchedule()
    private var history = NotificationHistory()
    private var weeklyHistory = WeeklyReminderHistory()
    private var pendingWeeklies = Set<String>()
    private var account: String?
    private var generation = UUID()
    private var refreshID: UUID?
    private var handledResets: [Quota.Identity: Date] = [:]
    private var asleep = false
    private var sleepObserver: NSObjectProtocol?
    private var needsAccountRefresh = false
    private var sharedURL: URL?
    private var localURL: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("QuotaMonitor/snapshot.json")
    }

    init() {
        menuGaugeSource = defaults.string(forKey: "menuGaugeSource").flatMap(MenuGaugeSource.init(rawValue:)) ?? .average
        interval = [5, 15, 30].contains(defaults.integer(forKey: "interval")) ? defaults.integer(forKey: "interval") : 15
        schedule.interval = Double(interval * 60)
        grokSchedule.interval = schedule.interval
        antigravitySchedule.interval = schedule.interval
        antigravityEnabled = defaults.object(forKey: "antigravityEnabled") as? Bool ?? (AntigravityClient.executable != nil)
        grokEnabled = defaults.bool(forKey: "grokEnabled")
        grokProfile = defaults.string(forKey: "grokProfile").flatMap(UUID.init(uuidString:)) ?? UUID()
        grokAccount = defaults.string(forKey: "grokAccount").flatMap(UUID.init(uuidString:)) ?? UUID()
        defaults.set(grokProfile.uuidString, forKey: "grokProfile")
        defaults.set(grokAccount.uuidString, forKey: "grokAccount")
        notifications = defaults.bool(forKey: "notifications")
        threshold = (defaults.object(forKey: "threshold") as? Double) ?? 20
        weeklyReminders = defaults.bool(forKey: "weeklyReminders")
        reminderDays = min(7, max(1, defaults.object(forKey: "reminderDays") as? Int ?? 2))
        reminderThreshold = min(99, max(0, defaults.object(forKey: "reminderThreshold") as? Int ?? 50))
        if let data = defaults.data(forKey: "weeklyReminderHistory"),
           let saved = try? JSONDecoder().decode(WeeklyReminderHistory.self, from: data) { weeklyHistory = saved }
        codexEnabled = defaults.bool(forKey: "codexEnabled")
        sharedURL = SnapshotFile.sharedURL
        sharedStorageAvailable = sharedURL != nil
        snapshot = SnapshotFile.read(at: localURL) ?? SnapshotFile.read(at: sharedURL) ?? Snapshot()
        defaults.removeObject(forKey: "geminiEnabled")
        if !codexEnabled {
            snapshot.quotas.removeAll { $0.feed == .codex && $0.source == .official }
            snapshot.codexResetCredits = nil
        }
        if !grokEnabled { snapshot.quotas.removeAll { $0.feed == .grok && $0.source == .official } }
        if let quota = snapshot.quotas.first(where: { $0.feed == .grok && $0.source == .official }) {
            grokSchedule.lastSuccess = quota.lastSuccessAt
            if quota.status == .loginRequired {
                grokSchedule.loginRequired = true
                grokMessage = GrokFailure.loginRequired.message
            }
        }
        if antigravityEnabled {
            antigravitySchedule.lastSuccess = snapshot.quotas.filter { $0.feed == .antigravity }.map(\.lastSuccessAt).max()
        } else {
            snapshot.quotas.removeAll { $0.feed == .antigravity }
        }
        snapshot.quotas.removeAll { $0.feed == .legacyGeminiWeb }
        snapshot.normalizePins()
        if let data = defaults.data(forKey: "notificationHistory"),
           let saved = try? JSONDecoder().decode(NotificationHistory.self, from: data) { history = saved }
        let codexQuotas = snapshot.quotas.filter { $0.source == .official && $0.feed == .codex }
        account = codexQuotas.first?.accountRef ?? snapshot.codexResetCredits?.accountRef
        schedule.lastSuccess = snapshot.codexResetCredits?.lastSuccessAt
        if codexEnabled, codexQuotas.contains(where: { $0.status == .loginRequired }) ||
            snapshot.codexResetCredits?.status == .loginRequired {
            schedule.loginRequired = true
            codexMessage = CodexFailure.loginRequired.message
        }
        persist()
        client.didUpdate = { [weak self] payload, date in self?.receive(payload, at: date) }
        client.accountDidChange = { [weak self] in self?.accountChanged() }
        monitor.pathUpdateHandler = { [weak self] path in
            let isOnline = path.status == .satisfied
            DispatchQueue.main.async { [weak self] in
                guard let self else { return }
                let wasOnline = self.online
                self.online = isOnline
                if isOnline, !wasOnline { self.resume() }
            }
        }
        monitor.start(queue: DispatchQueue(label: "QuotaMonitor.network"))
        let center = NSWorkspace.shared.notificationCenter
        wakeObserver = center.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.asleep = false; self?.resume() }
        }
        sleepObserver = center.addObserver(forName: NSWorkspace.willSleepNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.pause() }
        }
        timer = Timer.scheduledTimer(withTimeInterval: 15, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.tick() }
        }
        Task { tick() }
    }

    private func pause() {
        asleep = true
        generation = UUID()
        refreshID = nil
        refreshing = false
        client.close()
        stopGrokRefresh()
        stopAntigravity()
    }

    private func resume() {
        guard !asleep, online else { return }
        tick()
    }

    private func tick() {
        updateMenuGauge()
        guard online, !asleep else { return }
        tickGrok()
        refreshAntigravity(.background)
        guard codexEnabled, !refreshing else { return }
        let now = Date.now
        let due = snapshot.quotas.filter { quota in
            guard quota.feed == .codex, quota.source == .official, let reset = quota.resetsAt else { return false }
            return reset <= now && handledResets[quota.id] != reset
        }
        if !due.isEmpty, schedule.shouldRead(.reset, at: now) {
            for quota in due { handledResets[quota.id] = quota.resetsAt }
            refreshCodex(.reset)
        } else {
            refreshCodex(.background)
        }
    }

    func show(_ screen: Screen, quota: Quota? = nil) {
        selection = screen
        selectedQuota = quota?.widgetKey
        refreshVisible(screen)
    }

    func open(_ url: URL) {
        guard let (feed, key) = QuotaRoute.decode(url), let screen = Screen(rawValue: feed.rawValue) else { return }
        selection = screen
        selectedQuota = key
        refreshVisible(screen)
    }

    func connectCodex() {
        codexEnabled = true
        defaults.set(true, forKey: "codexEnabled")
        schedule.loginRequired = false
        codexMessage = nil
        refreshCodex(.manual)
    }

    func disconnect(_ feed: Feed) {
        snapshot.quotas.removeAll { $0.feed == feed }
        snapshot.normalizePins()
        if feed == .codex {
            codexEnabled = false
            snapshot.codexResetCredits = nil
            defaults.set(false, forKey: "codexEnabled")
            generation = UUID()
            refreshID = nil
            refreshing = false
            needsAccountRefresh = false
            account = nil
            schedule = RefreshSchedule(interval: Double(interval * 60))
            handledResets.removeAll()
            codexMessage = nil
            retryAt = nil
            client.close()
        }
        if feed == .grok {
            grokEnabled = false
            defaults.set(false, forKey: "grokEnabled")
            stopGrokRefresh()
            grokClient.endLogin()
            grokSchedule = RefreshSchedule(interval: Double(interval * 60))
            grokHandledReset = nil
            grokMessage = nil
            grokRetryAt = nil
            grokAccount = UUID()
            defaults.set(grokAccount.uuidString, forKey: "grokAccount")
        }
        if feed == .antigravity {
            antigravityEnabled = false
            defaults.set(false, forKey: "antigravityEnabled")
            stopAntigravity()
            antigravitySchedule = RefreshSchedule(interval: schedule.interval)
            antigravityHandledResets.removeAll()
            antigravityMessage = nil
        }
        pruneHistory()
        persist()
    }

    func refreshVisible(_ screen: Screen) {
        switch screen {
        case .overview: refresh(.visible)
        case .codex: refresh(.visible, feed: .codex)
        case .grok: refresh(.visible, feed: .grok)
        case .antigravity: refresh(.visible, feed: .antigravity)
        default: break
        }
    }

    func refresh(_ trigger: RefreshSchedule.Trigger, feed: Feed? = nil) {
        if feed == nil || feed == .codex { refreshCodex(trigger) }
        if feed == nil || feed == .grok { refreshGrok(trigger) }
        if feed == nil || feed == .antigravity { refreshAntigravity(trigger) }
    }

    func enabled(_ feed: Feed) -> Bool {
        switch feed {
        case .legacyChat, .legacyGeminiWeb: false
        case .codex: codexEnabled
        case .grok: grokEnabled
        case .antigravity: antigravityEnabled
        }
    }

    func isRefreshing(_ feed: Feed) -> Bool {
        switch feed {
        case .legacyChat, .legacyGeminiWeb: false
        case .codex: refreshing
        case .grok: grokRefreshing
        case .antigravity: antigravityRefreshing
        }
    }

    func message(_ feed: Feed) -> String? {
        switch feed {
        case .legacyChat, .legacyGeminiWeb: nil
        case .codex: codexMessage
        case .grok: grokMessage
        case .antigravity: antigravityMessage
        }
    }

    func nextRetry(_ feed: Feed) -> Date? {
        switch feed {
        case .legacyChat, .legacyGeminiWeb: nil
        case .codex: retryAt
        case .grok: grokRetryAt
        case .antigravity: antigravitySchedule.serverRetryAt
        }
    }

    func canRefresh(_ feed: Feed, at date: Date) -> Bool {
        guard online, !asleep, enabled(feed), !isRefreshing(feed) else { return false }
        switch feed {
        case .legacyChat, .legacyGeminiWeb: return false
        case .codex: return schedule.shouldRead(.manual, at: date)
        case .grok: return !grokLoginPresented && grokSchedule.shouldRead(.manual, at: date)
        case .antigravity: return true
        }
    }

    private func refreshCodex(_ trigger: RefreshSchedule.Trigger) {
        guard codexEnabled, !refreshing, online, !asleep, schedule.shouldRead(trigger, at: .now) else { return }
        refreshing = true
        codexMessage = nil
        let requestID = UUID()
        refreshID = requestID
        let start = Date.now
        let revision = generation
        Task {
            defer {
                if refreshID == requestID {
                    refreshing = false
                    refreshID = nil
                    if needsAccountRefresh {
                        needsAccountRefresh = false
                        refreshCodex(.manual)
                    }
                }
            }
            do {
                let result = try await client.refresh { observed in
                    guard generation == revision, codexEnabled else { return }
                    updateAccount(observed)
                }
                guard generation == revision, codexEnabled else { return }
                let now = Date.now
                let incoming = result.windows.quotas(account: result.account, at: now)
                let credits = result.windows.resetCredits(account: result.account, at: now)
                guard !incoming.isEmpty || credits != nil else { throw CodexFailure.noReading }
                snapshot.applyCodex(incoming, account: result.account, startedAt: start, complete: result.windows.isComplete)
                snapshot.applyCodexResetCredits(credits, account: result.account, startedAt: start)
                schedule.success(at: now)
                retryAt = nil
                let accepted = snapshot.quotas.filter { $0.source == .official && $0.lastSuccessAt == now }
                notify(accepted, at: now)
                persist()
            } catch {
                guard generation == revision, codexEnabled else { return }
                let failure = error as? CodexFailure ?? .connection
                fail(failure, startedAt: start)
            }
        }
    }

    private func updateAccount(_ observed: String) {
        guard account != observed else { return }
        account = observed
        snapshot.quotas.removeAll { $0.feed == .codex && $0.source == .official && $0.accountRef != observed }
        if snapshot.codexResetCredits?.accountRef != observed { snapshot.codexResetCredits = nil }
        snapshot.normalizePins()
        handledResets.removeAll()
        pruneHistory()
        persist()
    }

    private func accountChanged() {
        guard codexEnabled else { return }
        generation = UUID()
        account = nil
        snapshot.quotas.removeAll { $0.feed == .codex && $0.source == .official }
        snapshot.codexResetCredits = nil
        snapshot.normalizePins()
        schedule.loginRequired = false
        retryAt = schedule.serverRetryAt
        handledResets.removeAll()
        pruneHistory()
        persist()
        if refreshing {
            needsAccountRefresh = true
        } else {
            refreshCodex(.manual)
        }
    }

    private func receive(_ windows: CodexWindows, at date: Date) {
        guard let eventAccount = account, codexEnabled, !asleep else { return }
        let revision = generation
        Task {
            guard let observed = try? await client.readAccount(), generation == revision,
                  codexEnabled, account == eventAccount, !asleep else { return }
            guard observed == eventAccount else { accountChanged(); return }
            let incoming = windows.quotas(account: observed, at: date)
            let credits = windows.resetCredits(account: observed, at: date)
            guard !incoming.isEmpty || credits != nil else { return }
            snapshot.applyCodex(incoming, account: observed, startedAt: date, complete: false)
            if let credits { snapshot.applyCodexResetCredits(credits, account: observed, startedAt: date) }
            let accepted = snapshot.quotas.filter { $0.source == .official && $0.lastSuccessAt == date }
            if !accepted.isEmpty || credits != nil {
                codexMessage = nil
                notify(accepted, at: date)
                persist()
            }
        }
    }

    private func fail(_ failure: CodexFailure, startedAt: Date) {
        let now = Date.now
        var requiresLogin = false
        var serverDelay: TimeInterval?
        if case .loginRequired = failure { requiresLogin = true }
        if case .rateLimited(let wait) = failure { serverDelay = wait }
        schedule.failure(at: now, serverDelay: serverDelay, requiresLogin: requiresLogin)
        retryAt = schedule.serverRetryAt
        codexMessage = failure.message
        if requiresLogin { client.close() }
        for index in snapshot.quotas.indices where snapshot.quotas[index].feed == .codex &&
            snapshot.quotas[index].source == .official && snapshot.quotas[index].lastSuccessAt <= startedAt {
            snapshot.quotas[index].status = requiresLogin ? .loginRequired : .stale
            snapshot.quotas[index].lastAttemptAt = now
        }
        if let credits = snapshot.codexResetCredits, credits.lastSuccessAt <= startedAt {
            snapshot.codexResetCredits?.status = requiresLogin ? .loginRequired : .stale
        }
        persist()
    }

    func presentGrokLogin() {
        stopGrokRefresh()
        grokLoginPresented = true
    }

    func finishGrokLogin() {
        grokLoginPresented = false
        if grokSchedule.loginRequired {
            grokAccount = UUID()
            defaults.set(grokAccount.uuidString, forKey: "grokAccount")
        }
        grokEnabled = true
        defaults.set(true, forKey: "grokEnabled")
        grokSchedule.loginRequired = false
        refresh(.manual, feed: .grok)
    }

    private func tickGrok() {
        guard grokEnabled, !grokRefreshing, !grokLoginPresented else { return }
        let now = Date.now
        let reset = snapshot.quotas.first { $0.feed == .grok && $0.source == .official }?.resetsAt
        if let reset, reset <= now, reset != grokHandledReset, grokSchedule.shouldRead(.reset, at: now) {
            grokHandledReset = reset
            refreshGrok(.reset)
        } else {
            refreshGrok(.background)
        }
    }

    private func stopGrokRefresh() {
        grokRequest = nil
        grokTask?.cancel()
        if grokTask != nil { grokClient.cancelRefresh() }
        grokTask = nil
        grokRefreshing = false
    }

    private func refreshGrok(_ trigger: RefreshSchedule.Trigger) {
        guard grokEnabled, !grokRefreshing, !grokLoginPresented, online, !asleep,
              grokSchedule.shouldRead(trigger, at: .now) else { return }
        let request = UUID()
        grokRequest = request
        grokRefreshing = true
        grokMessage = nil
        grokTask = Task {
            defer {
                if grokRequest == request {
                    grokRequest = nil
                    grokTask = nil
                    grokRefreshing = false
                }
            }
            do {
                let result = try await grokClient.refresh()
                guard grokRequest == request, !Task.isCancelled else { return }
                let now = Date.now
                let quota = result.quota(account: grokAccount.uuidString, at: now)
                snapshot.applyGrok(quota)
                grokSchedule.success(at: now)
                grokRetryAt = nil
                pruneHistory()
                notify([quota], at: now)
                persist()
            } catch {
                guard grokRequest == request, !Task.isCancelled else { return }
                let failure = error as? GrokFailure ?? .connection
                var loginRequired = false
                var wait: TimeInterval?
                if case .loginRequired = failure { loginRequired = true }
                if case .rateLimited(let delay) = failure { wait = delay }
                let now = Date.now
                grokSchedule.failure(at: now, serverDelay: wait, requiresLogin: loginRequired)
                grokRetryAt = grokSchedule.serverRetryAt
                grokMessage = failure.message
                for index in snapshot.quotas.indices where snapshot.quotas[index].feed == .grok &&
                    snapshot.quotas[index].source == .official {
                    snapshot.quotas[index].status = loginRequired ? .loginRequired : .stale
                    snapshot.quotas[index].lastAttemptAt = now
                }
                persist()
            }
        }
    }

    func connectAntigravity() {
        antigravityEnabled = true
        defaults.set(true, forKey: "antigravityEnabled")
        refreshAntigravity(.manual)
    }

    private func stopAntigravity() {
        antigravityTask?.cancel()
        antigravityClient.stop()
        antigravityTask = nil
        antigravityRefreshing = false
    }

    private func refreshAntigravity(_ trigger: RefreshSchedule.Trigger) {
        guard antigravityEnabled, online, !asleep, antigravityTask == nil else { return }
        let now = Date.now
        let due = snapshot.quotas.filter { quota in
            guard quota.feed == .antigravity, let reset = quota.resetsAt else { return false }
            return reset <= now && antigravityHandledResets[quota.id] != reset
        }
        let reason: RefreshSchedule.Trigger = trigger == .background && !due.isEmpty ? .reset : trigger
        if reason == .manual { antigravitySchedule.loginRequired = false }
        guard antigravitySchedule.shouldRead(reason, at: now) else { return }
        for quota in due { antigravityHandledResets[quota.id] = quota.resetsAt }
        antigravityRefreshing = true
        antigravityMessage = nil
        antigravityTask = Task {
            defer {
                if !Task.isCancelled {
                    antigravityRefreshing = false
                    antigravityTask = nil
                }
            }
            do {
                let quotas = try await antigravityClient.read()
                guard !Task.isCancelled else { return }
                snapshot.applyAntigravity(quotas)
                antigravitySchedule.success(at: .now)
                notify(quotas.filter { $0.windowId == "weekly" }, at: .now)
            } catch {
                guard !Task.isCancelled else { return }
                let failure = error as? AntigravityFailure ?? .unavailable
                antigravitySchedule.failure(at: .now, requiresLogin: failure == .login)
                antigravityMessage = failure.message
                snapshot.markAntigravityUnavailable(failure == .login ? .loginRequired : .stale, at: .now)
            }
            persist()
        }
    }

    func saveManual(_ quota: Quota) {
        if let index = snapshot.quotas.firstIndex(where: { $0.id == quota.id }) {
            snapshot.quotas[index] = quota
        } else {
            snapshot.quotas.append(quota)
        }
        persist()
    }

    func deleteManual(_ quota: Quota) {
        guard quota.source == .manual else { return }
        snapshot.quotas.removeAll { $0.id == quota.id }
        snapshot.normalizePins()
        persist()
    }

    func pin(_ quota: Quota) {
        guard quota.service.supportsPinning else { return }
        snapshot.pins[quota.service] = quota.widgetKey
        persist()
    }

    func setNotifications(_ enabled: Bool) {
        if !enabled { notifications = false; return }
        Task {
            let center = UNUserNotificationCenter.current()
            notifications = (try? await center.requestAuthorization(options: [.alert, .sound])) ?? false
        }
    }

    func setWeeklyReminders(_ enabled: Bool) {
        reminderPermissionMessage = nil
        guard enabled else { weeklyReminders = false; return }
        Task {
            do {
                let allowed = try await UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound])
                weeklyReminders = allowed
                if allowed {
                    refresh(.visible)
                } else {
                    reminderPermissionMessage = "请在「系统设置 → 通知 → 额度监控器」中允许通知。"
                }
            } catch {
                reminderPermissionMessage = "暂时无法开启通知，请稍后重试。"
            }
        }
    }

    func login() {
        let workspace = NSWorkspace.shared
        let official = workspace.urlForApplication(withBundleIdentifier: "com.openai.codex")
            ?? workspace.urlForApplication(withBundleIdentifier: "com.openai.chat")
        workspace.open(official ?? Feed.codex.url)
    }

    private func notify(_ quotas: [Quota], at date: Date) {
        if weeklyReminders {
            for quota in quotas { notifyBeforeReset(quota, at: .now) }
        }
        guard notifications else { return }
        for quota in quotas where history.shouldNotify(quota, threshold: threshold, at: date) {
            let content = UNMutableNotificationContent()
            content.title = "\(quota.feed.name) 额度较低"
            content.body = "\(quota.title) · 剩余 \(quota.numberAndMeaning(at: date).0)"
            content.sound = .default
            let request = UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil)
            UNUserNotificationCenter.current().add(request)
        }
        defaults.set(try? JSONEncoder().encode(history), forKey: "notificationHistory")
    }

    private func notifyBeforeReset(_ quota: Quota, at now: Date) {
        let key = quota.widgetKey
        guard let reset = weeklyHistory.eligibleReset(for: quota, days: reminderDays, above: reminderThreshold, at: now),
              pendingWeeklies.insert(key).inserted else { return }

        let content = UNMutableNotificationContent()
        let name = quota.service == .grok ? quota.service.name : quota.displayName
        content.title = "\(name) 周额度即将重置"
        let percent = quota.numberAndMeaning(at: now).0
        let balance = percent == "\(reminderThreshold)%" ? "超过 \(percent)" : percent
        content.body = "本周还剩\(balance)，将于\(reset.formatted(.dateTime.month().day().hour().minute()))重置。"
        content.sound = .default
        let id = "weekly-reminder.\(key).\(Int(reset.timeIntervalSince1970))"
        let request = UNNotificationRequest(identifier: id, content: content, trigger: nil)
        UNUserNotificationCenter.current().add(request) { error in
            Task { @MainActor in
                self.pendingWeeklies.remove(key)
                if let error {
                    if (error as? UNError)?.code == .notificationsNotAllowed {
                        self.weeklyReminders = false
                        self.reminderPermissionMessage = "请在「系统设置 → 通知 → 额度监控器」中允许通知。"
                    }
                    return
                }
                self.weeklyHistory.recordDelivery(for: key, reset: reset, at: .now)
                self.defaults.set(try? JSONEncoder().encode(self.weeklyHistory), forKey: "weeklyReminderHistory")
            }
        }
    }

    private func pruneHistory() {
        history.keepOnly(Set(snapshot.quotas.map(\.widgetKey)))
        defaults.set(try? JSONEncoder().encode(history), forKey: "notificationHistory")
    }

    private func updateMenuGauge() {
        let percent = menuGaugeSource.percent(in: snapshot, at: .now)
        if percent != menuGaugePercent { menuGaugePercent = percent }
    }

    private func persist() {
        updateMenuGauge()
        do {
            try SnapshotFile.write(snapshot, at: localURL)
            storageError = nil
        } catch {
            storageError = "无法保存本地记录。"
        }
        guard let sharedURL else { return }
        if SnapshotFile.read(at: sharedURL) == snapshot {
            sharedStorageAvailable = true
            return
        }
        do {
            try SnapshotFile.write(snapshot, at: sharedURL)
            sharedStorageAvailable = true
            WidgetCenter.shared.reloadTimelines(ofKind: SnapshotFile.widgetKind)
        } catch {
            sharedStorageAvailable = false
        }
    }
}
