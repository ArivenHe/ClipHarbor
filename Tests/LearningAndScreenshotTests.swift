import XCTest
import AppKit
@testable import ClipHarbor

final class LearningAndScreenshotTests: XCTestCase {
    func testFrequencyPrioritizesUseAndHonorsExclusions() {
        let copied = ClipItem(kind: .text, text: "copied", captureCount: 5)
        let used = ClipItem(kind: .text, text: "used", captureCount: 1, useCount: 2)
        let excluded = ClipItem(kind: .text, text: "excluded", captureCount: 100, excludedFromLearning: true)
        let once = ClipItem(kind: .text, text: "once")
        XCTAssertEqual(FrequentContent.ranked([copied, used, excluded, once], threshold: 2).map(\.id), [used.id, copied.id])
    }
    func testSensitiveCandidatesAreNotPromoted() {
        for text in ["api_key=abcdefgh", "Bearer abcdef", "123456", "sk-abcdefghijklmnop", "https://example.com/?token=abc"] {
            XCTAssertFalse(FrequentContent.eligible(ClipItem(kind: .text, text: text)), text)
        }
        XCTAssertTrue(FrequentContent.eligible(ClipItem(kind: .text, text: "常用的回复文字")))
    }
    func testLegacyHistoryDecodesWithoutLearningFields() throws {
        let original = ClipItem(kind: .text, text: "legacy")
        let data = try JSONEncoder().encode(original)
        let decoded = try JSONDecoder().decode(ClipItem.self, from: data)
        XCTAssertEqual(decoded.captures, 1)
        XCTAssertEqual(decoded.uses, 0)
        XCTAssertNil(decoded.screenshotURL)
    }
    @MainActor func testScreenshotsAreCachedAndOldImagesNotImported() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let screenshots = root.appendingPathComponent("Screenshots")
        try FileManager.default.createDirectory(at: screenshots, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let suite = "ClipHarborScreenshotTests." + UUID().uuidString
        let defaults = UserDefaults(suiteName: suite)!
        defer { defaults.removePersistentDomain(forName: suite) }
        defaults.set(screenshots.path, forKey: "screenshotFolder")
        let store = ClipboardStore(directory: root.appendingPathComponent("History"), defaults: defaults, startMonitoring: false)
        let monitor = ScreenshotMonitor(store: store, defaults: defaults)
        let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 2, pixelsHigh: 2, bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
        for x in 0..<2 { for y in 0..<2 { bitmap.setColor(.red, atX: x, y: y) } }
        let png = try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
        try png.write(to: screenshots.appendingPathComponent("Screenshot old.png"))
        monitor.tick()
        XCTAssertTrue(store.items.isEmpty)
        let fresh = screenshots.appendingPathComponent("Screenshot new.png")
        try png.write(to: fresh)
        monitor.tick(); monitor.tick()
        XCTAssertEqual(store.items.count, 1)
        XCTAssertEqual(try XCTUnwrap(store.items[0].screenshotURL).resolvingSymlinksInPath(), fresh.resolvingSymlinksInPath())
        let cached = store.imageDirectory.appendingPathComponent(try XCTUnwrap(store.items[0].imageName))
        try FileManager.default.removeItem(at: fresh)
        XCTAssertTrue(FileManager.default.fileExists(atPath: cached.path))
        XCTAssertNotNil(NSImage(contentsOf: cached))
        store.paused = true
        try png.write(to: screenshots.appendingPathComponent("Screenshot paused.png"))
        monitor.tick()
        store.paused = false
        monitor.tick(); monitor.tick()
        XCTAssertEqual(store.items.count, 1)
        try png.write(to: screenshots.appendingPathComponent("existing arbitrary.png"))
        defaults.set(true, forKey: "screenshotAllImages")
        monitor.tick(); monitor.tick()
        XCTAssertEqual(store.items[0].captures, 1)
        try png.write(to: screenshots.appendingPathComponent("new arbitrary.png"))
        monitor.tick(); monitor.tick()
        XCTAssertEqual(store.items[0].captures, 2)
    }
    @MainActor func testRepeatedContentPreservesFavoriteAndAccumulatesLearning() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let suite = "ClipHarborLearningTests." + UUID().uuidString
        let defaults = UserDefaults(suiteName: suite)!
        defer { defaults.removePersistentDomain(forName: suite) }
        let store = ClipboardStore(directory: root, defaults: defaults, startMonitoring: false)
        let first = ClipItem(kind: .text, text: "common", favorite: true, note: "备注")
        store.add(first); store.add(ClipItem(kind: .text, text: "common"))
        XCTAssertEqual(store.items.count, 1)
        XCTAssertEqual(store.items[0].captures, 2)
        XCTAssertTrue(store.items[0].favorite)
        XCTAssertEqual(store.items[0].note, "备注")
        defaults.set(false, forKey: "learningEnabled")
        store.add(ClipItem(kind: .text, text: "common"))
        XCTAssertEqual(store.items[0].captures, 2)
        store.resetLearning()
        XCTAssertTrue(FrequentContent.ranked(store.items, threshold: 2).isEmpty)
    }
}
