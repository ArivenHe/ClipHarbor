import SwiftUI
import AppKit
import Quartz

struct HistoryView: View {
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
    @FocusState private var searchFocused: Bool
    @FocusState private var noteFocused: Bool
    private var visible: [ClipItem] {
        let source = filter == "frequent" ? (learningEnabled ? FrequentContent.ranked(store.items, threshold: learningThreshold) : []) : store.items
        return source.filter { item in
            (filter == "all" || filter == "frequent" || (filter == "screenshots" ? item.screenshotURL != nil : (filter == "favorites" ? item.favorite : item.kind.rawValue == filter))) &&
            (filter != "files" || fileCategory == "all" || item.fileCategories.contains(fileCategory)) && item.matches(query)
        }
    }
    private var selected: ClipItem? { visible.first { $0.id == selection } }
    private var delegate: AppDelegate? { AppDelegate.shared }
    var body: some View {
        NavigationSplitView {
            List(selection: $filter) {
                Label("全部", systemImage: "tray.full").tag("all")
                Label("收藏", systemImage: "star").tag("favorites")
                Label("常用", systemImage: "sparkles").tag("frequent")
                Label("系统截图", systemImage: "camera.viewfinder").tag("screenshots")
                Section("类型") {
                    ForEach(ClipKind.allCases) { kind in Label(kind.title, systemImage: kind.symbol).tag(kind.rawValue) }
                }
            }.navigationSplitViewColumnWidth(min: 130, ideal: 150)
        } detail: {
            VStack(spacing: 0) {
                HStack {
                    Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                    TextField("搜索内容、文件名或备注", text: $query).textFieldStyle(.plain).focused($searchFocused)

                    if !query.isEmpty { Button { query = "" } label: { Image(systemName: "xmark.circle.fill") }.buttonStyle(.plain) }
                }
                .padding(12).glassEffect(.regular, in: .rect(cornerRadius: 14)).padding(12)
                if filter == "frequent" {
                    Text(learningEnabled ? FrequentContent.summary(store.items, threshold: learningThreshold) : "常用内容学习已关闭，可在设置中开启。")
                        .font(.callout).foregroundStyle(.secondary).padding(.horizontal, 12).padding(.bottom, 8)
                }
                if filter == "files" {
                    Picker("文件类型", selection: $fileCategory) {
                        Text("全部文件").tag("all")
                        ForEach(FileCategory.allCases) { Text($0.title).tag($0.rawValue) }
                    }.padding(.horizontal, 12).padding(.bottom, 8)
                }
                if visible.isEmpty {
                    ContentUnavailableView(query.isEmpty ? "等待你的下一次复制" : "没有匹配内容", systemImage: "doc.on.clipboard", description: Text(query.isEmpty ? "复制文本、图片或 Finder 文件后，它们会出现在这里。" : "尝试其他关键词或类型。"))
                } else {
                    HSplitView {
                        List(selection: $selection) {
                            ForEach(visible) { item in
                                row(item).tag(item.id)
                                    .contextMenu {
                                        Button("复制") { delegate?.use(item, quick: quick, paste: false) }
                                        if item.text != nil { Button("复制为纯文本") { delegate?.use(item, quick: quick, plain: true, paste: false) } }
                                        Button(item.favorite ? "取消收藏" : "收藏") { store.toggleFavorite(item.id) }
                                        if !item.fileURLs.isEmpty { Button("在 Finder 中显示") { NSWorkspace.shared.activateFileViewerSelecting(item.fileURLs) } }
                                        Button(item.excludedFromLearning == true ? "允许常用内容学习" : "不学习这条内容") { store.toggleLearningExclusion(item.id) }
                                        Button("删除", role: .destructive) { store.delete([item.id]) }
                                    }
                            }
                        }.frame(minWidth: 220)
                        if let selected { detail(selected).frame(minWidth: 200, idealWidth: 280) }
                    }
                }
                Divider()
                HStack {
                    Text(store.paused ? "记录已暂停" : "\(visible.count) 条记录").foregroundStyle(.secondary)
                    Spacer()
                    if let selected {
                        Button { store.toggleFavorite(selected.id) } label: { Image(systemName: selected.favorite ? "star.fill" : "star") }.help("收藏")
                        Button(UserDefaults.standard.bool(forKey: "autoPaste") && quick ? "粘贴" : "复制") { delegate?.use(selected, quick: quick) }

                    }
                }.padding(12)
            }
        }
        .toolbar {
            Toggle(isOn: $store.paused) { Label("暂停记录", systemImage: store.paused ? "play" : "pause") }.toggleStyle(.button)
            Button { confirmClear = true } label: { Label("清空历史", systemImage: "trash") }
            Button { delegate?.openSettings() } label: { Label("设置", systemImage: "gearshape") }
        }
        .onAppear { selection = visible.first?.id; if quick { searchFocused = true } }
        .onChange(of: visible.map(\.id)) { _, ids in if (selection.map { !ids.contains($0) } ?? true) { selection = ids.first } }
        .background(HistoryWindowReader { window in
            windowNumber = window.windowNumber
            if searchFocused { ShortcutManager.shared.searchWindow = window.windowNumber }
        })
        .onChange(of: searchFocused) { _, focused in
            if focused { ShortcutManager.shared.searchWindow = windowNumber }
            else if ShortcutManager.shared.searchWindow == windowNumber { ShortcutManager.shared.searchWindow = nil }
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
        case .search: searchFocused = true
        case .clearSearch: query = ""; searchFocused = true
        case .close: if quick { delegate?.closePanel() } else { NSApp.keyWindow?.performClose(nil) }
        case .clear: confirmClear = true
        case .note: if selected != nil { searchFocused = false; noteFocused = true }
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
            case .preview:
                if let url = selected.fileURLs.first { preview(url) }
                else if let name = selected.imageName { preview(store.imageDirectory.appendingPathComponent(name)) }
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
            Image(systemName: item.kind.symbol).foregroundStyle(.secondary).frame(width: 22)
            VStack(alignment: .leading, spacing: 5) {
                Text(item.title).lineLimit(2)
                HStack {
                    Text(item.kind.title)
                    if let source = item.source { Text(source) }
                    Text(item.date, style: .relative)
                }.font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
            Spacer(minLength: 0)
            if item.favorite { Image(systemName: "star.fill").foregroundStyle(.yellow) }
        }.padding(.vertical, 5)
    }
    @ViewBuilder private func detail(_ item: ClipItem) -> some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 12) {
                if let name = item.imageName, let image = NSImage(contentsOf: store.imageDirectory.appendingPathComponent(name)) {
                    Image(nsImage: image).resizable().scaledToFit().frame(maxHeight: 240)
                    Button("预览图片") { preview(store.imageDirectory.appendingPathComponent(name)) }
                } else if item.kind == .files {
                    ForEach(item.fileURLs, id: \.self) { url in
                        VStack(alignment: .leading, spacing: 4) {
                            Text(url.lastPathComponent).font(.headline)
                            Text(url.path).font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
                            if FileManager.default.fileExists(atPath: url.path) {
                                Button("快速预览") { preview(url) }
                            } else { Label("原文件不可用", systemImage: "exclamationmark.triangle").foregroundStyle(.orange) }
                        }
                    }
                } else { Text(item.text ?? "").textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading) }
                Divider()
                TextField("添加备注", text: Binding(get: { store.items.first { $0.id == item.id }?.note ?? "" }, set: { store.setNote(item.id, $0) })).focused($noteFocused)
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
}

@MainActor
final class PreviewController: NSObject, @preconcurrency QLPreviewPanelDataSource {
    static let shared = PreviewController()
    private var url: URL?
    func show(_ url: URL) {
        self.url = url
        guard let panel = QLPreviewPanel.shared() else { return }
        panel.dataSource = self
        panel.reloadData(); panel.makeKeyAndOrderFront(nil)
    }
    func numberOfPreviewItems(in panel: QLPreviewPanel!) -> Int { url == nil ? 0 : 1 }
    func previewPanel(_ panel: QLPreviewPanel!, previewItemAt index: Int) -> (any QLPreviewItem)! { url as NSURL? }
}
