import AppKit

enum AppBrand {
    static var icon: NSImage {
        if let url = Bundle.main.url(forResource: "ClipHarbor", withExtension: "png"), let image = NSImage(contentsOf: url) { return image }
        return NSImage(systemSymbolName: "doc.on.clipboard", accessibilityDescription: "拾贴")!
    }
    static var menuIcon: NSImage {
        // Menu bar artwork must remain legible at 18 pt and adapt to the
        // system's appearance. Never shrink the full-color application icon.
        let image = NSImage(systemSymbolName: "clipboard", accessibilityDescription: "拾贴")!
            .withSymbolConfiguration(NSImage.SymbolConfiguration(pointSize: 17, weight: .medium))!
        image.size = NSSize(width: 18, height: 18)
        image.isTemplate = true
        return image
    }
}
