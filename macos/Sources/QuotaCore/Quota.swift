import Foundation

enum AntigravityGroup: String, CaseIterable {
    case gemini, partners

    var title: String { self == .gemini ? "Gemini" : "Claude / GPT" }
    var bucketPrefix: String { self == .gemini ? "gemini" : "3p" }
}

enum Service: String, Codable, Identifiable {
    // Preserve the stored identifier used by older snapshots and widget selections.
    case codex = "chatgpt"
    case grok, antigravity
    case legacyGeminiWeb = "gemini"

    var id: String { rawValue }
    var supportsPinning: Bool { self == .codex || self == .grok }
    var primaryFeed: Feed {
        switch self {
        case .codex: .codex
        case .grok: .grok
        case .legacyGeminiWeb: .legacyGeminiWeb
        case .antigravity: .antigravity
        }
    }
    var name: String { primaryFeed.name }
}

enum Feed: String, Codable, CaseIterable, Identifiable {
    case legacyChat = "chat"
    case codex, grok, antigravity
    case legacyGeminiWeb = "gemini"

    var id: String { rawValue }
    var isMonitored: Bool { self != .legacyChat && self != .legacyGeminiWeb }
    var service: Service {
        switch self {
        case .legacyChat, .codex: .codex
        case .grok: .grok
        case .legacyGeminiWeb: .legacyGeminiWeb
        case .antigravity: .antigravity
        }
    }
    var name: String {
        switch self {
        case .legacyChat: "ChatGPT 普通对话"
        case .codex: "Codex"
        case .grok: "Grok"
        case .legacyGeminiWeb: "Gemini 网页版"
        case .antigravity: "Antigravity"
        }
    }
    var url: URL {
        switch self {
        case .legacyChat: URL(string: "https://chatgpt.com/")!
        case .codex: URL(string: "https://chatgpt.com/codex/settings/usage")!
        case .grok: URL(string: "https://grok.com/?_s=usage")!
        case .legacyGeminiWeb: URL(string: "https://gemini.google.com/usage")!
        case .antigravity: URL(string: "https://antigravity.google/docs/cli/commands/usage")!
        }
    }
}

enum ReadingSource: String, Codable {
    case official, manual, example
    var name: String {
        switch self {
        case .official: "官方 · 自动读取"
        case .manual: "手动记录"
        case .example: "示例"
        }
    }
}

enum ReadStatus: String, Codable {
    case current, stale, loginRequired
}

struct Quota: Codable, Identifiable, Equatable {
    struct Identity: Hashable, Codable {
        let service: Service
        let account: String
        let scope: String
        let window: String
    }

    let service: Service
    let feed: Feed
    let accountRef: String
    let scopeId: String
    let windowId: String
    var displayName: String
    var durationMinutes: Int?
    var usedPercent: Double?
    var remainingCount: Int?
    var unit: String?
    var resetsAt: Date?
    var source: ReadingSource
    var lastSuccessAt: Date
    var lastAttemptAt: Date
    var status: ReadStatus = .current

    static func reportedReset(_ date: Date?) -> Date? {
        guard let date, date.timeIntervalSince1970 > 0 else { return nil }
        return date
    }

    var id: Identity { .init(service: service, account: accountRef, scope: scopeId, window: windowId) }
    var remainingPercent: Double? { usedPercent.map { min(100, max(0, 100 - $0)) } }
    var widgetKey: String {
        let data = try? JSONEncoder().encode([service.rawValue, accountRef, scopeId, windowId])
        return data?.base64EncodedString() ?? ""
    }
    var windowLabel: String? {
        guard let minutes = durationMinutes, minutes > 0 else { return nil }
        if minutes.isMultiple(of: 10_080) { return "\(minutes / 10_080) 周" }
        if minutes.isMultiple(of: 1_440) { return "\(minutes / 1_440) 天" }
        if minutes.isMultiple(of: 60) { return "\(minutes / 60) 小时" }
        return "\(minutes) 分钟"
    }
    var title: String {
        guard let windowLabel else { return displayName }
        return "\(displayName) · \(windowLabel)"
    }

    var summaryTitle: String { displayName == service.name ? (windowLabel ?? "额度") : title }

    func waitingForReset(at date: Date) -> Bool { resetsAt.map { $0 <= date } ?? false }
    func statusLabel(at date: Date) -> String? {
        if status == .loginRequired { return "待重新登录" }
        if waitingForReset(at: date) { return "重置待确认" }
        if status == .stale { return "上次记录" }
        return nil
    }
    func numberAndMeaning(at date: Date) -> (String, String) {
        if let remaining = remainingPercent {
            return (remaining.formatted(.number.precision(.fractionLength(0)).rounded(rule: .toNearestOrAwayFromZero)) + "%", "剩余")
        }
        if let remainingCount { return (remainingCount.formatted(), "剩余\(unit ?? "次")") }
        if let resetsAt {
            return (resetsAt.formatted(.dateTime.hour(.twoDigits(amPM: .omitted)).minute()),
                    waitingForReset(at: date) ? "待确认" : (Calendar.current.isDate(resetsAt, inSameDayAs: date) ? "今天重置" : "重置时间"))
        }
        return ("—", "暂无读数")
    }
    func resetLabel(at date: Date) -> String? {
        guard let resetsAt else { return nil }
        let time = resetsAt.formatted(.dateTime.hour(.twoDigits(amPM: .omitted)).minute())
        if Calendar.current.isDate(resetsAt, inSameDayAs: date) { return "今天 \(time)" }
        return resetsAt.formatted(.dateTime.month(.defaultDigits).day().hour().minute())
    }
}

struct CodexResetCredits: Codable, Equatable {
    struct Expiration: Codable, Equatable, Identifiable {
        let date: Date
        let count: Int
        var id: Date { date }
    }

    let accountRef: String
    let availableCount: Int
    let expirations: [Expiration]
    let lastSuccessAt: Date
    var status: ReadStatus = .current

    var countWithoutExpiry: Int { max(0, availableCount - expirations.reduce(0) { $0 + $1.count }) }
    var nextExpiryLabel: String? {
        guard let first = expirations.first else { return availableCount > 0 ? "到期时间未提供" : nil }
        let prefix = countWithoutExpiry > 0 ? "已知最早到期" : "最早到期"
        return "\(prefix) \(first.date.formatted(.dateTime.month().day().hour().minute()))"
    }

    var statusLabel: String? {
        switch status {
        case .current: nil
        case .stale: "上次读取"
        case .loginRequired: "待重新登录"
        }
    }
}

struct Snapshot: Codable, Equatable {
    var schemaVersion = 1
    var quotas: [Quota] = []
    var pins: [Service: String] = [:]
    var codexResetCredits: CodexResetCredits?

    var monitoredQuotas: [Quota] { quotas.filter { $0.feed.isMonitored } }

    func pinned(_ service: Service) -> Quota? {
        guard service.supportsPinning else { return nil }
        let candidates = monitoredQuotas.filter { $0.service == service }
        if service == .grok, let automatic = candidates.first(where: { $0.source == .official }) {
            return automatic
        }
        if let pin = candidates.first(where: { $0.widgetKey == pins[service] }) {
            if pin.feed != .codex || pin.source == .official ||
                !candidates.contains(where: { $0.feed == .codex && $0.source == .official }) {
                return pin
            }
        }
        return candidates.first { $0.source == .official } ?? candidates.first
    }

    func picked(key: String?, service: Service) -> Quota? {
        guard let key else { return pinned(service) }
        if let quota = monitoredQuotas.first(where: { $0.widgetKey == key }) { return quota }
        let formerService = QuotaRoute.widgetService(key) ?? service
        return pinned(formerService)
    }

    mutating func applyCodex(_ incoming: [Quota], account: String, startedAt: Date, complete: Bool) {
        let incomingIDs = Set(incoming.map(\.id))
        quotas.removeAll { quota in
            quota.feed == .codex && quota.source == .official && (
                quota.accountRef != account ||
                (complete && !incomingIDs.contains(quota.id) && quota.lastSuccessAt <= startedAt)
            )
        }
        for quota in incoming {
            if let index = quotas.firstIndex(where: { $0.id == quota.id }) {
                if quotas[index].lastSuccessAt <= startedAt { quotas[index] = quota }
            } else {
                quotas.append(quota)
            }
        }
        normalizePins()
    }

    mutating func applyGrok(_ quota: Quota) {
        if let index = quotas.firstIndex(where: { $0.id == quota.id }) {
            quotas[index] = quota
        } else {
            quotas.removeAll { $0.feed == .grok && $0.source == .official }
            quotas.append(quota)
        }
        normalizePins()
    }

    mutating func applyAntigravity(_ current: [Quota]) {
        // The CLI lists the active account's available groups on each successful query.
        quotas.removeAll { $0.feed == .antigravity }
        quotas.append(contentsOf: current)
        normalizePins()
    }

    mutating func markAntigravityUnavailable(_ status: ReadStatus, at date: Date) {
        for index in quotas.indices where quotas[index].feed == .antigravity {
            quotas[index].lastAttemptAt = date
            quotas[index].status = status
        }
    }

    mutating func applyCodexResetCredits(_ credits: CodexResetCredits?, account: String, startedAt: Date) {
        if let current = codexResetCredits, current.accountRef == account, current.lastSuccessAt > startedAt { return }
        codexResetCredits = credits
    }

    mutating func normalizePins() {
        let keys = Set(monitoredQuotas.map(\.widgetKey))
        pins = pins.filter { $0.key.supportsPinning && keys.contains($0.value) }
    }
}

enum QuotaRoute {
    static func url(feed: Feed, key: String? = nil) -> URL {
        var components = URLComponents()
        components.scheme = "quotamonitor"
        components.host = feed.rawValue
        if let key { components.queryItems = [.init(name: "quota", value: key)] }
        return components.url!
    }

    static func decode(_ url: URL) -> (Feed, String?)? {
        guard url.scheme == "quotamonitor", let host = url.host, let feed = Feed(rawValue: host) else { return nil }
        if feed == .legacyChat { return (.codex, nil) }
        guard feed.isMonitored else { return nil }
        let key = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems?.first { $0.name == "quota" }?.value
        return (feed, key)
    }

    static func widgetService(_ key: String) -> Service? {
        guard let data = Data(base64Encoded: key), let fields = try? JSONDecoder().decode([String].self, from: data),
              let first = fields.first else { return nil }
        guard let service = Service(rawValue: first), service.primaryFeed.isMonitored else { return nil }
        return service
    }
}

enum SnapshotFile {
    static let widgetKind = "QuotaMonitorWidget"
    static var sharedURL: URL? {
        guard let group = Bundle.main.object(forInfoDictionaryKey: "QuotaAppGroup") as? String,
              !group.isEmpty else { return nil }
        return FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: group)?
            .appendingPathComponent("QuotaMonitor", isDirectory: true)
            .appendingPathComponent("snapshot.json")
    }

    static func read(at url: URL?) -> Snapshot? {
        guard let url, let data = try? Data(contentsOf: url),
              var value = try? JSONDecoder().decode(Snapshot.self, from: data),
              value.schemaVersion == 1, !value.quotas.contains(where: { $0.source == .example }) else { return nil }
        for index in value.quotas.indices where value.quotas[index].feed == .antigravity {
            value.quotas[index].resetsAt = Quota.reportedReset(value.quotas[index].resetsAt)
        }
        return value
    }

    static func write(_ snapshot: Snapshot, at url: URL) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try JSONEncoder().encode(snapshot).write(to: url, options: .atomic)
    }
}
