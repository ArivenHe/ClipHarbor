import Foundation
import Combine

@MainActor
final class QuickPhraseStore: ObservableObject {
    let sync: CloudSync
    let catalog: [PresetPhrase]
    @Published var error: String?
    @Published var requestedEditor: QuickPhrase?
    @Published private var statistics: [String: PhraseUsage] = [:]
    private var observation: AnyCancellable?
    private let directory: URL
    init(sync: CloudSync, directory: URL, catalog: [PresetPhrase]? = nil) {
        self.sync = sync; self.directory = directory.appendingPathComponent("QuickPhrases", isDirectory: true)
        if let catalog { self.catalog = catalog } else { do { self.catalog = try PresetCatalog.load().phrases } catch { self.catalog = []; self.error = error.localizedDescription } }
        do { try FileManager.default.createDirectory(at: self.directory, withIntermediateDirectories: true); let url = self.directory.appendingPathComponent("usage.json"); if FileManager.default.fileExists(atPath: url.path) { statistics = try JSONDecoder().decode([String: PhraseUsage].self, from: Data(contentsOf: url)) } } catch { self.error = "读取短语统计失败：\(error.localizedDescription)" }
        observation = sync.objectWillChange.sink { [weak self] _ in self?.objectWillChange.send() }
    }
    var space: String? { sync.phraseSpace }
    var canEdit: Bool { space != nil && sync.phraseLibrary.supported }
    var groups: [QuickPhrase] { sync.phraseLibrary.entities.filter { $0.kind == "group" }.sorted { $0.title.localizedStandardCompare($1.title) == .orderedAscending } }
    var usage: [String: PhraseUsage] { let prefix = (space ?? "public") + "|"; return Dictionary(statistics.filter { $0.key.hasPrefix(prefix) }.map { (String($0.key.dropFirst(prefix.count)), $0.value) }, uniquingKeysWith: { first, _ in first }) }
    func used(_ row: PhraseRow) { let key = (space ?? "public") + "|" + row.id; var value = statistics[key] ?? PhraseUsage(); value.count += 1; value.lastUsed = Date(); statistics[key] = value; do { try JSONEncoder().encode(statistics).write(to: directory.appendingPathComponent("usage.json"), options: .atomic) } catch { self.error = "内容已复制，保存使用统计失败：\(error.localizedDescription)" } }
    func save(_ draft: QuickPhrase, in expectedSpace: String, newGroup: QuickPhrase? = nil, delete: Bool = false) async throws {
        guard space == expectedSpace else { throw SyncFailure("账号已改变，输入保留在原账号草稿中。") }
        var entity = draft; if !delete { try entity.validate() }
        if !delete && entity.kind == "phrase", let alias = entity.alias, sync.phraseLibrary.entities.contains(where: { $0.kind == "phrase" && $0.id != entity.id && $0.alias == alias }) { throw SyncFailure("已有同名别名，请修改或清空。") }
        if !delete && entity.kind == "group", groups.contains(where: { $0.id != entity.id && $0.title.caseInsensitiveCompare(entity.title) == .orderedSame }) { throw SyncFailure("已有同名分组。") }
        try await sync.savePhrase(entity, space: expectedSpace, newGroup: newGroup, delete: delete)
    }
    func customize(_ preset: PresetPhrase) -> QuickPhrase {
        if let existing = sync.phraseLibrary.entities.first(where: { $0.kind == "phrase" && $0.originPresetId == preset.id }) { return existing }
        let group = groups.first { $0.title.caseInsensitiveCompare(preset.categoryName) == .orderedSame }
        return QuickPhrase(title: preset.title, body: preset.body, alias: preset.alias, groupId: group?.id, originPresetId: preset.id, originPresetVersion: preset.presetVersion)
    }
    func preference(_ presetId: String) -> QuickPhrase { sync.phraseLibrary.entities.first { $0.kind == "preference" && $0.id == presetId } ?? QuickPhrase(id: presetId, kind: "preference") }
    func requestNew(text: String = "") { requestedEditor = QuickPhrase(title: String(String.UnicodeScalarView((text.split(whereSeparator: \.isNewline).first(where: { !$0.trimmingCharacters(in: .whitespaces).isEmpty }).map(String.init) ?? "").unicodeScalars.prefix(80))), body: text) }
    private func draftURL(_ space: String, id: String) -> URL { directory.appendingPathComponent("draft-\(space)-\(id).json") }
    func retainDraft(_ entity: QuickPhrase, in space: String, groupName: String = "") { do { try JSONEncoder().encode(entity).write(to: draftURL(space, id: entity.id), options: .atomic); try groupName.write(to: draftURL(space, id: entity.id).appendingPathExtension("group"), atomically: true, encoding: .utf8) } catch { self.error = "保存草稿失败：\(error.localizedDescription)" } }
    func drafts(in space: String) -> [QuickPhrase] { ((try? FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil)) ?? []).filter { $0.pathExtension == "json" && $0.lastPathComponent.hasPrefix("draft-\(space)-") }.compactMap { url in (try? Data(contentsOf: url)).flatMap { try? JSONDecoder().decode(QuickPhrase.self, from: $0) } } }
    func draftGroupName(in space: String, id: String) -> String? { try? String(contentsOf: draftURL(space, id: id).appendingPathExtension("group"), encoding: .utf8) }
    func clearDraft(in space: String, id: String) { try? FileManager.default.removeItem(at: draftURL(space, id: id)); try? FileManager.default.removeItem(at: draftURL(space, id: id).appendingPathExtension("group")) }
}
