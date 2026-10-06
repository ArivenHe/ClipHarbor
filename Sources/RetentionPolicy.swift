import Foundation

enum RetentionUnit: String, CaseIterable, Identifiable {
    case minutes, hours, days, weeks, forever
    var id: String { rawValue }
    var title: String {
        switch self { case .minutes: "分钟"; case .hours: "小时"; case .days: "天"; case .weeks: "周"; case .forever: "永久" }
    }
    var seconds: TimeInterval? {
        switch self { case .minutes: 60; case .hours: 3600; case .days: 86400; case .weeks: 604800; case .forever: nil }
    }
}
struct RetentionPolicy {
    static func migrate(_ defaults: UserDefaults) {
        guard defaults.object(forKey: "retention.unit") == nil else { return }
        let days = defaults.object(forKey: "retentionDays") == nil ? 30 : defaults.integer(forKey: "retentionDays")
        defaults.set(days == 0 ? "forever" : "days", forKey: "retention.unit")
        defaults.set(max(1, days), forKey: "retention.value")
    }
    static func lifetime(for kind: ClipKind, defaults: UserDefaults) -> TimeInterval? {
        let prefix = defaults.bool(forKey: "retention.\(kind.rawValue).override") ? "retention.\(kind.rawValue)" : "retention"
        let unit = RetentionUnit(rawValue: defaults.string(forKey: prefix + ".unit") ?? "days") ?? .days
        guard let seconds = unit.seconds else { return nil }
        let value = defaults.object(forKey: prefix + ".value") == nil ? 30 : defaults.integer(forKey: prefix + ".value")
        return Double(min(max(1, value), 100000)) * seconds
    }
    static func keeping(_ items: [ClipItem], defaults: UserDefaults, now: Date = Date()) -> [ClipItem] {
        let limit = max(1, defaults.integer(forKey: "historyLimit"))
        let exempt = defaults.object(forKey: "favoritesExempt") == nil || defaults.bool(forKey: "favoritesExempt")
        var count = 0
        return items.filter { item in
            if item.favorite && exempt { return true }
            if let duration = lifetime(for: item.displayKind, defaults: defaults), now.timeIntervalSince(item.date) >= duration { return false }
            // Expired records must not consume the capacity of remaining records.
            count += 1
            return count <= limit
        }
    }
}
