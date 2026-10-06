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
    @AppStorage("retentionDays") private var retentionDays = 30
    @AppStorage("imageLimitMB") private var imageLimitMB = 20
    @AppStorage("autoPaste") private var autoPaste = false
    @AppStorage("plainText") private var plainText = false
    @AppStorage("excludedApps") private var excludedApps = ""
    @AppStorage("shortcutOption") private var shortcut = 0
    @State private var login = SMAppService.mainApp.status == .enabled
    @State private var deleteAll = false
    var body: some View {
        TabView {
            Form {
                Section("通用") {
                    Toggle("登录时启动", isOn: $login).onChange(of: login) { _, value in
                        do { if value { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() } }
                        catch { store.error = "登录项设置失败：\(error.localizedDescription)"; login = SMAppService.mainApp.status == .enabled }
                    }
                    Picker("全局快捷键", selection: $shortcut) { Text("⌥⌘V").tag(0); Text("⌃⌥V").tag(1) }
                    Toggle("快捷面板选择后自动粘贴", isOn: $autoPaste)
                    Toggle("文本默认以纯文本复制", isOn: $plainText)
                }
                Section("自动粘贴权限") {
                    Text("自动粘贴需要辅助功能权限。未授权时仍可复制内容。")
                    Button("打开辅助功能设置") {
                        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")!)
                    }
                }
            }.formStyle(.grouped).tabItem { Label("通用", systemImage: "gearshape") }
            Form {
                Section("记录类型") {
                    Toggle("文本与链接", isOn: $recordText)
                    Toggle("图片", isOn: $recordImages)
                    Toggle("原文件引用", isOn: $recordFiles)
                    Text("文件不会被备份。移动或删除原文件可能导致引用失效。")
                }
                Section("存储") {
                    Stepper("普通历史上限：\(historyLimit)", value: $historyLimit, in: 100...10000, step: 100)
                    Picker("保留时间", selection: $retentionDays) {
                        Text("7 天").tag(7); Text("30 天").tag(30); Text("90 天").tag(90); Text("永久").tag(0)
                    }
                    Stepper("单张图片上限：\(imageLimitMB) MB", value: $imageLimitMB, in: 1...100)
                    Text("收藏不受普通历史数量及保留时间限制。文本上限 2 MB。")
                    Button("立即按设置清理") { store.prune(); store.save() }
                    Button("在 Finder 中打开数据目录") { NSWorkspace.shared.open(store.directory) }
                }
            }.formStyle(.grouped).tabItem { Label("记录", systemImage: "tray") }
            Form {
                Section("排除应用") {
                    Text("每行填写一个应用 Bundle ID，例如 com.apple.keychainaccess。来源以复制时的前台应用为依据。")
                    TextEditor(text: $excludedApps).font(.system(.body, design: .monospaced)).frame(height: 100)
                    Toggle("暂停所有记录", isOn: $store.paused)
                }
                Section("本地数据") {
                    Text("历史只保存在本机，不上传。支持过滤已声明的敏感剪贴板标记；不能保证识别所有密码。")
                    Button("删除全部历史与收藏", role: .destructive) { deleteAll = true }
                }
            }.formStyle(.grouped).tabItem { Label("隐私", systemImage: "hand.raised") }
            VStack(spacing: 14) {
                Image(systemName: "doc.on.clipboard").font(.system(size: 56)).foregroundStyle(.tint)
                Text("拾贴 · ClipHarbor").font(.title)
                Text("0.1.0 · macOS 26+")
                Text("复制即收纳，随时找回来。").foregroundStyle(.secondary)
                Link("GitHub", destination: URL(string: "https://github.com/ArivenHe/ClipHarbor")!)
            }.tabItem { Label("关于", systemImage: "info.circle") }
        }
        .alert("删除全部本地历史？", isPresented: $deleteAll) {
            Button("取消", role: .cancel) {}
            Button("删除全部", role: .destructive) { store.clear(keepFavorites: false) }
        } message: { Text("包括收藏和图片缓存。不会删除原文件。") }
    }
}
