import Foundation

struct FrequentContent {
    static func eligible(_ item: ClipItem) -> Bool {
        guard item.excludedFromLearning != true else { return false }
        guard let text = item.text else { return true }
        let sensitive = #"(?i)(password|passwd|secret|token|authorization|api[_-]?key|验证码|密码)\s*[:=]|\bsk-[A-Za-z0-9_-]{12,}|\bBearer\s+\S+"#
        if text.range(of: sensitive, options: .regularExpression) != nil { return false }
        let value = text.trimmingCharacters(in: .whitespacesAndNewlines)
        // Avoid promoting one-time codes and opaque strings as frequently used text.
        if value.range(of: #"^\d{4,8}$"#, options: .regularExpression) != nil { return false }
        if value.range(of: #"^[A-Za-z0-9_+/=-]{20,}$"#, options: .regularExpression) != nil { return false }
        return true
    }
    static func ranked(_ items: [ClipItem], threshold: Int) -> [ClipItem] {
        items.filter { eligible($0) && $0.captures + $0.uses >= max(2, threshold) }
            .sorted {
                let left = $0.captures + $0.uses * 3
                let right = $1.captures + $1.uses * 3
                if left != right { return left > right }
                return ($0.lastUsedAt ?? $0.date) > ($1.lastUsedAt ?? $1.date)
            }
    }
    static func summary(_ items: [ClipItem], threshold: Int) -> String {
        let frequent = ranked(items, threshold: threshold)
        guard !frequent.isEmpty else { return "继续复制和使用内容，拾贴会在本机整理你的常用项。" }
        let groups = Dictionary(grouping: frequent, by: \.displayKind)
        return "已整理 \(frequent.count) 个常用项：" + ClipKind.allCases.compactMap { kind in
            groups[kind].map { "\(kind.title) \($0.count) 个" }
        }.joined(separator: "、") + "。排序综合重复复制与使用次数。"
    }
}
