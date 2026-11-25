import Foundation

struct GrokUsage {
    let percentUsed: Double?
    let reset: Date?

    static let weeklyHeadingPattern = #"^(?:weekly(?:\s+(?:supergrok(?:\s+heavy)?|grok|(?:the\s+)?x subscription|premium(?:\s*\+|\s+plus)?|usage))?\s+limit|weekly usage|(?:本周|每周|周度|本週|每週)(?:共享|使用|用量|订阅|訂閱|\s|SuperGrok(?:\s+Heavy)?|Grok|Premium(?:\s*\+|\s+Plus)?)*(?:额度|額度|限额|限額|用量|上限))$"#

    init?(card: String, dateTime: String?, includedPercent: Double? = nil,
          now: Date, timeZone: TimeZone = .current) {
        let lines = card.components(separatedBy: .newlines)
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty }
        guard let heading = lines.firstIndex(where: Self.isWeeklyHeading) else { return nil }

        // The weekly total precedes the reset line and the product breakdown.
        let details = Array(lines.dropFirst(heading + 1).prefix(12))
        let resetIndex = details.firstIndex { $0.range(of: #"(?i)\breset(?:s|ting)?\b|重[置设設]"#, options: .regularExpression) != nil }
        var totalLines = Array(details.prefix(resetIndex ?? details.count))
        if let resetIndex,
           let resetMarker = details[resetIndex].range(of: #"(?i)\breset(?:s|ting)?\b|重[置设設]"#, options: .regularExpression) {
            totalLines.append(String(details[resetIndex][..<resetMarker.lowerBound]))
        }
        let total = totalLines.prefix { line in
            line.range(of: #"(?i)^(usage by|breakdown|extra usage|additional usage|grok (?:build|bot)|chat|api|imagine|voice)\b|^(按产品|额外用量|额外额度|额外使用积分)"#,
                       options: .regularExpression) == nil
        }.joined(separator: " ")
        let labelled = Self.captures(#"(?i)(\d{1,3}(?:[.,]\d+)?)\s*[%％]\s*(used|remaining|left|已使用|已用|剩[余餘])"#, in: total)
        let before = Self.captures(#"(?i)(used|remaining|left|已使用|已用|剩[余餘])(?:\s*[:：])?\s*(\d{1,3}(?:[.,]\d+)?)\s*[%％]"#, in: total)
        let percentages = Self.captures(#"(\d{1,3}(?:[.,]\d+)?)\s*[%％]"#, in: total)
        let value: Double?
        if labelled.count == 1 && percentages.count == 1 {
            value = Self.used(labelled[0][0], label: labelled[0][1])
        } else if before.count == 1 && percentages.count == 1 {
            value = Self.used(before[0][1], label: before[0][0])
        } else if percentages.count == 1 {
            value = Self.used(percentages[0][0], label: total)
        } else {
            value = nil
        }

        let reset: Date?
        if let resetIndex {
            let resetText = details[resetIndex...].prefix(2).joined(separator: " ")
            reset = dateTime.flatMap(Self.isoDate) ?? Self.resetDate(resetText, now: now, timeZone: timeZone)
        } else {
            reset = nil
        }
        let used = value ?? includedPercent.flatMap { (0...100).contains($0) ? $0 : nil }
        guard used != nil || reset != nil else { return nil }
        percentUsed = used
        self.reset = reset
    }

    func quota(account: String, at date: Date) -> Quota {
        Quota(service: .grok, feed: .grok, accountRef: account, scopeId: "grok-shared",
              windowId: "weekly", displayName: "本周共享额度", usedPercent: percentUsed,
              resetsAt: reset, source: .official, lastSuccessAt: date, lastAttemptAt: date)
    }

    private static func isWeeklyHeading(_ value: String) -> Bool {
        value.range(of: weeklyHeadingPattern, options: [.regularExpression, .caseInsensitive]) != nil
    }

    private static func used(_ text: String, label: String) -> Double? {
        guard let number = Double(text.replacingOccurrences(of: ",", with: ".")),
              (0...100).contains(number) else { return nil }
        let remaining = label.range(of: #"(?i)\b(?:remaining|left)\b|剩[余餘]"#, options: .regularExpression) != nil
        return remaining ? 100 - number : number
    }

    private static func captures(_ pattern: String, in input: String) -> [[String]] {
        guard let regex = try? NSRegularExpression(pattern: pattern) else { return [] }
        return regex.matches(in: input, range: NSRange(input.startIndex..., in: input)).map { match in
            (1..<match.numberOfRanges).map { index in
                Range(match.range(at: index), in: input).map { String(input[$0]) } ?? ""
            }
        }
    }

    private static func isoDate(_ value: String) -> Date? {
        let formatter = ISO8601DateFormatter()
        if let date = formatter.date(from: value) { return date }
        formatter.formatOptions.insert(.withFractionalSeconds)
        return formatter.date(from: value)
    }

    private static func resetDate(_ text: String, now: Date, timeZone: TimeZone) -> Date? {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timeZone
        let year = calendar.component(.year, from: now)
        let format = DateFormatter()
        format.locale = Locale(identifier: "en_US_POSIX")
        format.timeZone = timeZone
        format.isLenient = false

        let english = captures(#"(?i)\b([a-z]{3,9})\.?\s+(\d{1,2})(?:,?\s+(\d{4}))?\s*(?:,|at|·|-)?\s*(\d{1,2}:\d{2})(?:\s*([ap]m))?\b"#, in: text).first
        let chinese = captures(#"(?:(\d{4})\s*年\s*)?(\d{1,2})\s*月\s*(\d{1,2})\s*[日号]\s*(?:周[一二三四五六日天]\s*)?(上午|下午|凌晨|晚上)?\s*(\d{1,2}:\d{2})"#, in: text).first

        if let english {
            let explicitYear = Int(english[2])
            let meridiem = english[4].uppercased()
            format.dateFormat = "yyyy MMM d " + (meridiem.isEmpty ? "HH:mm" : "h:mm a")
            for candidate in explicitYear.map({ [$0] }) ?? [year, year + 1] {
                let value = "\(candidate) \(english[0].prefix(3)) \(english[1]) \(english[3])"
                if let date = format.date(from: meridiem.isEmpty ? value : value + " " + meridiem),
                   explicitYear != nil || date.timeIntervalSince(now) >= -86_400 {
                    return date
                }
            }
        }
        if let chinese {
            let explicitYear = Int(chinese[0])
            let components = chinese[4].split(separator: ":").compactMap { Int($0) }
            guard components.count == 2 else { return nil }
            let hour = components[0]
            let adjustedHour = ["下午", "晚上"].contains(chinese[3]) ? hour % 12 + 12 : (chinese[3] == "凌晨" ? hour % 12 : hour)
            format.dateFormat = "yyyy-M-d H:mm"
            for candidate in explicitYear.map({ [$0] }) ?? [year, year + 1] {
                if let date = format.date(from: "\(candidate)-\(chinese[1])-\(chinese[2]) \(adjustedHour):\(components[1])"),
                   explicitYear != nil || date.timeIntervalSince(now) >= -86_400 { return date }
            }
        }
        return nil
    }
}
