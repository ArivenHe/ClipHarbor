import SwiftUI

struct SyncSettingsView: View {
    @ObservedObject var sync: CloudSync
    @State private var server = ""
    @State private var username = ""
    @State private var password = ""
    @State private var device = ""
    @State private var remember = true
    @State private var importMode = 0
    @State private var options = SyncConfiguration()
    @State private var busy = false
    @State private var feedback = ""
    var body: some View {
        Form {
            Section("自建服务器") {
                TextField("服务器地址", text: $server, prompt: Text("https://sync.example.com"))
                TextField("账号", text: $username)
                SecureField("密码", text: $password)
                TextField("设备名称", text: $device)
                KeyboardToggle("保持登录", isOn: $remember)
                Picker("首次登录时导入本机历史", selection: $importMode) {
                    Text("只同步之后新复制的内容").tag(0)
                    Text("导入已有收藏").tag(1)
                    Text("导入已有全部历史").tag(2)
                }
                HStack {
                    Button("测试连接") { perform { try await sync.test(server: server) } }
                    Button("登录并启用同步") {
                        perform { try await sync.login(server: server, username: username, password: password, device: device, remember: remember, importMode: importMode); password = ""; options = sync.config }
                    }.buttonStyle(.borderedProminent)
                }
                Text("密码只用于登录。保持登录时，令牌存入 macOS 钥匙串。服务器地址支持 HTTPS 和子路径。")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section("同步方式") {
                KeyboardToggle("自动同步", isOn: $options.enabled)
                KeyboardToggle("跨设备直接粘贴", isOn: $options.directPaste)
                KeyboardToggle("同步文本与链接", isOn: $options.syncText)
                KeyboardToggle("同步图片", isOn: $options.syncImages)
                KeyboardToggle("同步文件", isOn: $options.syncFiles)
                Text("另一台设备复制后，在此电脑直接 ⌘V。文件完整下载并校验后才更新剪切板；无需辅助功能权限。断网重连补历史，连接期间接收新复制的内容。")
                Text("第一版支持普通文件：单文件 100 MB，一次最多 100 个、500 MB。服务器及本机同步缓存上限 2 GB。")
                    .font(.caption).foregroundStyle(.secondary)
                Button("应用同步选项") { perform { var next = sync.config; next.enabled = options.enabled; next.directPaste = options.directPaste; next.syncText = options.syncText; next.syncImages = options.syncImages; next.syncFiles = options.syncFiles; next.keepSignedIn = remember; try await sync.apply(next); feedback = "同步选项已保存。" } }
            }
            Section("状态与管理") {
                Text(sync.status).textSelection(.enabled)
                if !feedback.isEmpty { Text(feedback).foregroundStyle(.secondary).textSelection(.enabled) }
                Button("立即补同步历史") { perform { try await sync.syncNow() } }
                Button("查看未同步内容与冲突草稿") { perform { try await sync.showConflicts() } }
                Button("退出登录") { perform { try await sync.logout(); options = sync.config } }
                Text("开启同步后，选中的内容类型会上传到指定服务器，服务器管理员可读取内容。使用次数和学习统计留在本机。")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }.formStyle(.grouped).disabled(busy)
        .onAppear { server = sync.config.serverUrl; username = sync.config.username; device = sync.config.deviceName; remember = sync.config.keepSignedIn; options = sync.config }
    }
    private func perform(_ action: @escaping @MainActor () async throws -> Void) {
        busy = true; feedback = ""
        Task { @MainActor in defer { busy = false }; do { try await action() } catch { feedback = error.localizedDescription } }
    }
}
