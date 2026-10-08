import AppKit

enum AppBrand {
    static var icon: NSImage {
        if let url = Bundle.main.url(forResource: "ClipHarbor", withExtension: "png"), let image = NSImage(contentsOf: url) { return image }
        return NSImage(systemSymbolName: "doc.on.clipboard", accessibilityDescription: "拾贴")!
    }
    static var menuIcon: NSImage {
        let image = icon
        image.size = NSSize(width: 18, height: 18)
        image.isTemplate = false
        return image
    }
}
