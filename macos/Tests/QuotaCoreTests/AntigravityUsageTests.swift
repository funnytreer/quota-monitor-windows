import Foundation
import XCTest
@testable import QuotaCore

final class AntigravityUsageTests: XCTestCase {
    private func read(_ buckets: String) -> [Quota] {
        let result = #"{"command":{"name":"usage","data":{"groups":[{"buckets":["# + buckets + #"]}]}}}"#
        return AntigravityUsage.parse(Data(result.utf8), at: Date(timeIntervalSince1970: 100))
    }

    func testWeeklyGroupsFirstAndPercentIsRemaining() throws {
        let rows = read("""
        {"id":"3p-5h","remaining_fraction":1},{"id":"gemini-5h","remaining_fraction":0.72},
        {"id":"gemini-weekly","remaining_fraction":0.63,"reset_time":"2032-01-10T12:00:00.000Z"},
        {"id":"3p-weekly","remaining_fraction":1}
        """)
        XCTAssertEqual(rows.map(\.windowId), ["weekly","weekly","fiveHour","fiveHour"])
        XCTAssertEqual(rows.map(\.scopeId), ["gemini","partners","gemini","partners"])
        XCTAssertEqual(try XCTUnwrap(rows[0].remainingPercent), 63, accuracy: 0.001)
        XCTAssertNotNil(rows[0].resetsAt)
        XCTAssertEqual(rows[1].remainingPercent, 100)
        XCTAssertNil(rows[1].resetsAt)
    }

    func testSummaryHasFourConsistentRowsAndNeverSubstitutesShortWindow() throws {
        let rows = read(#"{"id":"gemini-weekly","remaining_fraction":0.95},{"id":"3p-weekly","remaining_fraction":1},{"id":"3p-5h","remaining_fraction":0.08}"#)
        var snapshot = Snapshot(quotas: rows)
        snapshot.pins[.antigravity] = rows[2].widgetKey
        let items = QuotaSummaryItem.items
        XCTAssertEqual(items.map(\.antigravityGroup), [nil, nil, .gemini, .partners])
        XCTAssertEqual(items.map(\.title), ["Codex", "Grok", "Gemini", "Claude / GPT"])
        XCTAssertEqual(items.compactMap { $0.reading(in: snapshot)?.remainingPercent }, [95, 100])
        snapshot.quotas.removeAll { $0.scopeId == "partners" && $0.windowId == "weekly" }
        XCTAssertNil(items.last?.reading(in: snapshot))
        XCTAssertEqual(items[2].reading(in: snapshot)?.widgetKey, rows[0].widgetKey)
    }

    func testUnknownOrMissingDoesNotBecomeFullAndZeroIsValid() {
        let rows = read("""
        {"id":"gemini-weekly","remaining_fraction":0},{"id":"3p-weekly"},{"id":"3p-5h","remaining_fraction":12},
        {"id":"other-weekly","remaining_fraction":1}
        """)
        XCTAssertEqual(rows.count, 1)
        XCTAssertEqual(rows.first?.remainingPercent, 0)
        XCTAssertTrue(AntigravityUsage.parse(Data("login required".utf8), at: Date()).isEmpty)
    }

    func testPlaceholderResetKeepsQuotaAvailableForTheMenuAndGauge() throws {
        let rows = read(#"{"id":"3p-weekly","remaining_fraction":1,"reset_time":"1970-01-01T00:00:00Z"}"#)
        let row = try XCTUnwrap(rows.first)
        let now = row.lastSuccessAt
        XCTAssertNil(row.resetsAt)
        XCTAssertNil(row.statusLabel(at: now))
        XCTAssertEqual(MenuGaugeSource.partners.percent(in: Snapshot(quotas: rows), at: now), 100)

        var old = row
        old.resetsAt = Date(timeIntervalSince1970: 0)
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: url) }
        try SnapshotFile.write(Snapshot(quotas: [old]), at: url)
        XCTAssertEqual(SnapshotFile.read(at: url)?.quotas.first, row)

        old.resetsAt = now.addingTimeInterval(-1)
        try SnapshotFile.write(Snapshot(quotas: [old]), at: url)
        let elapsed = try XCTUnwrap(SnapshotFile.read(at: url)?.quotas.first)
        XCTAssertEqual(elapsed.resetsAt, old.resetsAt)
        XCTAssertTrue(elapsed.waitingForReset(at: now))
    }

    func testWeeklySelectionAndStaleStatusSurviveReload() throws {
        let rows = read(#"{"id":"3p-weekly","remaining_fraction":0.8},{"id":"3p-5h","remaining_fraction":0.2}"#)
        var snapshot = Snapshot()
        snapshot.applyAntigravity(rows)
        snapshot.pins[.antigravity] = rows[1].widgetKey // A selection saved by an earlier release.
        let later = Date(timeIntervalSince1970: 200)
        snapshot.markAntigravityUnavailable(.stale, at: later)
        var restored = try JSONDecoder().decode(Snapshot.self, from: JSONEncoder().encode(snapshot))
        XCTAssertNil(restored.pinned(.antigravity))
        restored.normalizePins()
        XCTAssertNil(restored.pins[.antigravity])
        let row = try XCTUnwrap(restored.picked(key: rows[0].widgetKey, service: .antigravity))
        XCTAssertEqual(row.status, .stale)
        XCTAssertLessThan(row.lastSuccessAt, later)
        XCTAssertEqual(QuotaRoute.widgetService(row.widgetKey), .antigravity)
        snapshot.applyAntigravity([rows[1]])
        XCTAssertEqual(snapshot.quotas.count, 1)
        XCTAssertNil(snapshot.picked(key: row.widgetKey, service: .antigravity))
        XCTAssertEqual(snapshot.picked(key: rows[1].widgetKey, service: .antigravity), rows[1])
    }

    func testWeeklyRemindersTrackGeminiAndPartnersSeparatelyAndSkipFiveHourQuota() throws {
        let rows = read("""
        {"id":"gemini-weekly","remaining_fraction":0.8,"reset_time":"2032-01-10T12:00:00Z"},
        {"id":"3p-weekly","remaining_fraction":1,"reset_time":"2032-01-10T12:00:00Z"},
        {"id":"3p-5h","remaining_fraction":1,"reset_time":"2032-01-10T12:00:00Z"}
        """)
        XCTAssertEqual(rows.count, 3)
        let reset = try XCTUnwrap(rows.first?.resetsAt)
        let now = reset.addingTimeInterval(-3600)
        var history = WeeklyReminderHistory()
        for row in rows {
            let due = history.eligibleReset(for: row, days: 1, above: 50, at: now)
            XCTAssertEqual(due != nil, row.windowId == "weekly")
            if let due { history.recordDelivery(for: row.widgetKey, reset: due, at: now) }
        }
        XCTAssertTrue(rows.allSatisfy { history.eligibleReset(for: $0, days: 1, above: 50, at: now) == nil })
    }
}
