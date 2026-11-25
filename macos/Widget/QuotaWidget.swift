import AppIntents
import SwiftUI
import WidgetKit

struct WidgetQuota: AppEntity {
    static let typeDisplayRepresentation = TypeDisplayRepresentation(name: "额度项")
    static let defaultQuery = WidgetQuotaQuery()

    let id: String
    let title: String

    var displayRepresentation: DisplayRepresentation { DisplayRepresentation(title: "\(title)") }
}

struct WidgetQuotaQuery: EntityQuery {
    private var choices: [WidgetQuota] {
        let snapshot = SnapshotFile.read(at: SnapshotFile.sharedURL) ?? Snapshot()
        return snapshot.monitoredQuotas.map { WidgetQuota(id: $0.widgetKey, title: "\($0.service.name) · \($0.summaryTitle)") }
    }

    func entities(for identifiers: [String]) async throws -> [WidgetQuota] {
        let current = choices
        return identifiers.map { key in
            current.first { $0.id == key } ?? WidgetQuota(id: key, title: "该额度项已移除")
        }
    }

    func suggestedEntities() async throws -> [WidgetQuota] { choices }
}

struct QuotaWidgetConfiguration: WidgetConfigurationIntent {
    static let title: LocalizedStringResource = "选择额度"
    static let description = IntentDescription("选择桌面小组件显示的订阅额度。")

    @Parameter(title: "主额度")
    var first: WidgetQuota?

    @Parameter(title: "中号右侧额度")
    var second: WidgetQuota?
}

struct QuotaEntry: TimelineEntry {
    let date: Date
    let first: Quota?
    let second: Quota?
    let firstService: Service
    let secondService: Service
}

struct QuotaTimeline: AppIntentTimelineProvider {
    func placeholder(in context: Context) -> QuotaEntry {
        .init(date: .now, first: nil, second: nil, firstService: .codex, secondService: .grok)
    }

    func snapshot(for configuration: QuotaWidgetConfiguration, in context: Context) async -> QuotaEntry {
        build(configuration, at: .now)
    }

    func timeline(for configuration: QuotaWidgetConfiguration, in context: Context) async -> Timeline<QuotaEntry> {
        let now = Date.now
        let first = build(configuration, at: now)
        let dates = [first.first, first.second].compactMap { $0 }.flatMap { quota in
            [quota.resetsAt, quota.source == .official ? quota.lastSuccessAt.addingTimeInterval(35 * 60) : nil]
                .compactMap { $0 }
        }
        let future = Set(dates.filter { $0 > now }).sorted().map { date in
            QuotaEntry(date: date, first: first.first, second: first.second,
                       firstService: first.firstService, secondService: first.secondService)
        }
        return Timeline(entries: [first] + future, policy: .after(now.addingTimeInterval(30 * 60)))
    }

    private func build(_ configuration: QuotaWidgetConfiguration, at date: Date) -> QuotaEntry {
        let firstService = configuration.first.flatMap { QuotaRoute.widgetService($0.id) } ?? .codex
        let secondService = configuration.second.flatMap { QuotaRoute.widgetService($0.id) } ?? .grok
        let snapshot = SnapshotFile.read(at: SnapshotFile.sharedURL) ?? Snapshot()
        return QuotaEntry(date: date, first: snapshot.picked(key: configuration.first?.id, service: firstService),
                          second: snapshot.picked(key: configuration.second?.id, service: secondService),
                          firstService: firstService, secondService: secondService)
    }
}

struct QuotaWidgetView: View {
    let entry: QuotaEntry
    @Environment(\.widgetFamily) private var family

    var body: some View {
        Group {
            if family == .systemSmall {
                tile(entry.first, service: entry.firstService, showTime: true)
                    .widgetURL(url(entry.first, entry.firstService))
            } else {
                let sameReadTime = entry.first != nil && entry.first?.lastSuccessAt == entry.second?.lastSuccessAt
                VStack(alignment: .leading, spacing: 6) {
                    Label("额度速览", systemImage: "gauge.with.dots.needle.33percent")
                        .font(.caption.bold())
                    HStack(spacing: 11) {
                        Link(destination: url(entry.first, entry.firstService)) {
                            tile(entry.first, service: entry.firstService, showTime: !sameReadTime)
                        }
                        Divider()
                        Link(destination: url(entry.second, entry.secondService)) {
                            tile(entry.second, service: entry.secondService, showTime: !sameReadTime)
                        }
                    }
                    if sameReadTime, let quota = entry.first {
                        timestamp(quota)
                    }
                }
            }
        }
        .containerBackground(for: .widget) {
            Color(nsColor: .windowBackgroundColor)
        }
    }

    private func url(_ quota: Quota?, _ service: Service) -> URL {
        QuotaRoute.url(feed: service.primaryFeed, key: quota?.widgetKey)
    }

    private func tile(_ quota: Quota?, service: Service, showTime: Bool) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Label {
                Text(service.name)
            } icon: {
                ServiceIcon(service: service, group: quota.flatMap { AntigravityGroup(rawValue: $0.scopeId) }, size: 18)
            }
            .font(.headline).widgetAccentable()
            if let quota {
                Text(quota.summaryTitle).font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                let (number, meaning) = quota.numberAndMeaning(at: entry.date)
                ViewThatFits(in: .horizontal) {
                    HStack(alignment: .firstTextBaseline, spacing: 4) {
                        figure(number)
                        Text(meaning).font(.caption2).foregroundStyle(.secondary)
                    }
                    VStack(alignment: .leading, spacing: 1) {
                        figure(number)
                        Text(meaning).font(.caption2).foregroundStyle(.secondary)
                    }
                }
                if let percent = quota.remainingPercent {
                    ProgressView(value: percent, total: 100)
                        .progressViewStyle(.linear).tint(.blue)
                        .widgetAccentable().accessibilityHidden(true)
                }
                if let status = status(quota) {
                    Label(status, systemImage: "exclamationmark.circle")
                        .font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                } else if let reset = quota.resetLabel(at: entry.date) {
                    Label(reset, systemImage: "clock")
                        .font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                }
                if showTime { timestamp(quota) }
            } else {
                Text("暂无读数").font(.caption).foregroundStyle(.secondary)
                Text("设置来源").font(.caption.bold()).foregroundStyle(.blue).widgetAccentable()
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
        .accessibilityElement(children: .combine)
    }

    private func status(_ quota: Quota) -> String? {
        quota.statusLabel(at: entry.date) ??
            (quota.source == .official && entry.date.timeIntervalSince(quota.lastSuccessAt) >= 35 * 60 ? "上次记录" : nil)
    }

    private func figure(_ text: String) -> some View {
        Text(text).font(.system(size: 27, weight: .semibold)).monospacedDigit()
            .minimumScaleFactor(0.75).lineLimit(1)
    }

    private func timestamp(_ quota: Quota) -> some View {
        let time = Calendar.current.isDate(quota.lastSuccessAt, inSameDayAs: entry.date)
            ? quota.lastSuccessAt.formatted(.dateTime.hour().minute())
            : quota.lastSuccessAt.formatted(.dateTime.month().day().hour().minute())
        return Text("\(quota.source == .manual ? "手动" : "读取") \(time)")
            .font(.caption2).foregroundStyle(.secondary).lineLimit(1)
    }
}

@main
struct QuotaWidget: Widget {
    var body: some WidgetConfiguration {
        AppIntentConfiguration(kind: SnapshotFile.widgetKind, intent: QuotaWidgetConfiguration.self, provider: QuotaTimeline()) { entry in
            QuotaWidgetView(entry: entry)
        }
        .configurationDisplayName("额度速览")
        .description("在桌面查看 Codex、Grok 或 Antigravity 的额度。")
        .supportedFamilies([.systemSmall, .systemMedium])
    }
}
