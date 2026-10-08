import Foundation

struct QuickPhrase: Codable, Identifiable, Equatable {
    var schemaVersion = 1
    var id = UUID().uuidString.lowercased()
    var kind = "phrase"
    var title = ""
    var body = ""
    var alias: String?
    var groupId: String?
    var pinned = false
    var hidden = false
    var originPresetId: String?
    var originPresetVersion: Int?
    var createdAt = ISO8601DateFormatter().string(from: Date())
    var updatedAt = ISO8601DateFormatter().string(from: Date())
    var revision = "0"
    var deletedAt: String?
    mutating func validate() throws {
        if kind == "preference" { return }
        title = title.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !title.isEmpty, title.unicodeScalars.count <= (kind == "group" ? 40 : 80), !title.unicodeScalars.contains(where: CharacterSet.controlCharacters.contains) else { throw SyncFailure("标题长度或分组名称无效。") }
        if kind == "group" { return }
        body = body.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n")
        guard !body.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, body.utf8.count <= 65_536 else { throw SyncFailure("正文不能为空，且不能超过 64 KiB。") }
        alias = alias?.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        if alias == "" { alias = nil }
        if let alias, alias.range(of: "^[a-z0-9_-]{1,32}$", options: .regularExpression) == nil { throw SyncFailure("别名需为 1–32 个字母、数字、下划线或短横线。") }
    }
}
struct PhraseOperationDTO: Codable, Equatable { var operationId: String; var type: String; var entity: QuickPhrase; var expectedRevision: String; var newGroup: QuickPhrase? }
struct PhraseFailureDTO: Codable, Identifiable, Equatable { var id: String; var operation: PhraseOperationDTO; var reason: String; var server: QuickPhrase? }
struct QuickPhraseLibrary: Codable, Equatable {
    var entities: [QuickPhrase] = []
    var failures: [PhraseFailureDTO] = []
    var pendingIds: [String] = []
    var status = "登录后可以维护自己的短语"
    var supported = false
}
struct PresetPhrase: Codable, Identifiable, Equatable {
    var presetId: String; var presetVersion: Int; var title: String; var body: String; var alias: String?; var categoryKey: String; var categoryName: String
    var id: String { presetId }
}
struct PresetCatalog: Codable {
    var schemaVersion: Int; var catalogVersion: Int; var locale: String; var phrases: [PresetPhrase]
    static func load(bundle: Bundle = .main) throws -> Self {
        guard let url = bundle.url(forResource: "quick-phrases.zh-CN", withExtension: "json") else { throw SyncFailure("预置短语资源缺失。") }
        let result = try JSONDecoder().decode(Self.self, from: Data(contentsOf: url))
        guard result.schemaVersion == 1, result.catalogVersion > 0, Set(result.phrases.map(\.id)).count == result.phrases.count else { throw SyncFailure("预置短语目录无效。") }
        for preset in result.phrases { var phrase = QuickPhrase(title: preset.title, body: preset.body, alias: preset.alias); try phrase.validate() }
        return result
    }
}
struct PhraseUsage: Codable, Equatable { var count = 0; var lastUsed = Date.distantPast }
struct PhraseRow: Identifiable, Equatable {
    var entity: QuickPhrase
    var preset: PresetPhrase?
    var category: String
    var pending = false
    var id: String { (preset == nil ? "personal:" : "preset:") + entity.id }
    var personal: Bool { preset == nil }
    var source: String { personal ? (entity.originPresetId == nil ? "我的短语" : "我的 · 自定义预置") : "预置短语" }
}
enum PhraseBrowser {
    static func rows(catalog: [PresetPhrase], library: QuickPhraseLibrary, section: String = "all", category: String = "all", query: String = "", showHidden: Bool = false, usage: [String: PhraseUsage] = [:]) -> [PhraseRow] {
        let entities = library.entities.filter { $0.deletedAt == nil }
        let groups = Dictionary(entities.filter { $0.kind == "group" }.map { ($0.id, $0.title) }, uniquingKeysWith: { first, _ in first })
        let personal = entities.filter { $0.kind == "phrase" }
        let sources = Set(personal.compactMap(\.originPresetId))
        let preferences = Dictionary(entities.filter { $0.kind == "preference" }.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        var rows: [PhraseRow] = section == "preset" ? [] : personal.map { PhraseRow(entity: $0, category: $0.groupId.flatMap { groups[$0] } ?? "未分组", pending: library.pendingIds.contains($0.id)) }
        if section != "mine" && section != "ungrouped" {
            rows += catalog.compactMap { preset in
                guard (showHidden || preferences[preset.id]?.hidden != true), (section == "preset" || !sources.contains(preset.id)) else { return nil }
                let e = QuickPhrase(id: preset.id, title: preset.title, body: preset.body, alias: preset.alias, pinned: preferences[preset.id]?.pinned ?? false, hidden: preferences[preset.id]?.hidden ?? false)
                return PhraseRow(entity: e, preset: preset, category: preset.categoryName)
            }
        }
        if section == "pinned" { rows = rows.filter { $0.entity.pinned } }
        if section == "ungrouped" { rows = rows.filter { $0.entity.groupId == nil } }
        if category != "all" { rows = rows.filter { $0.category == category } }
        var normalized = query.trimmingCharacters(in: .whitespacesAndNewlines).precomposedStringWithCanonicalMapping.lowercased()
        if normalized.hasPrefix("/") { normalized.removeFirst() }
        let words = normalized.split(whereSeparator: \.isWhitespace).map(String.init)
        if !words.isEmpty { rows = rows.filter { row in let fields = [row.entity.title, row.entity.alias ?? "", row.entity.body, row.category].map { $0.precomposedStringWithCanonicalMapping.lowercased() }; return words.allSatisfy { word in fields.contains { $0.contains(word) } } } }
        func rank(_ row: PhraseRow) -> Int { guard !normalized.isEmpty else { return 2 }; let values = [row.entity.title.lowercased(), row.entity.alias ?? ""]; if values.contains(normalized) { return 0 }; return values.contains { $0.hasPrefix(normalized) } ? 1 : 2 }
        return rows.sorted { a, b in
            if rank(a) != rank(b) { return rank(a) < rank(b) }
            if a.entity.pinned != b.entity.pinned { return a.entity.pinned }
            let au = usage[a.id]?.lastUsed ?? .distantPast, bu = usage[b.id]?.lastUsed ?? .distantPast
            if au != bu { return au > bu }
            let titleOrder = a.entity.title.localizedStandardCompare(b.entity.title)
            return titleOrder == .orderedSame ? a.id < b.id : titleOrder == .orderedAscending
        }
    }
}
