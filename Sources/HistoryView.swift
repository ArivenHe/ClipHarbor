import SwiftUI
import AppKit
import Quartz

struct HistoryView: View {
    private enum Focus: Hashable { case sidebar, search, fileFilter, records, preview, note, favorite, actions, copy, pause, clear, settings }
    @ObservedObject var store: ClipboardStore
    var quick: Bool
    @AppStorage("learningEnabled") private var learningEnabled = true
    @AppStorage("learningThreshold") private var learningThreshold = 2
    @State private var query = ""
    @State private var filter = "all"
    @State private var fileCategory = "all"
    @State private var selection: UUID?
    @State private var confirmClear = false
    @State private var windowNumber: Int?
    @State private var showActions = false
    @FocusState private var focus: Focus?
    private var visible: [ClipItem] {
        let source = filter == "frequent" ? (learningEnabled ? FrequentContent.ranked(store.visibleItems, threshold: learningThreshold) : []) : store.visibleItems
        return source.filter { item in
            (filter == "all" || filter == "frequent" || (filter == "screenshots" ? item.screenshotURL != nil : (filter == "favorites" ? item.favorite : item.displayKind.rawValue == filter))) &&
            (filter != "files" || fileCategory == "all" || item.fileCategories.contains(fileCategory)) && item.matches(query)
        }
    }
    private var selected: ClipItem? { visible.first { $0.id == selection } }
    private var delegate: AppDelegate? { AppDelegate.shared }
    private var previewURLs: [URL] {
        guard let selected else { return [] }
        if let name = selected.imageName { return [store.imageDirectory.appendingPathComponent(name)] }
        return selected.fileURLs.filter { FileManager.default.fileExists(atPath: $0.path) }
    }
    private var focusOrder: [Focus] {
        var order: [Focus] = [.sidebar, .search]
        if filter == "files" { order.append(.fileFilter) }
        if !visible.isEmpty { order.append(.records) }
        if selected != nil {
            if !previewURLs.isEmpty { order.append(.preview) }
            order += [.note, .favorite, .actions, .copy]
        }
        return order + [.pause, .clear, .settings]
    }
    var body: some View {
        NavigationSplitView {
            List(selection: $filter) {
                Label("全部", systemImage: "tray.full").tag("all")
                Label("收藏", systemImage: "star").tag("favorites")
                Label("常用", systemImage: "sparkles").tag("frequent")
                Button { delegate?.browserMode = "phrases" } label: { Label("快捷短语", systemImage: "text.bubble") }
                Label("系统截图", systemImage: "camera.viewfinder").tag("screenshots")
                Section("类型") {
                    ForEach(ClipKind.allCases) { kind in Label(kind.title, systemImage: kind.symbol).tag(kind.rawValue) }
                }
            }.navigationSplitViewColumnWidth(min: 130, ideal: 150)
                .focusable(interactions: .edit).focused($focus, equals: .sidebar)
                .onKeyPress(keys: [.upArrow, .downArrow]) { press in
                    guard press.modifiers.intersection([.command, .option, .control, .shift]).isEmpty else { return .ignored }
                    let filters = ["all", "favorites", "frequent", "screenshots"] + ClipKind.allCases.map(\.rawValue)
                    let index = filters.firstIndex(of: filter) ?? 0
                    filter = filters[min(max(index + (press.key == .upArrow ? -1 : 1), 0), filters.count - 1)]
                    return .handled
                }
                .onKeyPress(.rightArrow) { focus = visible.isEmpty ? .search : .records; return .handled }
        } detail: {
            VStack(spacing: 0) {
                HStack {
                    Spacer()
                    KeyboardToggle(isOn: $store.paused) { Label("暂停记录", systemImage: store.paused ? "play" : "pause") }
                        .toggleStyle(.button).focused($focus, equals: .pause)
                    KeyboardButton { confirmClear = true } label: { Label("清空历史", systemImage: "trash") }
                        .focused($focus, equals: .clear)
                    KeyboardButton { delegate?.openSettings() } label: { Label("设置", systemImage: "gearshape") }
                        .focused($focus, equals: .settings)
                }.labelStyle(.iconOnly).padding(.horizontal, 12).padding(.top, 8)
                HStack {
                    Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                    TextField("搜索内容、文件名或备注", text: $query).textFieldStyle(.plain).focused($focus, equals: .search)
                        .onSubmit { if let selected { delegate?.use(selected, quick: quick) } }
                        .onKeyPress(.downArrow) { if !visible.isEmpty { focus = .records }; return .handled }
                        .onKeyPress(.upArrow) { focus = .sidebar; return .handled }

                    if !query.isEmpty { KeyboardButton { query = ""; focus = .search } label: { Image(systemName: "xmark.circle.fill") }.buttonStyle(.plain).accessibilityLabel("清除搜索") }
                }
                .padding(12).glassEffect(.regular, in: .rect(cornerRadius: 14)).padding(12)
                if filter == "frequent" {
                    Text(learningEnabled ? FrequentContent.summary(store.items, threshold: learningThreshold) : "常用内容学习已关闭，可在设置中开启。")
                        .font(.callout).foregroundStyle(.secondary).padding(.horizontal, 12).padding(.bottom, 8)
                }
                if filter == "files" {
                    Picker("文件类型", selection: $fileCategory) {
                        Text("全部文件").tag("all")
                        ForEach(FileCategory.allCases.filter { $0 != .image }) { Text($0.title).tag($0.rawValue) }
                    }.keyboardPicker(selection: $fileCategory, values: ["all"] + FileCategory.allCases.filter { $0 != .image }.map(\.rawValue))
                        .focused($focus, equals: .fileFilter).padding(.horizontal, 12).padding(.bottom, 8)
                }
                if visible.isEmpty {
                    ContentUnavailableView(query.isEmpty ? "等待你的下一次复制" : "没有匹配内容", systemImage: "doc.on.clipboard", description: Text(query.isEmpty ? "复制文本、图片或 Finder 文件后，它们会出现在这里。" : "尝试其他关键词或类型。"))
                } else {
                    HSplitView {
                        ScrollViewReader { reader in
                        List(selection: $selection) {
                            ForEach(visible) { item in
                                row(item).tag(item.id).id(item.id)
                                    .onTapGesture(count: 2) {
                                        selection = item.id
                                        delegate?.use(item, quick: quick, paste: true)
                                    }
                                    .contextMenu {
                                        Button("复制") { delegate?.use(item, quick: quick, paste: false) }
                                        if [.text, .link].contains(item.kind), let text = item.text { Button("存为个人短语") { delegate?.phrases.requestNew(text: text); delegate?.openPhrases() } }
                                        if item.text != nil { Button("复制为纯文本") { delegate?.use(item, quick: quick, plain: true, paste: false) } }
                                        Button(item.favorite ? "取消收藏" : "收藏") { store.toggleFavorite(item.id) }
                                        if !item.fileURLs.isEmpty { Button("在 Finder 中显示") { NSWorkspace.shared.activateFileViewerSelecting(item.fileURLs) } }
                                        Button(item.excludedFromLearning == true ? "允许常用内容学习" : "不学习这条内容") { store.toggleLearningExclusion(item.id) }
                                        Button("删除", role: .destructive) { store.delete([item.id]) }
                                    }
                            }
                        }.frame(minWidth: 220)
                            .focusable(interactions: .edit).focused($focus, equals: .records)
                            .onKeyPress(keys: [.upArrow, .downArrow]) { press in
                                guard press.modifiers.intersection([.command, .option, .control, .shift]).isEmpty else { return .ignored }
                                move(press.key == .upArrow ? -1 : 1); return .handled
                            }
                            .onKeyPress(.return) { if let selected { delegate?.use(selected, quick: quick) }; return .handled }
                            .onKeyPress(.space) { showPreview(); return .handled }
                            .onKeyPress(.leftArrow) { focus = .sidebar; return .handled }
                            .onKeyPress(.rightArrow) { if selected != nil { focus = .note }; return .handled }
                            .onChange(of: selection) { _, id in if let id { reader.scrollTo(id) } }
                        }
                        if let selected { detail(selected).frame(minWidth: 200, idealWidth: 280) }
                    }
                }
                Divider()
                HStack {
                    Text(store.paused ? "记录已暂停" : "\(visible.count) 条记录").foregroundStyle(.secondary)
                    Spacer()
                    if let selected {
                        if !previewURLs.isEmpty {
                            KeyboardButton("预览") { showPreview() }.focused($focus, equals: .preview)
                        }
                        KeyboardButton { store.toggleFavorite(selected.id) } label: { Image(systemName: selected.favorite ? "star.fill" : "star") }
                            .help("收藏").accessibilityLabel(selected.favorite ? "取消收藏" : "收藏").focused($focus, equals: .favorite)
                        KeyboardButton { showActions = true } label: { Image(systemName: "ellipsis") }
                            .accessibilityLabel("更多操作").focused($focus, equals: .actions)
                            .popover(isPresented: $showActions) {
                                RecordActionsView(item: selected, store: store, quick: quick) { showActions = false; focus = .actions }
                            }
                        KeyboardButton(UserDefaults.standard.bool(forKey: "autoPaste") && quick ? "粘贴" : "复制") { delegate?.use(selected, quick: quick) }
                            .focused($focus, equals: .copy)
                    }
                }.padding(12)
                Text("Tab 切换 · ↑↓ 选择 · 回车使用 · 空格预览 · 双击粘贴")
                    .font(.caption).foregroundStyle(.secondary).padding(.bottom, 8)
            }
        }
        .onAppear { selection = visible.first?.id; focus = .search }
        .onChange(of: visible.map(\.id)) { _, ids in
            if (selection.map { !ids.contains($0) } ?? true) { selection = ids.first }
            if let focus, !focusOrder.contains(focus) { self.focus = .search }
        }
        .background(HistoryWindowReader { window in
            windowNumber = window.windowNumber
            window.autorecalculatesKeyViewLoop = true
        })
        .onKeyPress(.tab, phases: .down) { press in
            guard !showActions, let focus, let index = focusOrder.firstIndex(of: focus) else { return .ignored }
            let direction = press.modifiers.contains(.shift) ? -1 : 1
            self.focus = focusOrder[(index + direction + focusOrder.count) % focusOrder.count]
            return .handled
        }
        .onKeyPress(.escape) {
            if showActions { showActions = false; focus = .actions }
            else if focus == .note { focus = .records }
            else if !query.isEmpty { query = ""; focus = .search }
            else { perform(.close) }
            return .handled
        }
        .onReceive(NotificationCenter.default.publisher(for: .clipShortcut)) { notification in
            guard let number = notification.userInfo?["window"] as? Int, number == windowNumber,
                  let action = notification.object as? ShortcutAction else { return }
            perform(action)
        }
        .alert("清空普通历史？", isPresented: $confirmClear) {
            Button("取消", role: .cancel) {}
            Button("清空并保留收藏", role: .destructive) { store.clear(keepFavorites: true) }
        } message: { Text("会删除历史及图片缓存。原文件不会被删除。") }
        .alert("拾贴", isPresented: Binding(get: { store.error != nil }, set: { if !$0 { store.error = nil } })) {
            Button("好") { store.error = nil }
        } message: { Text(store.error ?? "") }
    }
    private func perform(_ action: ShortcutAction) {
        if let target = action.filter { filter = target.kind; fileCategory = target.category; return }
        switch action {
        case .next: move(1)
        case .previous: move(-1)
        case .search: focus = .search
        case .clearSearch: query = ""; focus = .search
        case .close: if quick { delegate?.closePanel() } else { NSApp.keyWindow?.performClose(nil) }
        case .clear: confirmClear = true
        case .note: if selected != nil { focus = .note }
        default:
            guard let selected else { return }
            switch action {
            case .use: delegate?.use(selected, quick: quick)
            case .paste: delegate?.use(selected, quick: quick, paste: true)
            case .copy: delegate?.use(selected, quick: quick, paste: false)
            case .copyPlain: delegate?.use(selected, quick: quick, plain: true, paste: false)
            case .excludeLearning: store.toggleLearningExclusion(selected.id)
            case .favorite: store.toggleFavorite(selected.id)
            case .delete: store.delete([selected.id])
            case .reveal: if !selected.fileURLs.isEmpty { NSWorkspace.shared.activateFileViewerSelecting(selected.fileURLs) }
            case .preview: showPreview()
            default: break
            }
        }
    }
    private func move(_ offset: Int) {
        guard !visible.isEmpty else { return }
        let index = visible.firstIndex { $0.id == selection } ?? 0
        selection = visible[min(max(index + offset, 0), visible.count - 1)].id
    }
    private func row(_ item: ClipItem) -> some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: item.displayKind.symbol).foregroundStyle(.secondary).frame(width: 22)
            VStack(alignment: .leading, spacing: 5) {
                Text(item.title).lineLimit(2)
                HStack {
                    Text(item.displayKind.title)
                    if let source = item.source { Text(source) }
                    Text(item.date, style: .relative)
                }.font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
            Spacer(minLength: 0)
            if item.favorite { Image(systemName: "star.fill").foregroundStyle(.yellow) }
        }.padding(.vertical, 5).contentShape(Rectangle())
    }
    @ViewBuilder private func detail(_ item: ClipItem) -> some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 12) {
                if let name = item.imageName, let image = NSImage(contentsOf: store.imageDirectory.appendingPathComponent(name)) {
                    Image(nsImage: image).resizable().scaledToFit().frame(maxHeight: 240)
                        .accessibilityLabel(item.title)
                } else if item.kind == .files {
                    ForEach(item.fileURLs, id: \.self) { url in
                        VStack(alignment: .leading, spacing: 4) {
                            Text(url.lastPathComponent).font(.headline)
                            Text(url.path).font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
                            if FileManager.default.fileExists(atPath: url.path) {
                                FilePreview(url: url).frame(maxWidth: .infinity).frame(height: 260)
                                    .accessibilityLabel("\(url.lastPathComponent)的内容预览")
                            } else { Label("原文件不可用", systemImage: "exclamationmark.triangle").foregroundStyle(.orange) }
                        }
                    }
                } else { Text(item.text ?? "").textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading) }
                Divider()
                TextField("添加备注", text: Binding(get: { store.items.first { $0.id == item.id }?.note ?? "" }, set: { store.setNote(item.id, $0) }))
                    .focused($focus, equals: .note).onSubmit { focus = .records }
                if learningEnabled {
                    Text("记录 \(item.captures) 次 · 使用 \(item.uses) 次").font(.caption).foregroundStyle(.secondary)
                }
                if let screenshot = item.screenshotURL {
                    Text("系统截图已保存到拾贴缓存").font(.caption)
                    Text(screenshot.path).font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
                }
                Text(item.date.formatted()).font(.caption).foregroundStyle(.secondary)
            }.padding()
        }
    }
    private func preview(_ url: URL) { PreviewController.shared.show(url) }
    private func showPreview() { if !previewURLs.isEmpty { PreviewController.shared.show(previewURLs) } }
}

@MainActor
final class PreviewController: NSObject, @preconcurrency QLPreviewPanelDataSource {
    static let shared = PreviewController()
    private var urls: [URL] = []
    func show(_ url: URL) { show([url]) }
    func show(_ urls: [URL]) {
        self.urls = urls
        guard let panel = QLPreviewPanel.shared() else { return }
        panel.dataSource = self
        panel.reloadData(); panel.currentPreviewItemIndex = 0; panel.makeKeyAndOrderFront(nil)
    }
    func numberOfPreviewItems(in panel: QLPreviewPanel!) -> Int { urls.count }
    func previewPanel(_ panel: QLPreviewPanel!, previewItemAt index: Int) -> (any QLPreviewItem)! {
        guard urls.indices.contains(index) else { return nil }
        return urls[index] as NSURL
    }
}

private struct RecordActionsView: View {
    private enum Action: String, Hashable {
        case copy = "复制", copyPlain = "复制为纯文本", paste = "粘贴到原应用", favorite = "收藏／取消收藏"
        case savePhrase = "存为个人短语"
        case reveal = "在 Finder 中显示", learning = "允许／排除常用内容学习", delete = "删除记录"
    }
    let item: ClipItem
    @ObservedObject var store: ClipboardStore
    let quick: Bool
    var dismiss: () -> Void
    @FocusState private var focus: Action?
    private var actions: [Action] {
        var result: [Action] = [.copy, .paste]
        if [.text, .link].contains(item.kind), item.text != nil { result += [.copyPlain, .savePhrase] }
        result.append(.favorite)
        if !item.fileURLs.isEmpty { result.append(.reveal) }
        return result + [.learning, .delete]
    }
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            ForEach(actions, id: \.self) { action in
                KeyboardButton(action.rawValue, role: action == .delete ? .destructive : nil) { perform(action) }
                    .focused($focus, equals: action).frame(maxWidth: .infinity, alignment: .leading)
            }
        }.padding(12).frame(width: 235)
            .onAppear { focus = actions.first }
            .onKeyPress(.escape) { dismiss(); return .handled }
            .onKeyPress(keys: [.tab, .upArrow, .downArrow]) { press in
                let index = focus.flatMap { actions.firstIndex(of: $0) } ?? 0
                let backward = press.key == .upArrow || press.modifiers.contains(.shift)
                focus = actions[(index + (backward ? -1 : 1) + actions.count) % actions.count]
                return .handled
            }
    }
    private func perform(_ action: Action) {
        dismiss()
        switch action {
        case .copy: AppDelegate.shared?.use(item, quick: quick, paste: false)
        case .copyPlain: AppDelegate.shared?.use(item, quick: quick, plain: true, paste: false)
        case .paste: AppDelegate.shared?.use(item, quick: quick, paste: true)
        case .savePhrase: AppDelegate.shared?.phrases.requestNew(text: item.text ?? ""); AppDelegate.shared?.openPhrases()
        case .favorite: store.toggleFavorite(item.id)
        case .reveal: NSWorkspace.shared.activateFileViewerSelecting(item.fileURLs)
        case .learning: store.toggleLearningExclusion(item.id)
        case .delete: store.delete([item.id])
        }
    }
}
