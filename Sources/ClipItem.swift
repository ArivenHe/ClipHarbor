import Foundation
import UniformTypeIdentifiers

enum ClipKind: String, Codable, CaseIterable, Identifiable {
    case text, link, image, files
    var id: String { rawValue }
    var title: String {
        switch self { case .text: "文本"; case .link: "链接"; case .image: "图片"; case .files: "文件" }
    }
    var symbol: String {
        switch self { case .text: "text.alignleft"; case .link: "link"; case .image: "photo"; case .files: "doc.on.doc" }
    }
}
enum FileCategory: String, CaseIterable, Identifiable {
    case document, spreadsheet, presentation, image, audio, video, archive, code, folder, other
    var id: String { rawValue }
    var title: String {
        switch self {
        case .document: "文档"; case .spreadsheet: "表格"; case .presentation: "演示"
        case .image: "图片文件"; case .audio: "音频"; case .video: "视频"
        case .archive: "压缩包"; case .code: "代码"; case .folder: "文件夹"; case .other: "其他"
        }
    }
    static func classify(_ url: URL) -> Self {
        if (try? url.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true { return .folder }
        let ext = url.pathExtension.lowercased()
        if ["xls", "xlsx", "numbers", "csv", "tsv"].contains(ext) { return .spreadsheet }
        if ["ppt", "pptx", "key"].contains(ext) { return .presentation }
        if ["swift", "js", "ts", "tsx", "jsx", "py", "rs", "go", "c", "cpp", "h", "java", "json", "yaml", "yml", "sh", "css", "html"].contains(ext) { return .code }
        if ["zip", "7z", "rar", "tar", "gz", "bz2", "xz"].contains(ext) { return .archive }
        if let type = UTType(filenameExtension: ext) {
            if type.conforms(to: .image) { return .image }
            if type.conforms(to: .audio) { return .audio }
            if type.conforms(to: .movie) { return .video }
            if type.conforms(to: .text) || type.conforms(to: .pdf) { return .document }
        }
        if ["doc", "docx", "pages", "rtf"].contains(ext) { return .document }
        return .other
    }
}
struct ClipItem: Identifiable, Codable, Equatable {
    var id = UUID()
    var date = Date()
    var kind: ClipKind
    var text: String?
    var richText: Data?
    var imageName: String?
    var fileURLs: [URL] = []
    var fileCategories: [String] = []
    var source: String?
    var favorite = false
    var note = ""
    var screenshotURL: URL?
    var captureCount: Int?
    var useCount: Int?
    var lastUsedAt: Date?
    var excludedFromLearning: Bool?
    var captures: Int { max(1, captureCount ?? 1) }
    var uses: Int { max(0, useCount ?? 0) }
    // File-reference transport is preserved for copying, while browsing uses
    // the actual content type. This also reclassifies existing history.
    var displayKind: ClipKind {
        if kind == .files, !fileURLs.isEmpty, fileURLs.allSatisfy({ FileCategory.classify($0) == .image }) { return .image }
        return kind
    }
    var title: String {
        if kind == .files { return fileURLs.map(\.lastPathComponent).joined(separator: "、") }
        if kind == .image { return screenshotURL?.lastPathComponent ?? "图片" }
        return String((text ?? "").prefix(240))
    }
    func matches(_ query: String) -> Bool {
        query.isEmpty || [title, text ?? "", note, source ?? ""].contains { $0.localizedCaseInsensitiveContains(query) }
    }
    func sameContent(as other: Self) -> Bool {
        kind == other.kind && text == other.text && richText == other.richText && fileURLs == other.fileURLs && imageName == other.imageName
    }
}
