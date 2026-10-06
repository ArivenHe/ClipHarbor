import SwiftUI
import ServiceManagement
import ApplicationServices
import AppKit

struct SettingsView: View {
    @ObservedObject var store: ClipboardStore
    @AppStorage("recordText") private var recordText = true
    @AppStorage("recordImages") private var recordImages = true
    @AppStorage("recordFiles") private var recordFiles = true
    @AppStorage("historyLimit") private var historyLimit = 1000
    @AppStorage("favoritesExempt") private var favoritesExempt = true
    @AppStorage("imageLimitMB") private var imageLimitMB = 20
    @AppStorage("autoPaste") private var autoPaste = false
    @AppStorage("plainText") private var plainText = false
    @AppStorage("excludedApps") private var excludedApps = ""
    @AppStorage("settingsTab") private var tab = "general"
    @State private var login = SMAppService.mainApp.status == .enabled
    @State private var deleteAll = false
    @State private var accessibilityGranted = AXIsProcessTrusted()
    @FocusState private var pageFocused: Bool
    private let pages = ["general", "sync", "shortcuts", "recording", "privacy", "screenshots", "learning", "about"]
    var body: some View {
        VStack(spacing: 0) {
            Picker("设置页面", selection: $tab) {
                Text("通用").tag("general")
                Text("同步").tag("sync")
                Text("快捷键").tag("shortcuts")
                Text("记录").tag("recording")
                Text("隐私").tag("privacy")
                Text("截图").tag("screenshots")
                Text("常用").tag("learning")
                Text("关于").tag("about")
            }.pickerStyle(.segmented).keyboardPicker(selection: $tab, values: pages)
                .focused($pageFocused).padding(12)
            Divider()
            Group {
                switch tab {
                case "sync": if let sync = AppDelegate.shared?.sync { SyncSettingsView(sync: sync) }
                case "shortcuts": ShortcutSettingsView()
                case "recording": recordingPage
                case "privacy": privacyPage
                case "screenshots":
                    if let monitor = AppDelegate.shared?.screenshotMonitor { ScreenshotSettingsView(monitor: monitor) }
                case "learning": LearningSettingsView(store: store)
                case "about": aboutPage
                default: generalPage
                }
            }.frame(maxWidth: .infinity, maxHeight: .infinity).focusSection()
            Divider()
            Text("Tab 切换选项 · 方向键调整 · 空格／回车确认 · Esc 关闭")
                .font(.caption).foregroundStyle(.secondary).padding(8)
        }
        .onAppear { pageFocused = true; accessibilityGranted = AXIsProcessTrusted() }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            accessibilityGranted = AXIsProcessTrusted()
        }
        .onKeyPress(.escape) { NSApp.keyWindow?.performClose(nil); return .handled }
        .onChange(of: tab) { _, _ in login = SMAppService.mainApp.status == .enabled }
        .onReceive(NotificationCenter.default.publisher(for: UserDefaults.didChangeNotification)) { _ in login = SMAppService.mainApp.status == .enabled }
        .alert("操作失败", isPresented: Binding(get: { store.error != nil }, set: { if !$0 { store.error = nil } })) {
            Button("好") { store.error = nil }
        } message: { Text(store.error ?? "") }
        .alert("删除全部本地历史？", isPresented: $deleteAll) {
            Button("取消", role: .cancel) {}
            Button("删除全部", role: .destructive) { store.clear(keepFavorites: false) }
        } message: { Text("包括收藏和图片缓存。不会删除原文件。") }
    }
    private var generalPage: some View {
            Form {
                Section("通用") {
                    KeyboardToggle("登录时启动", isOn: $login).onChange(of: login) { _, value in
                        guard value != (SMAppService.mainApp.status == .enabled) else { return }
                        do { if value { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() } }
                        catch { store.error = "登录项设置失败：\(error.localizedDescription)"; login = SMAppService.mainApp.status == .enabled }
                    }
                    KeyboardToggle("快捷面板选择后自动粘贴", isOn: $autoPaste)
                    KeyboardToggle("文本默认以纯文本复制", isOn: $plainText)
                }
                Section("自动粘贴权限") {
                    Label(accessibilityGranted ? "辅助功能已授权，可以自动粘贴。" : "当前运行的拾贴尚未获得辅助功能权限，仍可复制内容。", systemImage: accessibilityGranted ? "checkmark.circle.fill" : "exclamationmark.circle")
                        .foregroundStyle(accessibilityGranted ? Color.green : Color.secondary)
                    if !accessibilityGranted {
                        Text("权限与应用的路径和签名有关。更换安装位置或更新临时签名版本后，系统可能要求重新授权。")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                    KeyboardButton("打开辅助功能设置") {
                        AppDelegate.shared?.openAccessibility()
                    }
                    KeyboardButton("重新检查权限") { accessibilityGranted = AXIsProcessTrusted() }
                }
            }.formStyle(.grouped)
    }
    private var recordingPage: some View {
            Form {
                Section("记录类型") {
                    KeyboardToggle("文本与链接", isOn: $recordText)
                    KeyboardToggle("图片", isOn: $recordImages)
                    KeyboardToggle("原文件引用", isOn: $recordFiles)
                    Text("本地记录保存文件引用；开启文件同步后上传副本供其他设备粘贴。原文件移动或删除后，本地旧引用可能失效。")
                }
                Section("存储") {
                    KeyboardStepper("普通历史上限：\(historyLimit)", value: $historyLimit, in: 100...10000, step: 100)
                    RetentionEditor(prefix: "retention")
                    KeyboardToggle("收藏永久保留，免于数量和时间清理", isOn: $favoritesExempt)
                    Text("永久仅免于时间清理，普通记录仍受数量上限限制。每 30 秒自动清理过期记录。缩短期限可能删除已有历史。")
                    KeyboardStepper("单张图片上限：\(imageLimitMB) MB", value: $imageLimitMB, in: 1...100)
                    Text("文本上限 2 MB。删除记录不会删除原文件。")
                    KeyboardButton("立即按设置清理") { store.prune(); store.save() }
                    KeyboardButton("在 Finder 中打开数据目录") { NSWorkspace.shared.open(store.directory) }
                }
                Section("按类型设置保留时间") {
                    Text("未开启单独设置时，使用上方的默认保留时间。")
                    ForEach(ClipKind.allCases) { kind in TypeRetentionEditor(kind: kind) }
                }
            }.formStyle(.grouped)
    }
    private var privacyPage: some View {
            Form {
                Section("排除应用") {
                    Text("每行填写一个应用 Bundle ID，例如 com.apple.keychainaccess。来源以复制时的前台应用为依据。")
                    TextEditor(text: $excludedApps).font(.system(.body, design: .monospaced)).frame(height: 100)
                    KeyboardToggle("暂停所有记录", isOn: $store.paused)
                }
                Section("本地数据") {
                    Text("默认只在本机保存。开启跨设备同步后通过 HTTPS 上传到指定服务器；服务器管理员可读取内容。支持过滤已声明的敏感剪切板标记，不能保证识别所有密码。")
                    KeyboardButton("删除全部历史与收藏", role: .destructive) { deleteAll = true }
                }
            }.formStyle(.grouped)
    }
    private var aboutPage: some View {
            VStack(spacing: 14) {
                Image(systemName: "doc.on.clipboard").font(.system(size: 56)).foregroundStyle(.tint)
                Text("拾贴 · ClipHarbor").font(.title)
                Text("\(Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "0.2.1") · macOS 26+")
                Text("复制即收纳，随时找回来。").foregroundStyle(.secondary)
                KeyboardButton("GitHub") { NSWorkspace.shared.open(URL(string: "https://github.com/ArivenHe/ClipHarbor")!) }
            }
    }
}

struct RetentionEditor: View {
    @AppStorage private var unit: String
    @AppStorage private var value: Int
    init(prefix: String) {
        _unit = AppStorage(wrappedValue: "days", prefix + ".unit")
        _value = AppStorage(wrappedValue: 30, prefix + ".value")
    }
    var body: some View {
        HStack {
            Text("保留时间")
            Spacer()
            if unit != RetentionUnit.forever.rawValue {
                TextField("数量", value: $value, format: .number.grouping(.never))
                    .frame(width: 75).multilineTextAlignment(.trailing)
                    .onChange(of: value) { _, next in
                        let clamped = min(max(1, next), 100000)
                        if clamped != next { value = clamped }
                    }
                KeyboardStepper("数量", value: $value, in: 1...100000).labelsHidden()
            }
            Picker("单位", selection: $unit) {
                ForEach(RetentionUnit.allCases) { Text($0.title).tag($0.rawValue) }
            }.keyboardPicker(selection: $unit, values: RetentionUnit.allCases.map(\.rawValue)).labelsHidden().frame(width: 95)
        }
    }
}
struct TypeRetentionEditor: View {
    let kind: ClipKind
    @AppStorage private var customRetention: Bool
    init(kind: ClipKind) {
        self.kind = kind
        _customRetention = AppStorage(wrappedValue: false, "retention.\(kind.rawValue).override")
    }
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            KeyboardToggle("\(kind.title)单独设置", isOn: $customRetention)
            if customRetention { RetentionEditor(prefix: "retention.\(kind.rawValue)") }
        }
    }
}

struct ScreenshotSettingsView: View {
    @ObservedObject var monitor: ScreenshotMonitor
    @AppStorage("watchScreenshots") private var enabled = true
    @AppStorage("screenshotFolder") private var folder = ""
    @AppStorage("screenshotAllImages") private var allImages = false
    var body: some View {
        Form {
            Section("系统截图自动保存") {
                KeyboardToggle("保存 macOS 系统截图", isOn: $enabled)
                Text("支持 ⌘⇧3、⌘⇧4 和截图工具保存的图片；不修改系统快捷键。包含 Control 的截图由剪贴板记录接收。")
                Text(monitor.status).foregroundStyle(.secondary)
                Text(monitor.configuredFolder.path).font(.caption).textSelection(.enabled)
            }
            Section("监听位置") {
                KeyboardButton("选择截图文件夹…") { AppDelegate.shared?.chooseScreenshotFolder() }
                KeyboardButton("跟随系统截图保存位置") { folder = "" }
                KeyboardToggle("监听此文件夹中的所有新图片", isOn: $allImages)
                Text("默认识别系统截图文件名及系统自定义名称。使用其他命名规则时，可选专用截图目录并开启所有新图片。首次启用、切换目录及恢复记录时不会导入已有图片。")
            }
            Section("保存规则") {
                Text("图片保存到拾贴缓存。删除原截图后，缓存仍可使用。遵循图片大小、保留期限与收藏策略；暂停记录同时暂停截图监听。")
                Text("需要系统允许访问截图文件夹。没有屏幕录制权限要求，因为拾贴只读取截图保存后的文件。")
            }
        }.formStyle(.grouped)
    }
}
struct LearningSettingsView: View {
    @ObservedObject var store: ClipboardStore
    @AppStorage("learningEnabled") private var enabled = true
    @AppStorage("learningThreshold") private var threshold = 2
    @State private var reset = false
    var body: some View {
        Form {
            Section("常用内容学习") {
                KeyboardToggle("在本机自动整理常用内容", isOn: $enabled)
                KeyboardStepper("进入常用列表的累计次数：\(threshold)", value: $threshold, in: 2...20)
                Text("累计次数为记录次数与使用次数之和；排序时使用次数权重更高。统计基于当前保留的历史，删除或过期后不再保留该条统计。")
                Text(enabled ? FrequentContent.summary(store.items, threshold: threshold) : "已关闭学习，不再累计新的复制和使用次数。")
            }
            Section("隐私与控制") {
                Text("学习统计留在本机，不调用 AI 服务。开启跨设备同步后，选中的文字、图片和文件会上传到指定服务器。通过本地规则排除疑似密钥和验证码，无法保证识别所有敏感内容。")
                KeyboardButton("重置学习记录") { reset = true }
            }
        }.formStyle(.grouped)
        .alert("重置常用内容学习？", isPresented: $reset) {
            Button("取消", role: .cancel) {}
            Button("重置") { store.resetLearning() }
        } message: { Text("会清除累计次数，保留剪贴板内容与收藏。") }
    }
}
