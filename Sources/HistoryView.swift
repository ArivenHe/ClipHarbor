import SwiftUI
import AppKit
import Quartz

struct HistoryView: View {
    @ObservedObject var store: ClipboardStore
    var quick: Bool
    @State private var query = ""
    @State private var filter = "all"
    @State private var fileCategory = "all"
    @State private var selection: UUID?
    @State private var confirmClear = false
    @FocusState private var searchFocused: Bool
    private var visible: [ClipItem] {
        store.items.filter { item in
            (filter == "all" || (filter == "favorites" ? item.favorite : item.kind.rawValue == filter)) &&
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
                Section("类型") {
                    ForEach(ClipKind.allCases) { kind in Label(kind.title, systemImage: kind.symbol).tag(kind.rawValue) }
                }
            }.navigationSplitViewColumnWidth(min: 130, ideal: 150)
        } detail: {
            VStack(spacing: 0) {
                HStack {
                    Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                    TextField("搜索内容、文件名或备注", text: $query).textFieldStyle(.plain).focused($searchFocused)
                        .onSubmit { if let selected { delegate?.use(selected, quick: quick) } }
                    if !query.isEmpty { Button { query = "" } label: { Image(systemName: "xmark.circle.fill") }.buttonStyle(.plain) }
                }
                .padding(12).glassEffect(.regular, in: .rect(cornerRadius: 14)).padding(12)
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
                                        Button("复制") { delegate?.use(item, quick: quick) }
                                        if item.text != nil { Button("复制为纯文本") { delegate?.use(item, quick: quick, plain: true) } }
                                        Button(item.favorite ? "取消收藏" : "收藏") { store.toggleFavorite(item.id) }
                                        if !item.fileURLs.isEmpty { Button("在 Finder 中显示") { NSWorkspace.shared.activateFileViewerSelecting(item.fileURLs) } }
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
                            .keyboardShortcut(.return, modifiers: [])
                    }
                }.padding(12)
            }
        }
        .toolbar {
            Toggle(isOn: $store.paused) { Label("暂停记录", systemImage: store.paused ? "play" : "pause") }.toggleStyle(.button)
            Button { confirmClear = true } label: { Label("清空历史", systemImage: "trash") }
            SettingsLink { Label("设置", systemImage: "gearshape") }
        }
        .onAppear { selection = visible.first?.id; if quick { searchFocused = true } }
        .onChange(of: visible.map(\.id)) { _, ids in if selection.map { !ids.contains($0) } ?? true { selection = ids.first } }
        .onExitCommand { if quick { delegate?.closePanel() } }
        .onKeyPress(.downArrow) { move(1); return .handled }
        .onKeyPress(.upArrow) { move(-1); return .handled }
        .onKeyPress(.space) { if !searchFocused, let url = selected?.fileURLs.first { preview(url); return .handled }; return .ignored }
        .alert("清空普通历史？", isPresented: $confirmClear) {
            Button("取消", role: .cancel) {}
            Button("清空并保留收藏", role: .destructive) { store.clear(keepFavorites: true) }
        } message: { Text("会删除历史及图片缓存。原文件不会被删除。") }
        .alert("拾贴", isPresented: Binding(get: { store.error != nil }, set: { if !$0 { store.error = nil } })) {
            Button("好") { store.error = nil }
        } message: { Text(store.error ?? "") }
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
                TextField("添加备注", text: Binding(get: { store.items.first { $0.id == item.id }?.note ?? "" }, set: { store.setNote(item.id, $0) }))
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
