import Foundation
import XCTest
@testable import QuotaCore

final class GrokUsageTests: XCTestCase {
    private let now = Date(timeIntervalSince1970: 1_956_873_600)
    private let utc = TimeZone(secondsFromGMT: 0)!

    func testWeeklyTotalDoesNotReadProductBreakdown() throws {
        let reading = try XCTUnwrap(GrokUsage(card: """
            Weekly SuperGrok Limit
            34% used
            Resets Jan 8 at 2:30 PM
            Usage by product
            Chat 79%
            Build 100%
            """, dateTime: nil, now: now, timeZone: utc))
        let quota = reading.quota(account: "test", at: now)
        XCTAssertEqual(quota.feed, .grok)
        XCTAssertEqual(quota.remainingPercent, 66)
        XCTAssertEqual(reading.reset, ISO8601DateFormatter().date(from: "2032-01-08T14:30:00Z"))
    }

    func testChinesePremiumUsageCard() throws {
        let shanghai = try XCTUnwrap(TimeZone(identifier: "Asia/Shanghai"))
        let reading = try XCTUnwrap(GrokUsage(card: """
            每周 Premium 限额
            37%
            已使用
            重置2032年1月10日 15:40
            Grok Bot 37%
            额外使用积分
            """, dateTime: nil, now: now, timeZone: shanghai))
        XCTAssertEqual(reading.percentUsed, 37)
        XCTAssertEqual(reading.quota(account: "test", at: now).remainingPercent, 63)
        XCTAssertEqual(reading.reset, ISO8601DateFormatter().date(from: "2032-01-10T07:40:00Z"))
    }

    func testIncludedUsageGaugeCanSupplyMissingTotal() throws {
        let reading = try XCTUnwrap(GrokUsage(card: """
            Weekly Premium+ Limit
            Resets Jan 10 at 15:40
            Grok Bot 50%
            """, dateTime: nil, includedPercent: 37, now: now, timeZone: utc))
        XCTAssertEqual(reading.percentUsed, 37)
    }

    func testLocalizedRemainingAndExplicitTimestamp() throws {
        let reading = try XCTUnwrap(GrokUsage(card: """
            本周共享额度
            剩余 75％
            重置于 1 月 8 日
            """, dateTime: "2032-01-08T08:00:00.000+08:00", now: now, timeZone: utc))
        XCTAssertEqual(reading.percentUsed, 25)
        XCTAssertEqual(reading.reset, ISO8601DateFormatter().date(from: "2032-01-08T00:00:00Z"))
    }

    func testCombinedResetLineAndMonthRollover() throws {
        let december = try XCTUnwrap(ISO8601DateFormatter().date(from: "2032-12-30T01:00:00Z"))
        let reading = try XCTUnwrap(GrokUsage(card: """
            Weekly X Subscription Limit
            100% used · Resets Jan 2 at 04:30
            """, dateTime: nil, now: december, timeZone: utc))
        XCTAssertEqual(reading.percentUsed, 100)
        XCTAssertEqual(reading.reset, ISO8601DateFormatter().date(from: "2033-01-02T04:30:00Z"))
    }

    func testDoesNotInventTotalsOrAbsoluteReset() {
        XCTAssertNil(GrokUsage(card: """
            Weekly SuperGrok Limit
            Usage by product
            Grok Build 15%
            Resets in 2 days
            """, dateTime: nil, now: now, timeZone: utc))
        XCTAssertNil(GrokUsage(card: "Extra Usage Credits\n100% used", dateTime: nil, now: now))
        XCTAssertNil(GrokUsage(card: "Weekly usage\n240% used", dateTime: nil, now: now))
    }

    func testAutomaticReadingTakesPriorityAndReplacesOldConnection() throws {
        let reading = try XCTUnwrap(GrokUsage(card: "Weekly usage\n25% used", dateTime: nil, now: now))
        var manual = reading.quota(account: "manual", at: now)
        manual.source = .manual
        let previous = reading.quota(account: "old", at: now)
        var snapshot = Snapshot(quotas: [manual, previous], pins: [.grok: manual.widgetKey])
        let current = reading.quota(account: "new", at: now.addingTimeInterval(60))
        snapshot.applyGrok(current)
        XCTAssertEqual(snapshot.pinned(.grok), current)
        XCTAssertEqual(snapshot.quotas, [manual, current])
    }
}
