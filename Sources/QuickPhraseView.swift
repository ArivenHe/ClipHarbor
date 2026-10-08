import SwiftUI
import AppKit

struct ClipBrowserView: View {
    @ObservedObject var delegate: AppDelegate
    let quick: Bool
    var body: some View {
        VStack(spacing: 0) {
            Picker("内容", selection: $delegate.browserMode) { Text("历史").tag("history"); Text("快捷短语").tag("phrases") }.pickerStyle(.segmented).frame(width: 250).padding(10)
            Divider()
            if delegate.browserMode == "phrases" { QuickPhraseView(store: delegate.phrases, quick: quick) }
            else { HistoryView(store: delegate.store, quick: quick) }
        }
    }
}
private struct PhraseEditorRequest: Identifiable { let id = UUID(); var entity: QuickPhrase; let space: String }
private struct PhraseConfirmation: Identifiable { let id = UUID(); let title: String; let message: String; let action: () -> Void }

struct QuickPhraseView: View {
    @ObservedObject var store: QuickPhraseStore
    let quick: Bool
    @State private var section = "all"
    @State private var category = "all"
    @State private var query = ""
    @State private var showHidden = false
    @State private var selection: String?
    @State private var editor: PhraseEditorRequest?
    @State private var confirmation: PhraseConfirmation?
    @FocusState private var focus: Focus?
    private enum Focus { case search, list }
    private var rows: [PhraseRow] { PhraseBrowser.rows(catalog: store.catalog, library: store.sync.phraseLibrary, section: section, category: category, query: query, showHidden: showHidden, usage: store.usage) }
    private var selected: PhraseRow? { rows.first { $0.id == selection } }
    private var categories: [String] { Array(Set(store.catalog.map(\.categoryName) + store.groups.map(\.title))).sorted { $0.localizedStandardCompare($1) == .orderedAscending } }
    var body: some View {
        NavigationSplitView {
            List(selection: $section) {
                Label("全部", systemImage: "text.bubble").tag("all")
                Label("我的短语", systemImage: "person.crop.circle").tag("mine")
                Label("预置短语", systemImage: "square.stack").tag("preset")
                Label("置顶", systemImage: "pin").tag("pinned")
                Label("未分组", systemImage: "folder").tag("ungrouped")
            }.navigationSplitViewColumnWidth(min: 130, ideal: 155)
        } detail: {
            VStack(spacing: 0) {
                HStack {
                    TextField("搜索标题、别名或正文", text: $query).textFieldStyle(.roundedBorder).focused($focus, equals: .search)
                        .onKeyPress(.downArrow) { if selection == nil { selection = rows.first?.id }; focus = .list; return .handled }
                    Button("新建") { store.requestNew(); openRequested() }.accessibilityLabel("新建个人短语")
                    Menu("分组") {
                        Button("新建个人分组") { openEditor(QuickPhrase(kind: "group")) }
                        ForEach(store.groups) { group in
                            Menu(group.title) {
                                Button("重命名") { openEditor(group) }
                                Button("删除分组", role: .destructive) {
                                    let count = store.sync.phraseLibrary.entities.filter { $0.groupId == group.id }.count
                                    confirm("删除分组？", "\(count) 条个人短语会移到未分组。") { save(group, delete: true) }
                                }
                            }
                        }
                    }.disabled(!store.canEdit)
                }.padding(12)
                HStack {
                    Picker("分类／分组", selection: $category) { Text("全部分类").tag("all"); ForEach(categories, id: \.self) { Text($0).tag($0) } }.frame(maxWidth: 250)
                    if section == "preset" { Toggle("显示已隐藏", isOn: $showHidden).toggleStyle(.checkbox) }
                    Spacer()
                    Text(store.space == nil ? "预置短语" : store.sync.config.username).foregroundStyle(.secondary)
                    Button(store.space == nil ? "登录账号" : "同步设置") { login() }
                }.padding(.horizontal, 12).padding(.bottom, 10)
                if let error = store.error { HStack { Text(error).foregroundStyle(.red).textSelection(.enabled); Spacer(); Button("关闭提示") { store.error = nil } }.padding(12) }
                if rows.isEmpty { ContentUnavailableView("没有匹配的短语", systemImage: "text.bubble", description: Text(section == "mine" && store.space == nil ? "登录账号后可以维护个人短语。" : "调整关键词，或新建自己的短语。")) }
                else {
                    HSplitView {
                        List(selection: $selection) {
                            ForEach(rows) { row in
                                HStack(alignment: .top) {
                                    if row.entity.pinned { Image(systemName: "pin.fill").foregroundStyle(.tint).accessibilityLabel("已置顶") }
                                    VStack(alignment: .leading, spacing: 5) {
                                        Text(row.entity.title).font(.headline).lineLimit(1)
                                        Text(row.entity.body.replacingOccurrences(of: "\n", with: " ")).lineLimit(1).foregroundStyle(.secondary)
                                        Text("\(row.source) · \(row.category)\(row.pending ? " · 待同步" : "")\(row.entity.hidden ? " · 已隐藏" : "")").font(.caption).foregroundStyle(.secondary)
                                    }
                                }.padding(.vertical, 5).contentShape(Rectangle()).tag(row.id)
                                .onTapGesture(count: 2) { selection = row.id; use(row, paste: true) }
                                .contextMenu { Button("复制") { use(row, paste: false) }; Button(row.personal ? "编辑" : "自定义") { edit(row) } }
                            }
                        }.frame(minWidth: 220).focused($focus, equals: .list)
                            .onKeyPress(.return) { if let selected { use(selected) }; return .handled }
                        if let selected { details(selected).frame(minWidth: 240, idealWidth: 330) }
                    }
                }
                Divider()
                HStack {
                    Text("\(rows.count) 条短语").foregroundStyle(.secondary)
                    if let space = store.space {
                        let drafts = store.drafts(in: space)
                        if !drafts.isEmpty { Menu("未保存草稿 (\(drafts.count))") { ForEach(drafts) { draft in Button(draft.title.isEmpty ? "未命名短语" : draft.title) { openEditor(draft) } } } }
                    }
                    Spacer()
                    if let selected {
                        Button("复制") { use(selected, paste: false) }
                        Button("粘贴") { use(selected, paste: true) }.buttonStyle(.borderedProminent)
                    }
                }.padding(12)
                Text(store.sync.phraseLibrary.status).font(.caption).foregroundStyle(.secondary).padding(.bottom, 6)
                if !store.sync.phraseLibrary.failures.isEmpty { failures }
            }
        }
        .onAppear { focus = .search; Task { try? await store.sync.reloadPhrases() }; openRequested() }
        .onChange(of: store.requestedEditor) { _, _ in openRequested() }
        .onChange(of: store.canEdit) { _, value in if value { openRequested() } }
        .onChange(of: store.space) { old, _ in if old != nil { store.requestedEditor = nil }; editor = nil; selection = nil; query = ""; category = "all"; showHidden = false }
        .onChange(of: rows.map(\.id)) { _, ids in if let selection, !ids.contains(selection) { self.selection = nil } }
        .onKeyPress(.escape) { if editor != nil { return .ignored }; if !query.isEmpty { query = ""; focus = .search } else if quick { AppDelegate.shared?.closePanel() } else { NSApp.keyWindow?.performClose(nil) }; return .handled }
        .background(Button("搜索") { focus = .search }.keyboardShortcut("f", modifiers: .command).hidden())
        .sheet(item: $editor) { request in QuickPhraseEditor(store: store, request: request) }
        .alert(item: $confirmation) { request in Alert(title: Text(request.title), message: Text(request.message), primaryButton: .default(Text("确认"), action: request.action), secondaryButton: .cancel()) }
    }
    private var failures: some View {
        DisclosureGroup("\(store.sync.phraseLibrary.failures.count) 份冲突／未同步草稿") {
            ScrollView {
                ForEach(store.sync.phraseLibrary.failures) { failure in
                    VStack(alignment: .leading, spacing: 8) {
                        Text(failure.operation.entity.title).font(.headline)
                        Text(failure.reason).font(.caption).foregroundStyle(.secondary)
                        Text("我的版本：\n\(failure.operation.entity.body)").textSelection(.enabled)
                        if let server = failure.server { Text("服务器版本：\n\(server.body)").textSelection(.enabled) }
                        HStack {
                            Button("保留服务器版本") { confirm("放弃我的草稿？", "保留服务器内容，删除这份本机冲突草稿。") { resolve(failure, "server") } }
                            if failure.server?.deletedAt == nil, failure.server?.id == failure.operation.entity.id { Button("用我的版本替换") { confirm("替换服务器版本？", "会更新此账号其他设备上的内容。") { resolve(failure, "mine") } } }
                            if failure.operation.entity.kind == "phrase" { Button("另存为个人短语") { resolve(failure, "copy") } }
                        }
                        Divider()
                    }.padding(8)
                }
            }.frame(maxHeight: 220)
        }.padding(12)
    }
    @ViewBuilder private func details(_ row: PhraseRow) -> some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 12) {
                Text(row.entity.title).font(.title2).textSelection(.enabled)
                Text(row.source + " · " + row.category).font(.caption).foregroundStyle(.secondary)
                Text(row.entity.body).textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading)
                Divider()
                Text("使用 \(store.usage[row.id]?.count ?? 0) 次").font(.caption).foregroundStyle(.secondary)
                HStack { Button(row.personal ? "编辑" : "自定义") { edit(row) }; Button(row.entity.pinned ? "取消置顶" : "置顶") { pin(row) }.disabled(!store.canEdit) }
                if row.personal {
                    Button("复制一份") { var copy = row.entity; copy.id = UUID().uuidString.lowercased(); copy.revision = "0"; copy.title = String(copy.title.prefix(70)) + " 副本"; copy.originPresetId = nil; copy.originPresetVersion = nil; copy.alias = nil; openEditor(copy) }.disabled(!store.canEdit)
                    if let sourceId = row.entity.originPresetId {
                        if let preset = store.catalog.first(where: { $0.id == sourceId }) {
                            DisclosureGroup((row.entity.originPresetVersion ?? 0) < preset.presetVersion ? "预置有更新，查看原文" : "查看预置原文") { Text(preset.body).textSelection(.enabled) }
                            Button("恢复预置正文") { confirm("恢复预置正文？", "会替换正文，保留个人标题、别名、分组和置顶。") { var e = row.entity; e.body = preset.body; e.originPresetVersion = preset.presetVersion; save(e) } }.disabled(!store.canEdit)
                        } else { Text("原预置已下架").font(.caption).foregroundStyle(.secondary) }
                    }
                    Button("删除个人短语", role: .destructive) { confirm("删除个人短语？", "会从此账号的其他设备删除。\(row.entity.originPresetId == nil ? "" : "对应预置未隐藏时会重新展示。")") { save(row.entity, delete: true) } }.disabled(!store.canEdit)
                } else {
                    Button(row.entity.hidden ? "恢复显示" : "隐藏这条预置短语") { var e = store.preference(row.entity.id); e.hidden.toggle(); save(e) }.disabled(!store.canEdit)
                    if let personal = store.sync.phraseLibrary.entities.first(where: { $0.originPresetId == row.entity.id }) { Button("编辑已有个人版本") { openEditor(personal) } }
                }
            }.padding(16)
        }
    }
    private func login() { UserDefaults.standard.set("sync", forKey: "settingsTab"); AppDelegate.shared?.openSettings() }
    private func openRequested() { guard let entity = store.requestedEditor else { return }; if store.canEdit, let space = store.space { editor = PhraseEditorRequest(entity: entity, space: space); store.requestedEditor = nil } else { login() } }
    private func openEditor(_ entity: QuickPhrase) { if store.canEdit, let space = store.space { editor = PhraseEditorRequest(entity: entity, space: space) } else if store.space != nil { store.error = store.sync.phraseLibrary.status } else { store.requestedEditor = entity; login() } }
    private func edit(_ row: PhraseRow) { openEditor(row.preset.map(store.customize) ?? row.entity) }
    private func pin(_ row: PhraseRow) { var e = row.personal ? row.entity : store.preference(row.entity.id); e.pinned.toggle(); save(e) }
    private func use(_ row: PhraseRow, paste: Bool? = nil) { if AppDelegate.shared?.usePhrase(row.entity.body, quick: quick, paste: paste) == true { store.used(row) } }
    private func save(_ entity: QuickPhrase, delete: Bool = false) { guard let space = store.space else { login(); return }; Task { do { try await store.save(entity, in: space, delete: delete) } catch { store.error = error.localizedDescription } } }
    private func resolve(_ failure: PhraseFailureDTO, _ choice: String) { guard let space = store.space else { return }; Task { do { try await store.sync.resolvePhrase(failure.id, choice: choice, space: space) } catch { store.error = error.localizedDescription } } }
    private func confirm(_ title: String, _ message: String, action: @escaping () -> Void) { confirmation = PhraseConfirmation(title: title, message: message, action: action) }
}

private struct QuickPhraseEditor: View {
    @ObservedObject var store: QuickPhraseStore
    let request: PhraseEditorRequest
    @Environment(\.dismiss) private var dismiss
    @State private var draft: QuickPhrase
    @State private var newGroupName = ""
    @State private var error = ""
    @State private var busy = false
    @State private var finished = false
    @State private var confirmDiscard = false
    init(store: QuickPhraseStore, request: PhraseEditorRequest) { self.store = store; self.request = request; _draft = State(initialValue: request.entity); _newGroupName = State(initialValue: store.draftGroupName(in: request.space, id: request.entity.id)) }
    private var dirty: Bool { draft != request.entity || !newGroupName.isEmpty }
    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text(draft.kind == "group" ? "个人分组" : "个人短语").font(.title2)
            Text("保存到账号：\(store.sync.config.username)").foregroundStyle(.secondary)
            TextField(draft.kind == "group" ? "分组名称" : "标题", text: $draft.title).textFieldStyle(.roundedBorder)
            if draft.kind == "phrase" {
                Text("正文").font(.headline)
                TextEditor(text: $draft.body).font(.body).frame(minHeight: 180).border(.secondary.opacity(0.3)).accessibilityLabel("短语正文")
                TextField("检索别名（可选）", text: Binding(get: { draft.alias ?? "" }, set: { draft.alias = $0 })).textFieldStyle(.roundedBorder)
                Picker("分组", selection: Binding(get: { draft.groupId ?? "" }, set: { draft.groupId = $0.isEmpty ? nil : $0 })) { Text("未分组").tag(""); ForEach(store.groups) { Text($0.title).tag($0.id) } }
                TextField("新建分组（可选）", text: $newGroupName).textFieldStyle(.roundedBorder)
                Toggle("置顶", isOn: $draft.pinned)
                if draft.originPresetId != nil { Text("来自预置短语，保存后仅修改你的个人版本。").font(.caption).foregroundStyle(.secondary) }
            }
            if !error.isEmpty { Text(error).foregroundStyle(.red).textSelection(.enabled) }
            HStack { Button("取消") { if dirty { confirmDiscard = true } else { finished = true; dismiss() } }.keyboardShortcut(.cancelAction); Spacer(); if busy { ProgressView().controlSize(.small) }; Button("保存") { save() }.keyboardShortcut("s", modifiers: .command).buttonStyle(.borderedProminent) }.disabled(busy)
        }.padding(24).frame(width: 540).interactiveDismissDisabled(dirty || busy)
        .task(id: draft) { if dirty { try? await Task.sleep(for: .milliseconds(250)); if !Task.isCancelled { store.retainDraft(draft, in: request.space, groupName: newGroupName) } } }
        .onDisappear { if dirty && !finished { store.retainDraft(draft, in: request.space, groupName: newGroupName) } }
        .alert("放弃未保存改动？", isPresented: $confirmDiscard) { Button("继续编辑", role: .cancel) {}; Button("放弃", role: .destructive) { store.clearDraft(in: request.space, id: draft.id); finished = true; dismiss() } }
    }
    private func save() {
        busy = true; error = ""
        Task { @MainActor in
            defer { busy = false }
            do {
                var entity = draft; var group: QuickPhrase?
                let groupName = newGroupName.trimmingCharacters(in: .whitespacesAndNewlines)
                if !groupName.isEmpty { if let existing = store.groups.first(where: { $0.title.caseInsensitiveCompare(groupName) == .orderedSame }) { entity.groupId = existing.id } else { group = QuickPhrase(kind: "group", title: groupName); entity.groupId = group?.id } }
                else if entity.kind == "phrase", entity.revision == "0", let presetId = entity.originPresetId, entity.groupId == nil, let preset = store.catalog.first(where: { $0.id == presetId }) {
                    if let existing = store.groups.first(where: { $0.title == preset.categoryName }) { entity.groupId = existing.id } else { group = QuickPhrase(kind: "group", title: preset.categoryName); entity.groupId = group?.id }
                }
                try await store.save(entity, in: request.space, newGroup: group); store.clearDraft(in: request.space, id: draft.id); finished = true; dismiss()
            } catch { self.error = error.localizedDescription; store.retainDraft(draft, in: request.space, groupName: newGroupName) }
        }
    }
}
