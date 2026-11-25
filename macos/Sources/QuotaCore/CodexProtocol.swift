import Foundation

struct CodexWindows: Decodable {
    struct Bucket: Decodable {
        struct Window: Decodable {
            let usedPercent: Double?
            let windowDurationMins: Int?
            let resetsAt: Double?
        }

        let limitId: String?
        let limitName: String?
        let primary: Window?
        let secondary: Window?
    }

    struct ResetCreditPayload: Decodable {
        struct Credit: Decodable {
            let status: String
            let expiresAt: Double?
        }

        let availableCount: Int
        let credits: [Credit]?
    }

    let rateLimits: Bucket?
    let rateLimitsByLimitId: [String: Bucket]?
    let rateLimitResetCredits: ResetCreditPayload?
    var isComplete: Bool { rateLimitsByLimitId != nil }
    var hasPayload: Bool { rateLimits != nil || isComplete || rateLimitResetCredits != nil }

    func resetCredits(account: String, at date: Date) -> CodexResetCredits? {
        guard let payload = rateLimitResetCredits, payload.availableCount >= 0 else { return nil }
        let expiresAt = (payload.credits ?? []).compactMap { credit -> Date? in
            guard credit.status == "available", let expiresAt = credit.expiresAt,
                  expiresAt.isFinite, expiresAt > 0 else { return nil }
            return Date(timeIntervalSince1970: expiresAt)
        }.sorted().prefix(payload.availableCount)
        let expirations = Dictionary(grouping: expiresAt, by: { $0 })
            .map { CodexResetCredits.Expiration(date: $0.key, count: $0.value.count) }
            .sorted { $0.date < $1.date }
        return CodexResetCredits(accountRef: account, availableCount: payload.availableCount,
                                 expirations: expirations, lastSuccessAt: date)
    }

    func quotas(account: String, at date: Date) -> [Quota] {
        let buckets: [(String, Bucket)]
        if let multiple = rateLimitsByLimitId {
            buckets = multiple.sorted { lhs, rhs in
                if lhs.key == "codex" { return rhs.key != "codex" }
                if rhs.key == "codex" { return false }
                return lhs.key < rhs.key
            }
        } else if let rateLimits {
            buckets = [(rateLimits.limitId ?? "codex", rateLimits)]
        } else {
            return []
        }

        return buckets.flatMap { key, bucket in
            [(key: "primary", window: bucket.primary), (key: "secondary", window: bucket.secondary)].compactMap { item in
                guard let window = item.window else { return nil }
                let percent = window.usedPercent.flatMap { (0...100).contains($0) ? $0 : nil }
                let reset = window.resetsAt.flatMap { $0.isFinite && $0 > 0 ? Date(timeIntervalSince1970: $0) : nil }
                guard percent != nil || reset != nil else { return nil }
                return Quota(service: .codex, feed: .codex, accountRef: account,
                             scopeId: bucket.limitId ?? key, windowId: item.key,
                             displayName: bucket.limitName ?? (key == "codex" ? "Codex" : key),
                             durationMinutes: window.windowDurationMins, usedPercent: percent,
                             resetsAt: reset, source: .official, lastSuccessAt: date, lastAttemptAt: date)
            }
        }
    }
}

struct RefreshSchedule {
    enum Trigger { case background, visible, manual, reset }
    var interval: TimeInterval = 900
    var lastSuccess: Date?
    var retryAt: Date?
    var serverRetryAt: Date?
    var loginRequired = false
    private var failures = 0

    func shouldRead(_ trigger: Trigger, at date: Date) -> Bool {
        if loginRequired || serverRetryAt.map({ $0 > date }) == true { return false }
        if trigger == .manual { return true }
        if retryAt.map({ $0 > date }) == true { return false }
        let maximumAge: TimeInterval = trigger == .visible ? 60 : interval
        if trigger == .reset { return true }
        return lastSuccess.map { date.timeIntervalSince($0) >= maximumAge } ?? true
    }

    mutating func success(at date: Date) {
        lastSuccess = date
        failures = 0
        retryAt = nil
        serverRetryAt = nil
        loginRequired = false
    }

    mutating func failure(at date: Date, serverDelay: TimeInterval? = nil, requiresLogin: Bool = false) {
        failures = min(6, failures + 1)
        retryAt = date.addingTimeInterval(min(60 * pow(2, Double(failures - 1)), 30 * 60))
        serverRetryAt = serverDelay.map { date.addingTimeInterval($0) }
        loginRequired = requiresLogin
    }
}

struct NotificationHistory: Codable {
    private struct State: Codable {
        let reset: Date?
        let sent: Bool
    }
    private var entries: [String: State] = [:]

    mutating func shouldNotify(_ quota: Quota, threshold: Double, at date: Date) -> Bool {
        guard quota.source == .official, quota.status == .current,
              !quota.waitingForReset(at: date), let remaining = quota.remainingPercent else { return false }
        let key = quota.widgetKey
        let previous = entries[key]
        let changedPeriod = previous?.reset.map { oldReset in quota.resetsAt.map { $0 > oldReset } ?? false } ?? false
        let alreadySent = previous?.sent == true && !changedPeriod
        let reset = quota.resetsAt ?? previous?.reset
        if remaining > threshold {
            entries[key] = .init(reset: reset, sent: reset != nil && alreadySent)
            return false
        }
        entries[key] = .init(reset: reset, sent: true)
        return !alreadySent
    }

    mutating func keepOnly(_ keys: Set<String>) {
        entries = entries.filter { keys.contains($0.key) }
    }
}

struct WeeklyReminderHistory: Codable {
    private var sentResets: [String: Date] = [:]

    func eligibleReset(for quota: Quota, days: Int, above threshold: Int, at now: Date) -> Date? {
        guard quota.feed.isMonitored, quota.source == .official, quota.status == .current,
              quota.windowId == "weekly" || quota.durationMinutes == 10_080,
              let remaining = quota.remainingPercent, remaining > Double(threshold),
              let reset = quota.resetsAt, reset > now,
              reset.timeIntervalSince(now) <= Double(days) * 86_400 else { return nil }
        if let previous = sentResets[quota.widgetKey] {
            // Providers can adjust the reported reset slightly between reads.
            guard now >= previous, reset.timeIntervalSince(previous) > 300 else { return nil }
        }
        return reset
    }

    mutating func recordDelivery(for key: String, reset: Date, at now: Date) {
        sentResets = sentResets.filter { now.timeIntervalSince($0.value) <= 300 }
        sentResets[key] = reset
    }
}
