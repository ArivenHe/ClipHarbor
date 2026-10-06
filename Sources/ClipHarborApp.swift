import SwiftUI
import AppKit
import Carbon
import ApplicationServices

@main
struct ClipHarborApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) var delegate
    init() {
        UserDefaults.standard.register(defaults: [
            "recordText": true, "recordImages": true, "recordFiles": true,
            "historyLimit": 1000, "retentionDays": 30, "imageLimitMB": 20,
            "autoPaste": false, "plainText": false, "shortcutOption": 0
        ])
    }
    var body: some Scene {
        WindowGroup("拾贴 · ClipHarbor", id: "history") {
            HistoryView(store: delegate.store, quick: false)
                .frame(minWidth: 760, minHeight: 480)
        }
        .defaultSize(width: 1000, height: 680)
        MenuBarExtra("拾贴", systemImage: "doc.on.clipboard") {
            Button("打开快捷面板  ⌥⌘V") { delegate.togglePanel() }
            Button("打开历史") { delegate.openHistory() }
            Toggle("暂停记录", isOn: Binding(get: { delegate.store.paused }, set: { delegate.store.paused = $0 }))
            Divider()
            SettingsLink { Text("设置…") }
            Button("退出拾贴") { NSApp.terminate(nil) }.keyboardShortcut("q")
        }
        Settings { SettingsView(store: delegate.store).frame(width: 540, height: 520) }
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    let store = ClipboardStore()
    private var panel: NSPanel?
    private var historyWindow: NSWindow?
    private var previousApp: NSRunningApplication?
    private var hotKey: EventHotKeyRef?
    private var handler: EventHandlerRef?
    private var preferenceObserver: NSObjectProtocol?
    func applicationDidFinishLaunching(_ notification: Notification) {
        var type = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, _, pointer in
            guard let pointer else { return OSStatus(eventNotHandledErr) }
            let delegate = Unmanaged<AppDelegate>.fromOpaque(pointer).takeUnretainedValue()
            Task { @MainActor in delegate.togglePanel() }
            return noErr
        }, 1, &type, Unmanaged.passUnretained(self).toOpaque(), &handler)
        registerShortcut()
        preferenceObserver = NotificationCenter.default.addObserver(forName: UserDefaults.didChangeNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.registerShortcut() }
        }
    }
    private func registerShortcut() {
        if let hotKey { UnregisterEventHotKey(hotKey) }
        let alternate = UserDefaults.standard.integer(forKey: "shortcutOption") == 1
        let modifiers = UInt32(alternate ? (controlKey | optionKey) : (cmdKey | optionKey))
        let status = RegisterEventHotKey(UInt32(kVK_ANSI_V), modifiers, EventHotKeyID(signature: 0x434C4950, id: 1), GetApplicationEventTarget(), 0, &hotKey)
        if status != noErr { store.error = "快捷键注册失败，请在设置中选择另一个组合。" }
    }
    func togglePanel() {
        if panel?.isVisible == true { closePanel(); return }
        previousApp = NSWorkspace.shared.frontmostApplication
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
    func use(_ item: ClipItem, quick: Bool, plain: Bool = false) {
        guard store.copy(item, plain: plain || UserDefaults.standard.bool(forKey: "plainText")) else { return }
        guard quick else { return }
        let target = previousApp
        closePanel()
        guard UserDefaults.standard.bool(forKey: "autoPaste"), let target, target.processIdentifier != ProcessInfo.processInfo.processIdentifier else { return }
        guard AXIsProcessTrusted() else { store.error = "内容已复制。自动粘贴需要在系统设置中授予拾贴辅助功能权限。"; return }
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
