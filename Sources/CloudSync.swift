import AppKit
import Combine
import CryptoKit
import Security
import Darwin

struct SyncConfiguration: Codable {
    var credentialId = UUID().uuidString
    var serverUrl = ""
    var username = ""
    var deviceName = Host.current().localizedName ?? "Mac"
    var deviceId = UUID().uuidString
    var enabled = false
    var directPaste = true
    var syncText = true
    var syncImages = true
    var syncFiles = true
    var keepSignedIn = true
    var allowLocalHttp = false
    var serverInstanceId: String?
}

enum SyncKeychain {
    private static func query(_ id: String) -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: "com.arivenhe.ClipHarbor.sync", kSecAttrAccount as String: id]
    }
    static func read(_ id: String) -> Data? {
        var request = query(id); request[kSecReturnData as String] = true; request[kSecMatchLimit as String] = kSecMatchLimitOne
        var value: CFTypeRef?
        return SecItemCopyMatching(request as CFDictionary, &value) == errSecSuccess ? value as? Data : nil
    }
    static func write(_ id: String, data: Data) throws {
        let request = query(id)
        var status = SecItemUpdate(request as CFDictionary, [kSecValueData as String: data] as CFDictionary)
        if status == errSecItemNotFound {
            var insert = request; insert[kSecValueData as String] = data; insert[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
            status = SecItemAdd(insert as CFDictionary, nil)
        }
        guard status == errSecSuccess else { throw SyncFailure("无法保存钥匙串登录信息（\(status)）。") }
    }
    static func delete(_ id: String) { SecItemDelete(query(id) as CFDictionary) }
}
struct SyncFailure: LocalizedError { let message: String; init(_ message: String) { self.message = message }; var errorDescription: String? { message } }

@MainActor
final class CloudSync: ObservableObject {
    @Published var config: SyncConfiguration
    @Published var status = "未登录"
    @Published var busy = false
    private unowned let store: ClipboardStore
    private var process: Process?
    private var input: FileHandle?
    private var output: FileHandle?
    private var errors: FileHandle?
    private var buffer = Data()
    private var requests: [String: CheckedContinuation<Data, any Error>] = [:]
    private var session: Data?
    private var heartbeat: Timer?
    private var retry: Task<Void, Never>?
    private var starting = false
    private var stopped = false
    private var space: String?
    init(store: ClipboardStore) {
        self.store = store
        config = (UserDefaults.standard.data(forKey: "syncConfiguration").flatMap { try? JSONDecoder().decode(SyncConfiguration.self, from: $0) }) ?? SyncConfiguration()
        if config.keepSignedIn { session = SyncKeychain.read(config.credentialId) }
        if session != nil { store.captureSyncSpace = store.activeSyncSpace }
        store.captured = { [weak self] item, version, real in self?.capture(item, version: version, real: real) }
        store.syncMutation = { [weak self] item, type in self?.mutate(item, type: type) }
    }
    func start() {
        guard process == nil, !starting, !stopped else { return }
        starting = true
        Task { @MainActor in
            defer { starting = false }
            do {
                try launch()
                _ = try await command("initialize", params: initialParameters())
                await retryRevocations()
            } catch { status = "同步未连接：\(error.localizedDescription)" }
        }
    }
    private func launch() throws {
        let architecture: String
        #if arch(arm64)
        architecture = "arm64"
        #else
        architecture = "x64"
        #endif
        let helper = Bundle.main.bundleURL.appendingPathComponent("Contents/Helpers/Sync/\(architecture)/ClipHarbor.SyncHost")
        guard FileManager.default.isExecutableFile(atPath: helper.path) else { throw SyncFailure("此构建未包含同步组件，请安装 GitHub Actions 生成的完整版本。") }
        let next = Process(); next.executableURL = helper; next.currentDirectoryURL = helper.deletingLastPathComponent()
        let stdin = Pipe(), stdout = Pipe(), stderr = Pipe()
        next.standardInput = stdin; next.standardOutput = stdout; next.standardError = stderr
        input = stdin.fileHandleForWriting; output = stdout.fileHandleForReading; errors = stderr.fileHandleForReading
        output?.readabilityHandler = { [weak self] handle in
            let bytes = handle.availableData
            Task { @MainActor in self?.read(bytes) }
        }
        // Runtime diagnostics may contain implementation details; drain without logging clipboard data.
        errors?.readabilityHandler = { handle in _ = handle.availableData }
        next.terminationHandler = { [weak self] _ in Task { @MainActor in self?.disconnected() } }
        try next.run(); process = next
        heartbeat = Timer.scheduledTimer(withTimeInterval: 300, repeats: true) { [weak self] _ in Task { @MainActor in await self?.retryRevocations() } }
    }
    private func initialParameters() -> [String: Any] {
        var params: [String: Any] = ["root": store.directory.path, "config": dictionary(config)]
        if let session, let value = try? JSONSerialization.jsonObject(with: session) { params["session"] = value }
        return params
    }
    private func dictionary<T: Encodable>(_ value: T) -> [String: Any] { (try? JSONSerialization.jsonObject(with: JSONEncoder().encode(value))) as? [String: Any] ?? [:] }
    private func send(_ value: [String: Any]) throws {
        guard let input, process?.isRunning == true else { throw SyncFailure("同步组件未运行。") }
        var data = try JSONSerialization.data(withJSONObject: value); data.append(10); try input.write(contentsOf: data)
    }
    private func command(_ method: String, params: [String: Any] = [:]) async throws -> Data {
        let id = UUID().uuidString
        return try await withCheckedThrowingContinuation { completion in
            requests[id] = completion
            do { try send(["id": id, "method": method, "params": params]) }
            catch { requests.removeValue(forKey: id); completion.resume(throwing: error) }
        }
    }
    private func read(_ bytes: Data) {
        guard !bytes.isEmpty else { return }
        buffer.append(bytes)
        guard buffer.count <= 8 * 1024 * 1024 else { process?.terminate(); return }
        while let index = buffer.firstIndex(of: 10) {
            let line = buffer.prefix(upTo: index); buffer.removeSubrange(...index)
            guard let message = try? JSONSerialization.jsonObject(with: line) as? [String: Any] else { continue }
            if let id = message["id"] as? String, let completion = requests.removeValue(forKey: id) {
                if let error = message["error"] as? String { completion.resume(throwing: SyncFailure(error)) }
                else { completion.resume(returning: (try? JSONSerialization.data(withJSONObject: message["result"] ?? [:])) ?? Data("{}".utf8)) }
            } else { callback(message) }
        }
    }
    private var unlocked: Bool {
        guard let state = CGSessionCopyCurrentDictionary() as? [String: Any] else { return false }
        return (state["CGSSessionScreenIsLocked"] as? Bool) != true && (state[kCGSessionOnConsoleKey as String] as? Bool) == true
    }
    private func callback(_ message: [String: Any]) {
        let id = message["callId"] as? String
        let params = message["params"] as? [String: Any] ?? [:]
        do {
            var result: [String: Any] = [:]
            switch message["method"] as? String {
            case "status": status = params["value"] as? String ?? status
            case "clipboardState": result = ["version": NSPasteboard.general.changeCount, "unlocked": unlocked && !store.paused]
            case "persist":
                guard let value = params["session"], let credential = params["credentialId"] as? String else { throw SyncFailure("登录信息格式无效。") }
                let data = try JSONSerialization.data(withJSONObject: value)
                if credential != config.credentialId { store.captureSyncSpace = nil }
                if params["keepSignedIn"] as? Bool == true { try SyncKeychain.write(credential, data: data) } else { SyncKeychain.delete(credential) }
                session = data
            case "space":
                space = params["space"] as? String; store.activeSyncSpace = space; store.captureSyncSpace = space; UserDefaults.standard.set(space, forKey: "activeSyncSpace")
            case "configuration":
                if let value = params["config"] { config = try JSONDecoder().decode(SyncConfiguration.self, from: JSONSerialization.data(withJSONObject: value)); saveConfiguration() }
            case "bind": bind(params)
            case "receive": try receive(params)
            default: throw SyncFailure("同步组件返回未知操作。")
            }
            if let id { try send(["callId": id, "result": result]) }
        } catch {
            if let id { try? send(["callId": id, "error": error.localizedDescription]) }
            status = "同步待重试：\(error.localizedDescription)"
        }
    }
    private func bind(_ params: [String: Any]) {
        guard let wire = params["record"] as? [String: Any], let space = params["space"] as? String,
              let id = wire["recordId"] as? String, let hash = wire["contentHash"] as? String,
              let index = store.items.firstIndex(where: { ($0.syncSpace == nil || $0.syncSpace == space) && ($0.id.uuidString.caseInsensitiveCompare(params["sourceId"] as? String ?? id) == .orderedSame || $0.syncRecordId?.caseInsensitiveCompare(params["sourceId"] as? String ?? id) == .orderedSame || $0.id.uuidString.caseInsensitiveCompare(id) == .orderedSame || $0.syncRecordId?.caseInsensitiveCompare(id) == .orderedSame || $0.syncContentHash == hash) }) else { return }
        store.items[index].syncSpace = space; store.items[index].syncRecordId = id; store.items[index].syncContentHash = hash
        if (wire["revision"] as? Int ?? 0) > 0 { store.items[index].favorite = wire["favorite"] as? Bool ?? false; store.items[index].note = wire["note"] as? String ?? "" }
        let localId = store.items[index].id
        let duplicates = store.items.filter { $0.id != localId && $0.syncSpace == space && $0.syncRecordId == id }
        for duplicate in duplicates { store.items[index].captureCount = max(store.items[index].captureCount ?? 0, duplicate.captureCount ?? 0); store.items[index].useCount = max(store.items[index].useCount ?? 0, duplicate.useCount ?? 0) }
        let duplicateIds = Set(duplicates.map(\.id)); store.items.removeAll { duplicateIds.contains($0.id) }; store.save()
    }
    private func receive(_ params: [String: Any]) throws {
        guard let received = params["received"] as? [String: Any], let wire = received["record"] as? [String: Any], let id = wire["recordId"] as? String, let space = params["space"] as? String else { throw SyncFailure("远端内容格式无效。") }
        if wire["deletedAt"] is String {
            store.items.removeAll { $0.syncSpace == space && $0.syncRecordId?.caseInsensitiveCompare(id) == .orderedSame }; store.save(); return
        }
        guard let rawKind = wire["kind"] as? String, let kind = ClipKind(rawValue: rawKind) else { throw SyncFailure("不支持远端内容类型。") }
        var item = ClipItem(kind: kind, text: wire["text"] as? String, richText: (wire["rtfBase64"] as? String).flatMap { Data(base64Encoded: $0) }, html: wire["html"] as? String)
        item.id = UUID(uuidString: id) ?? UUID(); item.syncSpace = space; item.syncRecordId = id; item.syncContentHash = wire["contentHash"] as? String
        item.favorite = wire["favorite"] as? Bool ?? false; item.note = wire["note"] as? String ?? ""; item.source = "另一台设备"; item.captureCount = 0
        if (wire["originDeviceId"] as? String)?.caseInsensitiveCompare(config.deviceId) == .orderedSame { item.source = store.items.first { $0.syncSpace == space && $0.syncRecordId == id }?.source ?? "本机" }
        let formatter = ISO8601DateFormatter(); formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = wire["lastCapturedAt"] as? String { item.date = formatter.date(from: date) ?? ISO8601DateFormatter().date(from: date) ?? Date() }
        item.fileURLs = (received["filePaths"] as? [String] ?? []).map { URL(fileURLWithPath: $0) }; item.fileCategories = item.fileURLs.map { FileCategory.classify($0).rawValue }
        for file in item.fileURLs {
            let quarantine = "0081;\(String(Int(Date().timeIntervalSince1970), radix: 16));ClipHarbor;\(UUID().uuidString)"
            let bytes = Array(quarantine.utf8)
            let result = bytes.withUnsafeBytes { setxattr(file.path, "com.apple.quarantine", $0.baseAddress, $0.count, 0, 0) }
            guard result == 0 else { throw SyncFailure("无法写入接收文件的系统下载标记。") }
        }
        if let path = received["imagePath"] as? String {
            let data = try Data(contentsOf: URL(fileURLWithPath: path))
            guard let bitmap = NSBitmapImageRep(data: data), bitmap.pixelsWide > 0, bitmap.pixelsHigh > 0, bitmap.pixelsWide <= 40_000_000 / bitmap.pixelsHigh else { throw SyncFailure("接收图片无法安全解码。") }
            let name = SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined() + ".png"
            try data.write(to: store.imageDirectory.appendingPathComponent(name), options: .atomic); item.imageName = name
        }
        if params["hidden"] as? Bool != true { store.importRemote(item) }
        if params["direct"] as? Bool == true, config.enabled, config.directPaste, unlocked, !store.paused, let expected = params["expected"] as? Int {
            guard NSPasteboard.general.changeCount == expected else { throw SyncFailure("本机已复制新内容，保留当前剪切板。") }
            guard store.copy(item, remote: true, expectedVersion: expected) else { throw SyncFailure("写入原生剪切板失败。") }
        }
    }
    private func capture(_ item: ClipItem, version: Int, real: Bool) {
        guard session != nil, item.syncSpace == nil || item.syncSpace == space else { return }
        var wire: [String: Any] = ["recordId": item.syncRecordId ?? item.id.uuidString, "kind": item.kind.rawValue, "capturedAt": ISO8601DateFormatter().string(from: item.date), "lastCapturedAt": ISO8601DateFormatter().string(from: Date()), "favorite": item.favorite, "note": item.note]
        if let text = item.text { wire["text"] = text }; if let rtf = item.richText { wire["rtfBase64"] = rtf.base64EncodedString() }; if let html = item.html { wire["html"] = html }
        let paths = item.imageName.map { [store.imageDirectory.appendingPathComponent($0).path] } ?? item.fileURLs.map(\.path)
        Task { @MainActor in do { _ = try await command("capture", params: ["record": wire, "paths": paths, "version": version, "real": real]) } catch { status = "同步失败：\(error.localizedDescription)" } }
    }
    private func mutate(_ item: ClipItem, type: String) {
        guard item.syncSpace == space, let id = item.syncRecordId else { return }
        var params: [String: Any] = ["recordId": id, "type": type]
        if type == "favorite" { params["favorite"] = item.favorite }; if type == "note" { params["note"] = item.note }
        Task { @MainActor in do { _ = try await command("mutate", params: params) } catch { status = "同步失败：\(error.localizedDescription)" } }
    }
    private func saveConfiguration() { if let data = try? JSONEncoder().encode(config) { UserDefaults.standard.set(data, forKey: "syncConfiguration") } }
    func test(server: String) async throws { _ = try await command("test", params: ["server": server]); status = "连接成功，服务器协议兼容。" }
    func login(server: String, username: String, password: String, device: String, remember: Bool, importMode: Int) async throws {
        let oldCredential = config.credentialId
        var proposed = config; proposed.serverUrl = server.trimmingCharacters(in: .whitespacesAndNewlines); proposed.username = username.trimmingCharacters(in: .whitespacesAndNewlines); proposed.deviceName = device; proposed.keepSignedIn = remember
        let result = try await command("login", params: ["config": dictionary(proposed), "password": password])
        guard let response = try JSONSerialization.jsonObject(with: result) as? [String: Any], let value = response["config"] else { throw SyncFailure("登录响应无效。") }
        config = try JSONDecoder().decode(SyncConfiguration.self, from: JSONSerialization.data(withJSONObject: value)); saveConfiguration(); SyncKeychain.delete(oldCredential)
        if importMode > 0 { for item in store.items where item.syncSpace == nil && (importMode == 2 || item.favorite) { capture(item, version: NSPasteboard.general.changeCount, real: false) } }
    }
    func apply(_ next: SyncConfiguration) async throws { config = next; saveConfiguration(); _ = try await command("configure", params: initialParameters()) }
    func syncNow() async throws { _ = try await command("sync"); status = "历史已补同步。" }
    func showConflicts() async throws {
        let data = try await command("conflicts")
        if let response = try JSONSerialization.jsonObject(with: data) as? [String: Any], let path = response["path"] as? String { NSWorkspace.shared.open(URL(fileURLWithPath: path)) }
    }
    func logout() async throws {
        let result = try await command("logout"); let response = try JSONSerialization.jsonObject(with: result) as? [String: Any]
        session = nil; store.captureSyncSpace = nil; SyncKeychain.delete(config.credentialId); config.enabled = false; saveConfiguration()
        if let token = response?["revokeToken"] as? String, let server = response?["server"] as? String {
            let id = "revoke-" + UUID().uuidString; try SyncKeychain.write(id, data: JSONSerialization.data(withJSONObject: ["server": server, "revokeToken": token]))
            var ids = UserDefaults.standard.stringArray(forKey: "syncRevocations") ?? []; ids.append(id); UserDefaults.standard.set(ids, forKey: "syncRevocations"); await retryRevocations()
        }
    }
    private func retryRevocations() async {
        let pending = UserDefaults.standard.stringArray(forKey: "syncRevocations") ?? []
        for id in pending {
            guard let data = SyncKeychain.read(id), let params = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { continue }
            do {
                _ = try await command("revoke", params: params)
                SyncKeychain.delete(id)
                var current = UserDefaults.standard.stringArray(forKey: "syncRevocations") ?? []
                current.removeAll { $0 == id }; UserDefaults.standard.set(current, forKey: "syncRevocations")
            } catch { }
        }
    }
    private func disconnected() {
        heartbeat?.invalidate(); heartbeat = nil; output?.readabilityHandler = nil; errors?.readabilityHandler = nil
        process = nil; input = nil; output = nil; errors = nil; buffer.removeAll()
        for completion in requests.values { completion.resume(throwing: SyncFailure("同步组件连接已结束。")) }; requests.removeAll()
        if !stopped { status = "同步组件已退出，正在重新连接"; retry = Task { @MainActor in try? await Task.sleep(for: .seconds(3)); if !Task.isCancelled { start() } } }
    }
    func stop() { stopped = true; retry?.cancel(); heartbeat?.invalidate(); try? input?.close(); process?.terminate() }
}
