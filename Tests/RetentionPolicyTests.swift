import XCTest
@testable import ClipHarbor

final class RetentionPolicyTests: XCTestCase {
    private func withDefaults(_ body: (UserDefaults) throws -> Void) rethrows {
        let suite = "ClipHarborTests." + UUID().uuidString
        let defaults = UserDefaults(suiteName: suite)!
        defer { defaults.removePersistentDomain(forName: suite) }
        defaults.set(100, forKey: "historyLimit")
        defaults.set("minutes", forKey: "retention.unit")
        defaults.set(10, forKey: "retention.value")
        try body(defaults)
    }
    func testExpiresAtBoundaryWithoutNewClipboardContent() {
        withDefaults { defaults in
            let now = Date(timeIntervalSince1970: 10000)
            let expired = ClipItem(date: now.addingTimeInterval(-600), kind: .text, text: "old")
            let fresh = ClipItem(date: now.addingTimeInterval(-599), kind: .text, text: "new")
            XCTAssertEqual(RetentionPolicy.keeping([fresh, expired], defaults: defaults, now: now).map(\.id), [fresh.id])
        }
    }
    func testExpiredRecordDoesNotConsumeCapacity() {
        withDefaults { defaults in
            defaults.set(1, forKey: "historyLimit")
            let now = Date(timeIntervalSince1970: 10000)
            let expiredImage = ClipItem(date: now.addingTimeInterval(-100), kind: .image, imageName: "old.png")
            defaults.set(true, forKey: "retention.image.override")
            defaults.set("minutes", forKey: "retention.image.unit")
            defaults.set(1, forKey: "retention.image.value")
            let validText = ClipItem(date: now.addingTimeInterval(-200), kind: .text, text: "valid")
            XCTAssertEqual(RetentionPolicy.keeping([expiredImage, validText], defaults: defaults, now: now).map(\.id), [validText.id])
        }
    }
    func testFavoritesCanBeExemptOrFollowPolicy() {
        withDefaults { defaults in
            let now = Date(timeIntervalSince1970: 10000)
            let favorite = ClipItem(date: now.addingTimeInterval(-601), kind: .text, text: "favorite", favorite: true)
            XCTAssertEqual(RetentionPolicy.keeping([favorite], defaults: defaults, now: now).count, 1)
            defaults.set(false, forKey: "favoritesExempt")
            XCTAssertTrue(RetentionPolicy.keeping([favorite], defaults: defaults, now: now).isEmpty)
        }
    }
    func testForeverStillHonorsCapacity() {
        withDefaults { defaults in
            defaults.set("forever", forKey: "retention.unit")
            defaults.set(1, forKey: "historyLimit")
            let items = [ClipItem(kind: .text, text: "first"), ClipItem(kind: .text, text: "second")]
            XCTAssertNil(RetentionPolicy.lifetime(for: .text, defaults: defaults))
            XCTAssertEqual(RetentionPolicy.keeping(items, defaults: defaults).map(\.id), [items[0].id])
        }
    }
    func testPerTypeOverrideAndUnitConversion() {
        withDefaults { defaults in
            defaults.set(true, forKey: "retention.files.override")
            defaults.set("weeks", forKey: "retention.files.unit")
            defaults.set(2, forKey: "retention.files.value")
            XCTAssertEqual(RetentionPolicy.lifetime(for: .files, defaults: defaults), 1209600)
            XCTAssertEqual(RetentionPolicy.lifetime(for: .text, defaults: defaults), 600)
            defaults.set("hours", forKey: "retention.unit")
            defaults.set(2, forKey: "retention.value")
            XCTAssertEqual(RetentionPolicy.lifetime(for: .link, defaults: defaults), 7200)
        }
    }
    func testLegacyMigrationIsIdempotent() {
        withDefaults { defaults in
            defaults.removeObject(forKey: "retention.unit")
            defaults.set(0, forKey: "retentionDays")
            RetentionPolicy.migrate(defaults)
            XCTAssertEqual(defaults.string(forKey: "retention.unit"), "forever")
            defaults.set("hours", forKey: "retention.unit")
            defaults.set(4, forKey: "retention.value")
            RetentionPolicy.migrate(defaults)
            XCTAssertEqual(RetentionPolicy.lifetime(for: .text, defaults: defaults), 14400)
        }
    }
}
