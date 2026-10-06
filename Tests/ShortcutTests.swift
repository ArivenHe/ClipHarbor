import XCTest
import AppKit
@testable import ClipHarbor

final class ShortcutTests: XCTestCase {
    func testCollisionUsesPhysicalKeyAndModifiersRatherThanLabel() {
        let first = KeyCombination(keyCode: 9, modifiers: NSEvent.ModifierFlags.command.rawValue, key: "V")
        let translated = KeyCombination(keyCode: 9, modifiers: NSEvent.ModifierFlags.command.rawValue, key: "另一个键盘布局")
        let other = KeyCombination(keyCode: 9, modifiers: NSEvent.ModifierFlags([.command, .shift]).rawValue, key: "V")
        XCTAssertTrue(first.conflicts(with: translated))
        XCTAssertFalse(first.conflicts(with: other))
    }
    @MainActor func testDefaultCombinationsAreUnique() async {
        let keys = Array(ShortcutManager.defaultCombinations.values)
        for index in keys.indices {
            for other in keys.indices where other > index {
                XCTAssertFalse(keys[index].conflicts(with: keys[other]))
            }
        }
    }
    func testFileTypeActionsTargetCorrectFilters() {
        XCTAssertEqual(ShortcutAction.imageFile.filter?.kind, "files")
        XCTAssertEqual(ShortcutAction.imageFile.filter?.category, "image")
        XCTAssertEqual(ShortcutAction.image.filter?.kind, "image")
        XCTAssertTrue(ShortcutAction.panel.global)
        XCTAssertFalse(ShortcutAction.delete.global)
        XCTAssertTrue(ShortcutAction.quit.applicationWide)
    }
}
