import Foundation
import XCTest
@testable import QuotaCore

final class QuotaCoreTests: XCTestCase {
    private let now = Date(timeIntervalSince1970: 1_800_000_000)

    private func decode(_ json: String) throws -> CodexWindows {
        try JSONDecoder().decode(CodexWindows.self, from: Data(json.utf8))
    }

    private func quota(_ account: String = "a", _ time: Date? = nil, _ percent: Double? = 80) -> Quota {
        let read = time ?? now
        return Quota(service: .codex, feed: .codex, accountRef: account, scopeId: "codex", windowId: "primary",
                     displayName: "Codex", usedPercent: percent, resetsAt: now.addingTimeInterval(3600),
                     source: .official, lastSuccessAt: read, lastAttemptAt: read)
    }

    func testMenuGaugeTracksWeeklySourcesAndAveragesEachPoolOnce() {
        func row(_ feed: Feed, _ scope: String, _ window: String, _ remaining: Double, _ minutes: Int) -> Quota {
            Quota(service: feed.service, feed: feed, accountRef: "sample", scopeId: scope, windowId: window,
                  displayName: scope, durationMinutes: minutes, usedPercent: 100 - remaining,
                  source: .official, lastSuccessAt: now, lastAttemptAt: now)
        }
        let short = row(.codex, "codex", "primary", 98, 300)
        let otherCodex = row(.codex, "other", "secondary", 6, 10_080)
        let main = row(.codex, "codex", "secondary", 20, 10_080)
        let grok = row(.grok, "shared", "weekly", 0, 10_080)
        let gemini = row(.antigravity, "gemini", "weekly", 80, 10_080)
        let partners = row(.antigravity, "partners", "weekly", 100, 10_080)
        let snapshot = Snapshot(quotas: [short, otherCodex, main, grok, partners, gemini], pins: [.codex: short.widgetKey])
        XCTAssertEqual(MenuGaugeSource.allCases.map { $0.percent(in: snapshot, at: now) }, [50, 20, 0, 80, 100])
        XCTAssertEqual(MenuGaugeSource.average.percent(in: Snapshot(quotas: [short, gemini, partners]), at: now), 90)
        XCTAssertNil(MenuGaugeSource.codex.percent(in: Snapshot(quotas: [short]), at: now))
    }

    func testMenuGaugeDistinguishesUnavailableFromZero() {
        var row = quota()
        row.durationMinutes = 10_080
        row.usedPercent = 100
        func current() -> Int? { MenuGaugeSource.average.percent(in: Snapshot(quotas: [row]), at: now) }
        XCTAssertEqual(current(), 0)
        row.status = .loginRequired
        XCTAssertNil(current())
        row.status = .current
        row.lastSuccessAt = now.addingTimeInterval(-35 * 60)
        XCTAssertNil(current())
        row.lastSuccessAt = now
        row.resetsAt = now
        XCTAssertNil(current())
        row.resetsAt = nil
        row.usedPercent = nil
        XCTAssertNil(current())
        XCTAssertNil(MenuGaugeSource.average.percent(in: Snapshot(), at: now))
    }

    func testRemainingPercentageRoundsOnlyForDisplay() throws {
        var reading = quota()
        for (remaining, expected) in [(62.14, "62%"), (62.5, "63%"), (43.89, "44%"), (0, "0%"), (100, "100%")] {
            reading.usedPercent = 100 - remaining
            XCTAssertEqual(reading.numberAndMeaning(at: now).0, expected)
            XCTAssertEqual(try XCTUnwrap(reading.remainingPercent), remaining, accuracy: 0.000001)
        }
    }

    func testMultiWindowAndResetOnly() throws {
        let windows = try decode("""
        {"rateLimits":{"limitId":"other","primary":{"usedPercent":77}},
         "rateLimitsByLimitId":{"codex":{
           "primary":{"usedPercent":32,"windowDurationMins":300},
           "secondary":{"resetsAt":1800007200,"windowDurationMins":10080}
         }}}
        """)
        let readings = windows.quotas(account: "a", at: now)
        XCTAssertTrue(windows.isComplete)
        XCTAssertEqual(readings.count, 2)
        XCTAssertEqual(readings[0].remainingPercent, 68)
        XCTAssertEqual(readings[0].windowLabel, "5 小时")
        XCTAssertNil(readings[1].remainingPercent)
        XCTAssertEqual(readings[1].windowLabel, "1 周")
        XCTAssertEqual(readings[1].resetsAt, now.addingTimeInterval(7200))
    }

    func testLegacyPartialDoesNotPrune() throws {
        let old = try decode("""
        {"rateLimitsByLimitId":{"codex":{
          "primary":{"usedPercent":50},
          "secondary":{"usedPercent":25}
        }}}
        """)
        let partial = try decode("""
        {"rateLimits":{"limitId":"codex","primary":{"usedPercent":60}}}
        """)
        var snapshot = Snapshot()
        snapshot.applyCodex(old.quotas(account: "a", at: now), account: "a", startedAt: now, complete: true)
        let later = now.addingTimeInterval(5)
        snapshot.applyCodex(partial.quotas(account: "a", at: later), account: "a", startedAt: later, complete: partial.isComplete)
        XCTAssertEqual(snapshot.quotas.map(\.remainingPercent), [40, 75])
    }

    func testLegacyChatIsReadFromDiskAndSkippedInTheApp() throws {
        let codex = quota()
        let chat = Quota(service: .codex, feed: .legacyChat, accountRef: "manual-chat", scopeId: "chat", windowId: "weekly",
                         displayName: "ChatGPT", source: .manual, lastSuccessAt: now, lastAttemptAt: now)
        let stored = Snapshot(quotas: [chat, codex], pins: [.codex: chat.widgetKey])
        let bytes = try JSONEncoder().encode(stored)
        let decoded = try JSONDecoder().decode(Snapshot.self, from: bytes)
        XCTAssertEqual(Service.codex.rawValue, "chatgpt")
        XCTAssertEqual(decoded.quotas, [chat, codex])
        XCTAssertEqual(decoded.monitoredQuotas, [codex])
        XCTAssertEqual(decoded.pinned(.codex), codex)
        XCTAssertEqual(decoded.picked(key: chat.widgetKey, service: .codex), codex)
        let oldURL = QuotaRoute.url(feed: .legacyChat, key: chat.widgetKey)
        XCTAssertEqual(QuotaRoute.decode(oldURL)?.0, .codex)
        XCTAssertNil(QuotaRoute.decode(oldURL)?.1)
    }

    func testResetCreditsUseOfficialCountAndGroupKnownExpirations() throws {
        let windows = try decode("""
        {"rateLimitResetCredits":{"availableCount":4,"credits":[
          {"status":"available","expiresAt":1800010800},
          {"status":"used","expiresAt":1800000100},
          {"status":"available","expiresAt":1800003600},
          {"status":"available","expiresAt":1800003600},
          {"status":"available","expiresAt":null}
        ]}}
        """)
        XCTAssertTrue(windows.hasPayload)
        let credits = try XCTUnwrap(windows.resetCredits(account: "a", at: now))
        XCTAssertEqual(credits.availableCount, 4)
        XCTAssertEqual(credits.expirations.map(\.date), [now.addingTimeInterval(3600), now.addingTimeInterval(10800)])
        XCTAssertEqual(credits.expirations.map(\.count), [2, 1])
        XCTAssertEqual(credits.countWithoutExpiry, 1)
    }

    func testUnknownResetCreditsAreDistinctFromZeroAndDoNotOverrideNewerRead() throws {
        let noField = try decode("""
        {"rateLimits":{}}
        """)
        let none = try decode("""
        {"rateLimitResetCredits":{"availableCount":0,"credits":null}}
        """)
        let known = try XCTUnwrap(none.resetCredits(account: "a", at: now))
        XCTAssertNil(noField.resetCredits(account: "a", at: now))
        XCTAssertEqual(known.availableCount, 0)
        XCTAssertNil(known.nextExpiryLabel)
        var snapshot = Snapshot(codexResetCredits: known)
        snapshot.applyCodexResetCredits(nil, account: "a", startedAt: now.addingTimeInterval(-1))
        XCTAssertEqual(snapshot.codexResetCredits, known)
        snapshot.applyCodexResetCredits(nil, account: "a", startedAt: now)
        XCTAssertNil(snapshot.codexResetCredits)
    }

    func testNewEventCannotBeReplacedOrPrunedByAnOlderRequest() {
        let fresh = quota("a", now.addingTimeInterval(10), 85)
        var snapshot = Snapshot(quotas: [fresh])
        snapshot.applyCodex([quota("a", now.addingTimeInterval(20), 60)], account: "a", startedAt: now, complete: true)
        XCTAssertEqual(snapshot.quotas, [fresh])
        snapshot.applyCodex([], account: "a", startedAt: now, complete: true)
        XCTAssertEqual(snapshot.quotas, [fresh])
        snapshot.pins[.codex] = fresh.widgetKey
        let other = quota("b")
        snapshot.applyCodex([other], account: "b", startedAt: now, complete: true)
        XCTAssertEqual(snapshot.quotas, [other])
        XCTAssertNil(snapshot.pins[.codex])
        XCTAssertEqual(snapshot.picked(key: fresh.widgetKey, service: .grok), other)
    }

    func testRefreshSchedule() {
        var schedule = RefreshSchedule()
        schedule.success(at: now)
        XCTAssertFalse(schedule.shouldRead(.visible, at: now.addingTimeInterval(59)))
        XCTAssertTrue(schedule.shouldRead(.visible, at: now.addingTimeInterval(61)))
        XCTAssertFalse(schedule.shouldRead(.background, at: now.addingTimeInterval(899)))
        XCTAssertTrue(schedule.shouldRead(.background, at: now.addingTimeInterval(900)))
        XCTAssertTrue(schedule.shouldRead(.reset, at: now))
        schedule.failure(at: now, serverDelay: 120)
        XCTAssertFalse(schedule.shouldRead(.manual, at: now.addingTimeInterval(119)))
        XCTAssertTrue(schedule.shouldRead(.manual, at: now.addingTimeInterval(120)))
        schedule.failure(at: now, requiresLogin: true)
        XCTAssertFalse(schedule.shouldRead(.manual, at: now.addingTimeInterval(900)))
    }

    func testThresholdDedupAndUnknownPeriodRearming() {
        var history = NotificationHistory()
        var reading = quota()
        XCTAssertTrue(history.shouldNotify(reading, threshold: 20, at: now))
        XCTAssertFalse(history.shouldNotify(reading, threshold: 20, at: now))
        reading.usedPercent = 5
        XCTAssertFalse(history.shouldNotify(reading, threshold: 20, at: now))
        reading.usedPercent = 99
        XCTAssertFalse(history.shouldNotify(reading, threshold: 20, at: now))
        reading.resetsAt = now.addingTimeInterval(7200)
        XCTAssertTrue(history.shouldNotify(reading, threshold: 20, at: now))
        XCTAssertFalse(history.shouldNotify(reading, threshold: 20, at: now.addingTimeInterval(7200)))

        var unknown = NotificationHistory()
        reading.resetsAt = nil
        XCTAssertTrue(unknown.shouldNotify(reading, threshold: 20, at: now))
        reading.usedPercent = 10
        XCTAssertFalse(unknown.shouldNotify(reading, threshold: 20, at: now))
        reading.usedPercent = 100
        XCTAssertTrue(unknown.shouldNotify(reading, threshold: 20, at: now))
        reading.source = .manual
        XCTAssertFalse(unknown.shouldNotify(reading, threshold: 20, at: now))
    }

    func testWeeklyReminderTimeAndStrictPercentageBoundaries() {
        let history = WeeklyReminderHistory()
        let cutoff = now.addingTimeInterval(2 * 86_400)
        var reading = quota("a", now, 49.99)
        reading.durationMinutes = 10_080
        reading.resetsAt = cutoff
        func eligible() -> Date? { history.eligibleReset(for: reading, days: 2, above: 50, at: now) }
        XCTAssertEqual(eligible(), cutoff)
        reading.usedPercent = 50
        XCTAssertNil(eligible())
        reading.usedPercent = 0
        reading.resetsAt = cutoff.addingTimeInterval(1)
        XCTAssertNil(eligible())
        reading.resetsAt = now.addingTimeInterval(1)
        XCTAssertNotNil(eligible())
        reading.resetsAt = now
        XCTAssertNil(eligible())
        reading.resetsAt = now.addingTimeInterval(-1)
        XCTAssertNil(eligible())
    }

    func testWeeklyReminderRequiresOfficialCurrentWeeklyReadingWithKnownResetAndPercent() throws {
        let history = WeeklyReminderHistory()
        var reading = quota("a", now, 5)
        func eligible() -> Date? { history.eligibleReset(for: reading, days: 1, above: 50, at: now) }
        XCTAssertNil(eligible())
        reading.durationMinutes = 300
        XCTAssertNil(eligible())
        reading.durationMinutes = 10_080
        XCTAssertNotNil(eligible())
        for status in [ReadStatus.stale, .loginRequired] {
            reading.status = status
            XCTAssertNil(eligible())
        }
        reading.status = .current
        reading.source = .manual
        XCTAssertNil(eligible())
        reading.source = .official
        reading.usedPercent = nil
        XCTAssertNil(eligible())
        reading.usedPercent = 0
        reading.resetsAt = nil
        XCTAssertNil(eligible())

        let grok = try XCTUnwrap(GrokUsage(card: "Weekly limit\n10% used", dateTime: nil,
                                           now: now)).quota(account: "grok", at: now)
        reading = grok
        reading.resetsAt = now.addingTimeInterval(100)
        XCTAssertNotNil(eligible())
    }

    func testWeeklyReminderSurvivesRestartAndAdvancesWithTheCycle() throws {
        var history = WeeklyReminderHistory()
        let reset = now.addingTimeInterval(3_600)
        var reading = quota("a", now, 10)
        reading.durationMinutes = 10_080
        history.recordDelivery(for: reading.widgetKey, reset: reset, at: now)
        history = try JSONDecoder().decode(WeeklyReminderHistory.self, from: JSONEncoder().encode(history))
        XCTAssertNil(history.eligibleReset(for: reading, days: 7, above: 0, at: now))
        reading.resetsAt = reset.addingTimeInterval(90)
        XCTAssertNil(history.eligibleReset(for: reading, days: 2, above: 50, at: reset))
        var otherAccount = quota("b", now, 10)
        otherAccount.durationMinutes = 10_080
        XCTAssertNotNil(history.eligibleReset(for: otherAccount, days: 2, above: 50, at: now))
        let nextReset = reset.addingTimeInterval(7 * 86_400)
        reading.resetsAt = nextReset
        XCTAssertEqual(history.eligibleReset(for: reading, days: 2, above: 50,
                                             at: nextReset.addingTimeInterval(-86_400)), nextReset)
    }

    func testRetiredWebSnapshotPreservesOtherSourcesAndHidesOldPins() throws {
        let codex = quota()
        let web = Quota(service: .legacyGeminiWeb, feed: .legacyGeminiWeb, accountRef: "chrome",
                        scopeId: "gemini-app", windowId: "weekly", displayName: "Gemini",
                        usedPercent: 12, source: .official, lastSuccessAt: now, lastAttemptAt: now)
        let ag = Quota(service: .antigravity, feed: .antigravity, accountRef: "active-agy",
                       scopeId: "gemini", windowId: "weekly", displayName: "Gemini",
                       usedPercent: 34, source: .official, lastSuccessAt: now, lastAttemptAt: now)
        let original = Snapshot(quotas: [web, codex, ag], pins: [.legacyGeminiWeb: web.widgetKey, .codex: codex.widgetKey])
        var restored = try JSONDecoder().decode(Snapshot.self, from: JSONEncoder().encode(original))
        restored.normalizePins()
        XCTAssertEqual(Feed.legacyGeminiWeb.rawValue, "gemini")
        XCTAssertEqual(Service.legacyGeminiWeb.rawValue, "gemini")
        XCTAssertEqual(restored.monitoredQuotas, [codex, ag])
        XCTAssertEqual(restored.pins, [.codex: codex.widgetKey])
        XCTAssertNil(QuotaRoute.decode(QuotaRoute.url(feed: .legacyGeminiWeb)))
        XCTAssertNil(QuotaRoute.widgetService(web.widgetKey))
    }

    func testDeepLinkAndSnapshotExcludesExamples() throws {
        var reading = quota()
        let url = QuotaRoute.url(feed: .codex, key: reading.widgetKey)
        XCTAssertEqual(QuotaRoute.decode(url)?.0, .codex)
        XCTAssertEqual(QuotaRoute.decode(url)?.1, reading.widgetKey)
        XCTAssertEqual(QuotaRoute.widgetService(reading.widgetKey), .codex)
        let file = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: file) }
        try SnapshotFile.write(Snapshot(quotas: [reading]), at: file)
        XCTAssertEqual(SnapshotFile.read(at: file)?.quotas, [reading])
        reading.source = .example
        try SnapshotFile.write(Snapshot(quotas: [reading]), at: file)
        XCTAssertNil(SnapshotFile.read(at: file))
    }
}
