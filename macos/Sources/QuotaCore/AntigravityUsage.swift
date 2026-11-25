import Foundation

enum AntigravityUsage {
    private struct Response: Decodable {
        let command: Command
        struct Command: Decodable { let name: String; let data: Payload }
        struct Payload: Decodable { let groups: [Group] }
        struct Group: Decodable { let buckets: [Bucket]? }
        struct Bucket: Decodable {
            let id: String?
            let remainingFraction: Double?
            let resetTime: String?
        }
    }

    static func parse(_ data: Data, at date: Date) -> [Quota] {
        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = .convertFromSnakeCase
        guard let reply = try? decoder.decode(Response.self, from: data), reply.command.name == "usage" else { return [] }
        let buckets = reply.command.data.groups.flatMap { $0.buckets ?? [] }
        let iso = ISO8601DateFormatter()
        let precise = ISO8601DateFormatter()
        precise.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        var result: [Quota] = []
        for weekly in [true, false] {
            for group in AntigravityGroup.allCases {
                let id = group.bucketPrefix + (weekly ? "-weekly" : "-5h")
                guard let bucket = buckets.first(where: { $0.id == id }),
                      let fraction = bucket.remainingFraction, (0...1).contains(fraction) else { continue }
                let reset = bucket.resetTime.flatMap { precise.date(from: $0) ?? iso.date(from: $0) }
                let row = Quota(service: .antigravity, feed: .antigravity, accountRef: "active-agy",
                                scopeId: group.rawValue, windowId: weekly ? "weekly" : "fiveHour",
                                displayName: group.title, durationMinutes: weekly ? 10080 : 300,
                                usedPercent: 100 * (1 - fraction), resetsAt: Quota.reportedReset(reset), source: .official,
                                lastSuccessAt: date, lastAttemptAt: date)
                result.append(row)
            }
        }
        return result
    }
}

enum MenuGaugeSource: String, CaseIterable {
    case average, codex, grok, gemini, partners

    var title: String {
        switch self {
        case .average: "总量（平均剩余）"
        case .codex: "Codex"
        case .grok: "Grok"
        case .gemini: "Gemini"
        case .partners: "Claude / GPT"
        }
    }

    func percent(in snapshot: Snapshot, at now: Date) -> Int? {
        let weekly = snapshot.monitoredQuotas.filter {
            ($0.windowId == "weekly" || $0.durationMinutes == 10_080) && $0.source == .official &&
                $0.status == .current && !$0.waitingForReset(at: now) &&
                now.timeIntervalSince($0.lastSuccessAt) < 35 * 60 && $0.remainingPercent != nil
        }
        func value(_ source: Self) -> Double? {
            switch source {
            case .codex:
                let rows = weekly.filter { $0.feed == .codex }
                let quota = rows.first { $0.widgetKey == snapshot.pins[.codex] }
                    ?? rows.first { $0.scopeId == "codex" } ?? rows.first
                return quota?.remainingPercent
            case .grok: return weekly.first { $0.feed == .grok }?.remainingPercent
            case .gemini, .partners:
                let group: AntigravityGroup = source == .gemini ? .gemini : .partners
                return weekly.first { $0.feed == .antigravity && $0.scopeId == group.rawValue }?.remainingPercent
            case .average: return nil
            }
        }
        if self != .average { return value(self).map { Int($0.rounded()) } }
        let values = Self.allCases.compactMap(value)
        guard !values.isEmpty else { return nil }
        return Int((values.reduce(0, +) / Double(values.count)).rounded())
    }
}

// The menu and the overview use the same four readings and detail destinations.
enum QuotaSummaryItem: Hashable {
    case pinned(Service)
    case antigravityWeek(AntigravityGroup)

    static let items: [Self] = [.pinned(.codex), .pinned(.grok)] + AntigravityGroup.allCases.map(Self.antigravityWeek)

    var service: Service {
        switch self {
        case .pinned(let service): service
        case .antigravityWeek: .antigravity
        }
    }

    var antigravityGroup: AntigravityGroup? {
        if case .antigravityWeek(let group) = self { return group }
        return nil
    }

    var title: String {
        switch self {
        case .pinned(let service): service.name
        case .antigravityWeek(let group): group.title
        }
    }

    func subtitle(for quota: Quota?) -> String? {
        switch self {
        case .pinned: quota?.summaryTitle
        case .antigravityWeek(.gemini): "Antigravity · 周额度"
        case .antigravityWeek(.partners): "Antigravity · 共享周额度"
        }
    }

    func reading(in snapshot: Snapshot) -> Quota? {
        switch self {
        case .pinned(let service): snapshot.pinned(service)
        case .antigravityWeek(let group): snapshot.quotas.first {
            $0.feed == .antigravity && $0.scopeId == group.rawValue && $0.windowId == "weekly"
        }
        }
    }
}
