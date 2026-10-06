import XCTest
import AppKit
@testable import ClipHarbor

final class CloudSyncTests: XCTestCase {
    @MainActor func testRemoteHistoryDoesNotBecomeNewLocalCapture() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        let defaults = UserDefaults(suiteName: UUID().uuidString)!
        let store = ClipboardStore(directory: directory, defaults: defaults, startMonitoring: false)
        store.activeSyncSpace = "one"
        var captures = 0; store.captured = { _, _, _ in captures += 1 }
        let local = ClipItem(kind: .text, text: "same", captureCount: 5, useCount: 3, syncSpace: "one", syncRecordId: "record", syncContentHash: "hash")
        store.items = [local]
        store.importRemote(ClipItem(kind: .text, text: "same", favorite: true, captureCount: 0, syncSpace: "one", syncRecordId: "record", syncContentHash: "hash"))
        XCTAssertEqual(store.items.count, 1); XCTAssertEqual(store.items[0].id, local.id); XCTAssertEqual(store.items[0].captureCount, 5); XCTAssertEqual(store.items[0].useCount, 3); XCTAssertEqual(captures, 0)
        var mutations: [String] = []; store.syncMutation = { _, type in mutations.append(type) }
        store.clear(keepFavorites: false); XCTAssertEqual(mutations, ["hide"])
    }
    @MainActor func testOldAccountIsIsolatedFromNewCopyAndCleanup() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        let store = ClipboardStore(directory: directory, defaults: UserDefaults(suiteName: UUID().uuidString)!, startMonitoring: false)
        store.activeSyncSpace = "new"
        store.items = [ClipItem(kind: .text, text: "same", favorite: true, syncSpace: "old")]
        let copied = store.add(ClipItem(kind: .text, text: "same"))
        XCTAssertEqual(store.items.count, 2); XCTAssertFalse(copied.favorite); XCTAssertEqual(store.visibleItems.count, 1)
        store.clear(keepFavorites: false); XCTAssertEqual(store.items.count, 1); XCTAssertEqual(store.items[0].syncSpace, "old")
    }
    func testConfigurationNeverEncodesPasswordOrToken() throws {
        let json = String(data: try JSONEncoder().encode(SyncConfiguration()), encoding: .utf8)!
        XCTAssertFalse(json.lowercased().contains("password")); XCTAssertFalse(json.lowercased().contains("token"))
    }
    @MainActor func testCopyAfterLogoutStaysLocal() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        let store = ClipboardStore(directory: directory, defaults: UserDefaults(suiteName: UUID().uuidString)!, startMonitoring: false)
        store.activeSyncSpace = "old"; store.captureSyncSpace = nil
        store.items = [ClipItem(kind: .text, text: "same", favorite: true, syncSpace: "old", syncRecordId: UUID().uuidString)]
        let local = store.add(ClipItem(kind: .text, text: "same"))
        XCTAssertNil(local.syncSpace); XCTAssertFalse(local.favorite); XCTAssertEqual(store.items.count, 2)
        XCTAssertEqual(ClipItem(kind: .text, text: "remote", captureCount: 0).captures, 0)
    }
}
