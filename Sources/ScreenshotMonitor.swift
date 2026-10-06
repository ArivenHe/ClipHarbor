import AppKit
import Combine
import UniformTypeIdentifiers

@MainActor
final class ScreenshotMonitor: ObservableObject {
    @Published private(set) var status = "截图监听未启动"
    private let store: ClipboardStore
    private let defaults: UserDefaults
    private var configuration = ""
    private var timer: Timer?
    private var baseline: [URL: Stamp] = [:]
    private var pending: [URL: Stamp] = [:]
    private var active = false
    private struct Stamp: Equatable { let bytes: Int; let modified: Date }
    init(store: ClipboardStore, defaults: UserDefaults = .standard) { self.store = store; self.defaults = defaults }
    var configuredFolder: URL {
        let custom = defaults.string(forKey: "screenshotFolder") ?? ""
        let system = UserDefaults(suiteName: "com.apple.screencapture")?.string(forKey: "location") ?? ""
        let path = custom.isEmpty ? system : custom
        if !path.isEmpty { return URL(fileURLWithPath: (path as NSString).expandingTildeInPath, isDirectory: true) }
        return FileManager.default.urls(for: .desktopDirectory, in: .userDomainMask)[0]
    }
    func start() {
        tick()
        timer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.tick() }
        }
    }
    private func snapshot(_ folder: URL) throws -> [URL: Stamp] {
        let urls = try FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: [.fileSizeKey, .contentModificationDateKey, .isRegularFileKey], options: [.skipsHiddenFiles])
        var result: [URL: Stamp] = [:]
        let customName = UserDefaults(suiteName: "com.apple.screencapture")?.string(forKey: "name")
        var prefixes = ["screenshot", "screen shot", "screen capture", "截屏", "截图", "屏幕快照", "螢幕截圖", "スクリーンショット", "capture d’écran", "capture d'ecran", "bildschirmfoto"]
        if let customName, !customName.isEmpty { prefixes.append(customName.lowercased()) }
        let allImages = defaults.bool(forKey: "screenshotAllImages")
        for url in urls {
            guard let type = UTType(filenameExtension: url.pathExtension), type.conforms(to: .image),
                  (allImages || prefixes.contains(where: { url.lastPathComponent.lowercased().hasPrefix($0) })),
                  let values = try? url.resourceValues(forKeys: [.fileSizeKey, .contentModificationDateKey, .isRegularFileKey]), values.isRegularFile == true,
                  let bytes = values.fileSize, let modified = values.contentModificationDate else { continue }
            result[url] = Stamp(bytes: bytes, modified: modified)
        }
        return result
    }
    func tick() {
        guard defaults.bool(forKey: "watchScreenshots"), !store.paused else {
            active = false; pending.removeAll(); status = "截图记录已暂停"; return
        }
        let target = configuredFolder
        do {
            let current = try snapshot(target)
            let signature = target.path + "|" + String(defaults.bool(forKey: "screenshotAllImages")) + "|" + (UserDefaults(suiteName: "com.apple.screencapture")?.string(forKey: "name") ?? "")
            if !active || signature != configuration {
                configuration = signature
                baseline = current; pending.removeAll(); active = true
                status = "正在监听：\(target.lastPathComponent)"; return
            }
            // Two matching snapshots avoid reading a file before screenshot writing completes.
            for (url, stamp) in current where baseline[url] != stamp {
                if pending[url] == stamp && stamp.bytes > 0 {
                    if store.captureScreenshot(at: url) { baseline[url] = stamp }
                    else { baseline[url] = stamp; status = "部分截图无法读取或超过图片大小限制" }
                    pending.removeValue(forKey: url)
                } else { pending[url] = stamp }
            }
            baseline = baseline.filter { current[$0.key] != nil }
            pending = pending.filter { current[$0.key] != nil }
        } catch {
            active = false
            status = "无法读取截图目录，请检查文件夹和系统隐私权限。"
        }
    }
}
