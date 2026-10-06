import SwiftUI
import AppKit
import ApplicationServices
import ServiceManagement

@main
struct ClipHarborApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) var delegate
    init() {
        UserDefaults.standard.register(defaults: [
            "recordText": true, "recordImages": true, "recordFiles": true,
            "historyLimit": 1000, "retentionDays": 30, "imageLimitMB": 20,
            "autoPaste": false, "plainText": false, "favoritesExempt": true
        ])
    }
    var body: some Scene {
        WindowGroup("拾贴 · ClipHarbor", id: "history") {
            HistoryView(store: delegate.store, quick: false)
                .frame(minWidth: 760, minHeight: 480)
        }
        .defaultSize(width: 1000, height: 680)
        .commands {
            CommandGroup(replacing: .appSettings) { Button("设置…") { delegate.openSettings() } }
            CommandGroup(replacing: .appTermination) { Button("退出拾贴") { NSApp.terminate(nil) } }
        }
        MenuBarExtra("拾贴", systemImage: "doc.on.clipboard") {
            Button("打开快捷面板") { delegate.togglePanel() }
            Button("打开历史") { delegate.openHistory() }
            Toggle("暂停记录", isOn: Binding(get: { delegate.store.paused }, set: { delegate.store.paused = $0 }))
            Divider()
            Button("设置…") { delegate.openSettings() }
            Button("退出拾贴") { NSApp.terminate(nil) }
        }
        Settings { SettingsView(store: delegate.store).frame(width: 640, height: 650) }
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    static weak var shared: AppDelegate?
    override init() { super.init(); Self.shared = self }
    let store = ClipboardStore()
    lazy var screenshotMonitor = ScreenshotMonitor(store: store)
    private var panel: NSPanel?
    private var historyWindow: NSWindow?
    private var previousApp: NSRunningApplication?
    private var workspaceObserver: NSObjectProtocol?
    private var settingsWindow: NSWindow?
    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)
        workspaceObserver = NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didActivateApplicationNotification, object: nil, queue: .main) { [weak self] notification in
            guard let app = notification.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication else { return }
            let processID = app.processIdentifier
            Task { @MainActor in
                guard processID != ProcessInfo.processInfo.processIdentifier else { return }
                self?.previousApp = NSRunningApplication(processIdentifier: processID)
            }
        }
        ShortcutManager.shared.globalHandler = { [weak self] action in self?.perform(action) }
        ShortcutManager.shared.start()
        screenshotMonitor.start()
    }
    func perform(_ action: ShortcutAction) {
        let defaults = UserDefaults.standard
        if let tab = action.settingsTab { defaults.set(tab, forKey: "settingsTab"); openSettings(); return }
        switch action {
        case .panel: togglePanel()
        case .history: openHistory()
        case .settings: openSettings()
        case .pause: store.paused.toggle()
        case .toggleLearning: defaults.set(!defaults.bool(forKey: "learningEnabled"), forKey: "learningEnabled")
        case .toggleScreenshots: defaults.set(!defaults.bool(forKey: "watchScreenshots"), forKey: "watchScreenshots")
        case .resetLearning:
            if confirm("重置常用内容学习？", detail: "会清除复制和使用次数，保留剪贴板内容及收藏。", button: "重置学习") { store.resetLearning() }
        case .screenshotFolder: chooseScreenshotFolder()
        case .quit: NSApp.terminate(nil)
        case .toggleText: defaults.set(!defaults.bool(forKey: "recordText"), forKey: "recordText")
        case .toggleImages: defaults.set(!defaults.bool(forKey: "recordImages"), forKey: "recordImages")
        case .toggleFiles: defaults.set(!defaults.bool(forKey: "recordFiles"), forKey: "recordFiles")
        case .toggleAutoPaste: defaults.set(!defaults.bool(forKey: "autoPaste"), forKey: "autoPaste")
        case .togglePlainText: defaults.set(!defaults.bool(forKey: "plainText"), forKey: "plainText")
        case .toggleFavoritesExempt: defaults.set(!defaults.bool(forKey: "favoritesExempt"), forKey: "favoritesExempt")
        case .toggleLogin:
            do {
                if SMAppService.mainApp.status == .enabled { try SMAppService.mainApp.unregister() }
                else { try SMAppService.mainApp.register() }
            } catch { store.error = "登录项设置失败：\(error.localizedDescription)"; openSettings() }
        case .clearAll:
            if confirm("删除全部历史与收藏？", detail: "会删除图片缓存，但不会删除原文件。", button: "删除全部") { store.clear(keepFavorites: false) }
        case .resetShortcuts:
            if confirm("恢复默认快捷键？", detail: "会覆盖所有自定义快捷键。", button: "恢复默认") { ShortcutManager.shared.restoreDefaults() }
        case .permissions: openAccessibility()
        case .projectPage: NSWorkspace.shared.open(URL(string: "https://github.com/ArivenHe/ClipHarbor")!)
        case .cleanup: store.prune(); store.save()
        case .dataDirectory: NSWorkspace.shared.open(store.directory)
        default: break
        }
    }
    private func confirm(_ title: String, detail: String, button: String) -> Bool {
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert(); alert.messageText = title; alert.informativeText = detail
        alert.addButton(withTitle: "取消"); alert.addButton(withTitle: button)
        return alert.runModal() == .alertSecondButtonReturn
    }
    func chooseScreenshotFolder() {
        NSApp.activate(ignoringOtherApps: true)
        let picker = NSOpenPanel()
        picker.canChooseDirectories = true; picker.canChooseFiles = false; picker.allowsMultipleSelection = false
        picker.prompt = "监听此文件夹"
        if picker.runModal() == .OK, let url = picker.url { UserDefaults.standard.set(url.path, forKey: "screenshotFolder") }
    }
    func openAccessibility() {
        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")!)
    }
    func openSettings() {
        NSApp.activate(ignoringOtherApps: true)
        if let window = NSApp.windows.first(where: { $0.identifier?.rawValue == "ClipHarbor.settings" }) {
            window.makeKeyAndOrderFront(nil); return
        }
        if settingsWindow == nil {
            let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 640, height: 650), styleMask: [.titled, .closable], backing: .buffered, defer: false)
            window.title = "拾贴设置"
            window.identifier = NSUserInterfaceItemIdentifier("ClipHarbor.settings")
            window.isReleasedWhenClosed = false
            window.contentView = NSHostingView(rootView: SettingsView(store: store))
            window.center(); settingsWindow = window
        }
        settingsWindow?.makeKeyAndOrderFront(nil)
    }
    func togglePanel() {
        if panel?.isVisible == true { closePanel(); return }
        if NSWorkspace.shared.frontmostApplication?.processIdentifier != ProcessInfo.processInfo.processIdentifier { previousApp = NSWorkspace.shared.frontmostApplication }
        if panel == nil {
            let window = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 760, height: 520), styleMask: [.titled, .closable, .resizable, .fullSizeContentView], backing: .buffered, defer: false)
            window.title = "拾贴"
            window.titlebarAppearsTransparent = true
            window.level = .floating
            window.collectionBehavior = [.moveToActiveSpace, .fullScreenAuxiliary]
            window.isReleasedWhenClosed = false
            window.contentView = NSHostingView(rootView: HistoryView(store: store, quick: true))
            panel = window
        }
        if let screen = NSScreen.screens.first(where: { NSMouseInRect(NSEvent.mouseLocation, $0.frame, false) }), let panel {
            let area = screen.visibleFrame
            panel.setFrameOrigin(NSPoint(x: area.midX - panel.frame.width / 2, y: area.midY - panel.frame.height / 2))
        }
        NSApp.activate(ignoringOtherApps: true)
        panel?.makeKeyAndOrderFront(nil)
    }
    func closePanel() { panel?.orderOut(nil); previousApp?.activate(options: [.activateIgnoringOtherApps]) }
    func openHistory() {
        if NSWorkspace.shared.frontmostApplication?.processIdentifier != ProcessInfo.processInfo.processIdentifier { previousApp = NSWorkspace.shared.frontmostApplication }
        NSApp.activate(ignoringOtherApps: true)
        if let window = NSApp.windows.first(where: { !($0 is NSPanel) && $0.title.contains("ClipHarbor") }) { window.makeKeyAndOrderFront(nil) }
        else {
            if historyWindow == nil {
                let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1000, height: 680), styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
                window.title = "拾贴 · ClipHarbor"
                window.isReleasedWhenClosed = false
                window.contentView = NSHostingView(rootView: HistoryView(store: store, quick: false))
                window.center()
                historyWindow = window
            }
            historyWindow?.makeKeyAndOrderFront(nil)
        }
    }
    func use(_ item: ClipItem, quick: Bool, plain: Bool = false, paste: Bool? = nil) {
        guard store.copy(item, plain: plain || UserDefaults.standard.bool(forKey: "plainText")) else { return }
        let shouldPaste = paste ?? (quick && UserDefaults.standard.bool(forKey: "autoPaste"))
        let target = previousApp
        if quick { closePanel() }
        guard shouldPaste, let target, target.processIdentifier != ProcessInfo.processInfo.processIdentifier else { return }
        guard AXIsProcessTrusted() else { store.error = "内容已复制。自动粘贴需要在系统设置中授予拾贴辅助功能权限。"; return }
        target.activate(options: [.activateIgnoringOtherApps])
        Task { @MainActor in
            try? await Task.sleep(for: .milliseconds(200))
            guard NSWorkspace.shared.frontmostApplication?.processIdentifier == target.processIdentifier else { return }
            guard let source = CGEventSource(stateID: .combinedSessionState),
                  let down = CGEvent(keyboardEventSource: source, virtualKey: 9, keyDown: true),
                  let up = CGEvent(keyboardEventSource: source, virtualKey: 9, keyDown: false) else { return }
            down.flags = .maskCommand; up.flags = .maskCommand
            down.post(tap: .cghidEventTap); up.post(tap: .cghidEventTap)
        }
    }
}
