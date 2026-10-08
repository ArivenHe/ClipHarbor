import SwiftUI
import AppKit
import Carbon

struct KeyCombination: Codable, Equatable {
    var keyCode: UInt16
    var modifiers: UInt
    var key: String
    static let mask: NSEvent.ModifierFlags = [.command, .option, .control, .shift]
    var flags: NSEvent.ModifierFlags { NSEvent.ModifierFlags(rawValue: modifiers).intersection(Self.mask) }
    var label: String {
        (flags.contains(.control) ? "⌃" : "") + (flags.contains(.option) ? "⌥" : "") +
        (flags.contains(.shift) ? "⇧" : "") + (flags.contains(.command) ? "⌘" : "") + key
    }
    var carbonModifiers: UInt32 {
        UInt32((flags.contains(.command) ? cmdKey : 0) | (flags.contains(.option) ? optionKey : 0) |
               (flags.contains(.control) ? controlKey : 0) | (flags.contains(.shift) ? shiftKey : 0))
    }
    func matches(_ event: NSEvent) -> Bool {
        keyCode == event.keyCode && flags == event.modifierFlags.intersection(Self.mask)
    }
    func conflicts(with other: Self) -> Bool { keyCode == other.keyCode && flags == other.flags }
    static func from(_ event: NSEvent) -> Self {
        let special: [UInt16: String] = [36: "↩", 76: "⌤", 48: "⇥", 49: "Space", 51: "⌫", 53: "Esc", 117: "⌦", 123: "←", 124: "→", 125: "↓", 126: "↑", 115: "Home", 119: "End", 116: "Page Up", 121: "Page Down", 122: "F1", 120: "F2", 99: "F3", 118: "F4", 96: "F5", 97: "F6", 98: "F7", 100: "F8", 101: "F9", 109: "F10", 103: "F11", 111: "F12"]
        return Self(keyCode: event.keyCode, modifiers: event.modifierFlags.intersection(mask).rawValue,
                    key: special[event.keyCode] ?? (event.charactersIgnoringModifiers ?? "?").uppercased())
    }
}

enum ShortcutAction: String, CaseIterable, Identifiable {
    case panel, phrasePanel, history, settings, pause, quit
    case toggleText, toggleImages, toggleFiles, toggleAutoPaste, togglePlainText, toggleLogin, toggleFavoritesExempt, cleanup, dataDirectory, clearAll, permissions, resetShortcuts, projectPage
    case toggleLearning, toggleScreenshots, resetLearning, screenshotFolder, learningSettings, screenshotSettings
    case generalSettings, shortcutSettings, recordingSettings, privacySettings, aboutSettings
    case use, copy, copyPlain, paste, clearSearch, favorite, delete, preview, reveal, note, search, next, previous, close, clear
    case all, favorites, frequent, screenshots, text, link, image, files
    case excludeLearning
    case document, spreadsheet, presentation, imageFile, audio, video, archive, code, folder, other
    var id: String { rawValue }
    var global: Bool {
        switch self {
        case .toggleLearning, .toggleScreenshots, .resetLearning, .screenshotFolder, .learningSettings, .screenshotSettings, .panel, .phrasePanel, .history, .pause, .toggleText, .toggleImages, .toggleFiles, .toggleAutoPaste, .togglePlainText, .toggleLogin, .toggleFavoritesExempt, .cleanup, .dataDirectory, .clearAll, .permissions, .resetShortcuts, .projectPage, .generalSettings, .shortcutSettings, .recordingSettings, .privacySettings, .aboutSettings: true
        default: false
        }
    }
    var applicationWide: Bool { self == .quit || self == .settings }
    var group: String { applicationWide ? "应用内操作" : (global ? "全局操作" : (filter != nil ? "类型筛选" : "窗口内操作")) }
    var settingsTab: String? {
        switch self {
        case .learningSettings: "learning"; case .screenshotSettings: "screenshots"
        case .generalSettings: "general"; case .shortcutSettings: "shortcuts"; case .recordingSettings: "recording"
        case .privacySettings: "privacy"; case .aboutSettings: "about"; default: nil
        }
    }
    var title: String {
        switch self {
        case .toggleLearning: "开启／关闭常用内容学习"; case .toggleScreenshots: "开启／关闭截图保存"
        case .resetLearning: "重置学习记录（确认）"; case .screenshotFolder: "选择截图监听目录"
        case .learningSettings: "常用内容设置"; case .screenshotSettings: "截图设置"
        case .frequent: "常用内容"; case .screenshots: "系统截图"; case .excludeLearning: "排除／允许学习选中内容"
        case .phrasePanel: "直接打开快捷短语"; case .panel: "打开／关闭快捷面板"; case .history: "打开历史"; case .settings: "打开设置"; case .pause: "暂停／恢复记录"; case .quit: "退出拾贴"
        case .toggleText: "开启／关闭文本记录"; case .toggleImages: "开启／关闭图片记录"; case .toggleFiles: "开启／关闭文件记录"
        case .toggleAutoPaste: "开启／关闭自动粘贴"; case .togglePlainText: "切换默认纯文本复制"
        case .toggleLogin: "开启／关闭登录启动"; case .toggleFavoritesExempt: "切换收藏永久保留"
        case .clearAll: "删除全部历史与收藏（确认）"; case .permissions: "打开辅助功能权限设置"
        case .resetShortcuts: "恢复默认快捷键（确认）"; case .projectPage: "打开项目主页"
        case .generalSettings: "通用设置"; case .shortcutSettings: "快捷键设置"; case .recordingSettings: "记录与保留时间设置"
        case .privacySettings: "隐私设置"; case .aboutSettings: "关于拾贴"
        case .paste: "粘贴到原应用"; case .clearSearch: "清除搜索"
        case .cleanup: "按保留策略清理"; case .dataDirectory: "打开数据目录"
        case .use: "使用选中内容"; case .copy: "复制选中内容"; case .copyPlain: "复制为纯文本"
        case .favorite: "收藏／取消收藏"; case .delete: "删除选中记录"; case .preview: "快速预览"
        case .reveal: "在 Finder 中显示"; case .note: "编辑备注"; case .search: "聚焦搜索"
        case .next: "下一条"; case .previous: "上一条"; case .close: "关闭窗口"; case .clear: "清空普通历史（确认）"
        case .all: "全部"; case .favorites: "收藏"; case .text: "文本"; case .link: "链接"; case .image: "图片"; case .files: "文件"
        case .document: "文档文件"; case .spreadsheet: "表格文件"; case .presentation: "演示文件"; case .imageFile: "图片文件"
        case .audio: "音频文件"; case .video: "视频文件"; case .archive: "压缩包"; case .code: "代码文件"; case .folder: "文件夹"; case .other: "其他文件"
        }
    }
    var filter: (kind: String, category: String)? {
        switch self {
        case .frequent: ("frequent", "all"); case .screenshots: ("screenshots", "all")
        case .all: ("all", "all"); case .favorites: ("favorites", "all"); case .text: ("text", "all")
        case .link: ("link", "all"); case .image: ("image", "all"); case .files: ("files", "all")
        case .document, .spreadsheet, .presentation, .audio, .video, .archive, .code, .folder, .other: ("files", rawValue)
        case .imageFile: ("image", "all")
        default: nil
        }
    }
    var defaultKey: KeyCombination? {
        let command = NSEvent.ModifierFlags.command.rawValue
        let shiftCommand = NSEvent.ModifierFlags([.shift, .command]).rawValue
        return switch self {
        case .panel: KeyCombination(keyCode: 9, modifiers: NSEvent.ModifierFlags([.option, .command]).rawValue, key: "V")
        case .quit: KeyCombination(keyCode: 12, modifiers: command, key: "Q")
        case .settings: KeyCombination(keyCode: 43, modifiers: command, key: ",")
        case .use: KeyCombination(keyCode: 36, modifiers: 0, key: "↩")
        case .copy: KeyCombination(keyCode: 8, modifiers: shiftCommand, key: "C")
        case .copyPlain: KeyCombination(keyCode: 9, modifiers: shiftCommand, key: "V")
        case .favorite: KeyCombination(keyCode: 3, modifiers: shiftCommand, key: "F")
        case .delete: KeyCombination(keyCode: 51, modifiers: command, key: "⌫")
        case .preview: KeyCombination(keyCode: 49, modifiers: 0, key: "Space")
        case .search: KeyCombination(keyCode: 3, modifiers: command, key: "F")
        case .next: KeyCombination(keyCode: 125, modifiers: 0, key: "↓")
        case .previous: KeyCombination(keyCode: 126, modifiers: 0, key: "↑")
        case .close: KeyCombination(keyCode: 53, modifiers: 0, key: "Esc")
        default: nil
        }
    }
}

extension Notification.Name { static let clipShortcut = Notification.Name("ClipHarbor.shortcut") }

@MainActor
final class ShortcutManager: ObservableObject {
    static let shared = ShortcutManager()
    @Published private(set) var combinations: [String: KeyCombination] = [:]
    @Published var recording: ShortcutAction? {
        didSet {
            guard started else { return }
            if recording != nil { unregisterGlobals() }
            else if let message = registerGlobals() { error = message }
        }
    }
    @Published var error: String?
    var searchWindow: Int?
    var globalHandler: ((ShortcutAction) -> Void)?
    private var hotKeys: [EventHotKeyRef] = []
    private var handler: EventHandlerRef?
    private var monitor: Any?
    private var started = false
    private init() {
        if let data = UserDefaults.standard.data(forKey: "shortcuts.v1"), let saved = try? JSONDecoder().decode([String: KeyCombination].self, from: data) { combinations = saved }
        else {
            combinations = Self.defaultCombinations
            if UserDefaults.standard.integer(forKey: "shortcutOption") == 1 {
                combinations[ShortcutAction.panel.rawValue] = KeyCombination(keyCode: 9, modifiers: NSEvent.ModifierFlags([.control, .option]).rawValue, key: "V")
            }
        }
    }
    static var defaultCombinations: [String: KeyCombination] {
        Dictionary(uniqueKeysWithValues: ShortcutAction.allCases.compactMap { action in action.defaultKey.map { (action.rawValue, $0) } })
    }
    func label(_ action: ShortcutAction) -> String { combinations[action.rawValue]?.label ?? "未设置" }
    func start() {
        guard !started else { return }; started = true
        var type = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, event, pointer in
            guard let event, let pointer else { return OSStatus(eventNotHandledErr) }
            var identifier = EventHotKeyID()
            let status = GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID), nil, MemoryLayout<EventHotKeyID>.size, nil, &identifier)
            guard status == noErr else { return status }
            let manager = Unmanaged<ShortcutManager>.fromOpaque(pointer).takeUnretainedValue()
            let index = Int(identifier.id) - 1
            Task { @MainActor in
                guard manager.recording == nil, ShortcutAction.allCases.indices.contains(index) else { return }
                manager.globalHandler?(ShortcutAction.allCases[index])
            }
            return noErr
        }, 1, &type, Unmanaged.passUnretained(self).toOpaque(), &handler)
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
            let shouldForward = MainActor.assumeIsolated { self.handle(event) != nil }
            return shouldForward ? event : nil
        }
        if let message = registerGlobals() { error = message }
    }
    private func handle(_ event: NSEvent) -> NSEvent? {
        if let action = recording {
            if event.keyCode == 53 && event.modifierFlags.intersection(KeyCombination.mask).isEmpty { recording = nil; return nil }
            _ = set(KeyCombination.from(event), for: action)
            recording = nil
            return nil
        }
        if let action = ShortcutAction.allCases.first(where: { $0.applicationWide && combinations[$0.rawValue]?.matches(event) == true }) {
            globalHandler?(action); return nil
        }
        // Ordinary navigation belongs to the focused view, independently of
        // saved shortcuts. In particular, Enter must not steal button presses
        // or commit a note by copying the selected record.
        if event.modifierFlags.intersection([.command, .option, .control]).isEmpty,
           [UInt16(36), 48, 49, 53, 76, 123, 124, 125, 126, 115, 119, 116, 121].contains(event.keyCode) { return event }
        // Leave settings controls and standard text editing to AppKit.
        guard NSApp.keyWindow?.identifier?.rawValue == "ClipHarbor.history", let window = NSApp.keyWindow else { return event }
        guard let action = ShortcutAction.allCases.first(where: { !$0.global && !$0.applicationWide && combinations[$0.rawValue]?.matches(event) == true }) else { return event }
        if window.firstResponder is NSTextView {
            if event.modifierFlags.intersection(KeyCombination.mask).isEmpty && ![UInt16(36), 76, 53, 125, 126].contains(event.keyCode) { return event }
            if (action == .preview || action == .delete) && event.modifierFlags.intersection(KeyCombination.mask).isEmpty { return event }
            if action == .next || action == .previous || action == .use {
                guard searchWindow == window.windowNumber else { return event }
            }
        }
        NotificationCenter.default.post(name: .clipShortcut, object: action, userInfo: ["window": window.windowNumber])
        return nil
    }
    @discardableResult func set(_ combination: KeyCombination?, for action: ShortcutAction) -> Bool {
        if let combination {
            if action.global && !combination.flags.contains(.command) && !combination.flags.contains(.control) && !combination.flags.contains(.option) {
                error = "全局快捷键需要包含 ⌘、⌃ 或 ⌥。"; return false
            }
            if let conflict = ShortcutAction.allCases.first(where: { $0 != action && combinations[$0.rawValue]?.conflicts(with: combination) == true }) {
                error = "该组合已用于“\(conflict.title)”。请先清除或更改该快捷键。"; return false
            }
        }
        let old = combinations
        combinations[action.rawValue] = combination
        if started, let message = registerGlobals() {
            combinations = old; _ = registerGlobals(); if recording != nil { unregisterGlobals() }; error = message; return false
        }
        persist(); error = nil
        if recording != nil { unregisterGlobals() }
        return true
    }
    func restoreDefaults() {
        let old = combinations; combinations = Self.defaultCombinations
        if started, let message = registerGlobals() { combinations = old; _ = registerGlobals(); error = message; return }
        persist(); error = nil
    }
    private func persist() {
        if let data = try? JSONEncoder().encode(combinations) { UserDefaults.standard.set(data, forKey: "shortcuts.v1") }
    }
    private func unregisterGlobals() {
        hotKeys.forEach { UnregisterEventHotKey($0) }; hotKeys.removeAll()
    }
    private func registerGlobals() -> String? {
        unregisterGlobals()
        for (index, action) in ShortcutAction.allCases.enumerated() where action.global {
            guard let key = combinations[action.rawValue] else { continue }
            var reference: EventHotKeyRef?
            let status = RegisterEventHotKey(UInt32(key.keyCode), key.carbonModifiers, EventHotKeyID(signature: 0x434C4950, id: UInt32(index + 1)), GetApplicationEventTarget(), 0, &reference)
            guard status == noErr, let reference else { return "“\(action.title)”的快捷键无法注册，可能被系统或其他应用占用。" }
            hotKeys.append(reference)
        }
        return nil
    }
}

struct ShortcutSettingsView: View {
    @ObservedObject private var manager = ShortcutManager.shared
    @State private var reset = false
    var body: some View {
        Form {
            Section {
                Text("点击录制后按下组合键；Esc 取消。全局操作在其他应用中也生效，窗口内操作只在历史和快捷面板中生效。")
                Text("可清除任意快捷键。冲突组合会被拒绝，保留原设置。")
            }
            ForEach(["全局操作", "应用内操作", "窗口内操作", "类型筛选"], id: \.self) { group in
                Section(group) {
                    ForEach(ShortcutAction.allCases.filter { $0.group == group }) { action in
                        HStack {
                            Text(action.title)
                            Spacer()
                            KeyboardButton(manager.recording == action ? "按下快捷键…" : manager.label(action)) { manager.recording = manager.recording == action ? nil : action }
                                .monospaced().frame(minWidth: 105)
                            KeyboardButton { _ = manager.set(nil, for: action); manager.recording = nil } label: { Image(systemName: "xmark.circle") }.help("清除快捷键")
                        }
                    }
                }
            }
            Section { KeyboardButton("恢复默认快捷键") { reset = true } }
        }.formStyle(.grouped)
        .onDisappear { manager.recording = nil }
        .alert("快捷键无法保存", isPresented: Binding(get: { manager.error != nil }, set: { if !$0 { manager.error = nil } })) {
            Button("好") { manager.error = nil }
        } message: { Text(manager.error ?? "") }
        .confirmationDialog("恢复所有默认快捷键？", isPresented: $reset) {
            Button("恢复默认") { manager.restoreDefaults() }
        }
    }
}

// Reports the actual host window so commands reach only its HistoryView.
struct HistoryWindowReader: NSViewRepresentable {
    var onWindow: (NSWindow) -> Void
    func makeNSView(context: Context) -> WindowReaderView {
        let view = WindowReaderView(); view.onWindow = onWindow; return view
    }
    func updateNSView(_ view: WindowReaderView, context: Context) { view.onWindow = onWindow }
}
final class WindowReaderView: NSView {
    var onWindow: ((NSWindow) -> Void)?
    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        if let window { window.identifier = NSUserInterfaceItemIdentifier("ClipHarbor.history"); onWindow?(window) }
    }
}
