import XCTest
@testable import ClipHarbor

final class ClipHarborTests: XCTestCase {
    func testImageReferencesAreBrowsedAsImagesAndKeepOriginalFileURLs() throws {
        let url = URL(fileURLWithPath: "/Users/example/WeChat/2.png")
        let original = ClipItem(kind: .files, fileURLs: [url], fileCategories: [FileCategory.image.rawValue])
        let restored = try JSONDecoder().decode(ClipItem.self, from: JSONEncoder().encode(original))
        XCTAssertEqual(restored.displayKind, .image)
        XCTAssertEqual(restored.kind, .files)
        XCTAssertEqual(restored.fileURLs, [url])
        XCTAssertEqual(restored.title, "2.png")
        XCTAssertEqual(ClipItem(kind: .files, fileURLs: [URL(fileURLWithPath: "/missing/report.pdf")]).displayKind, .files)
    }
    func testFileClassification() {
        let examples: [(String, FileCategory)] = [("report.pdf", .document), ("table.csv", .spreadsheet), ("deck.key", .presentation), ("photo.heic", .image), ("voice.mp3", .audio), ("movie.mov", .video), ("backup.zip", .archive), ("main.swift", .code), ("unknown.xyzunknown", .other)]
        for (name, expected) in examples { XCTAssertEqual(FileCategory.classify(URL(fileURLWithPath: "/nonexistent/" + name)), expected, name) }
    }
    func testHistoryRoundTripPreservesOriginalReferences() throws {
        let file = URL(fileURLWithPath: "/Users/example/Documents/report.pdf")
        let original = ClipItem(kind: .files, fileURLs: [file], fileCategories: [FileCategory.document.rawValue], favorite: true, note: "季度报告")
        let restored = try JSONDecoder().decode(ClipItem.self, from: JSONEncoder().encode(original))
        XCTAssertEqual(restored, original)
        XCTAssertEqual(restored.fileURLs, [file])
        XCTAssertTrue(restored.matches("季度"))
        XCTAssertTrue(restored.matches("REPORT"))
    }
    func testDeduplicationPreservesRichTextDifferences() {
        let plain = ClipItem(kind: .text, text: "hello")
        let same = ClipItem(kind: .text, text: "hello")
        let rich = ClipItem(kind: .text, text: "hello", richText: Data([1, 2]))
        XCTAssertTrue(plain.sameContent(as: same))
        XCTAssertFalse(plain.sameContent(as: rich))
    }
}
