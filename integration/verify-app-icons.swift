// Check the built bundle, since an icon build setting alone does not prove
// Finder will receive an icon declaration in the processed Info.plist.
import AppKit
import Foundation

func require(_ condition: Bool, _ message: String) {
    guard condition else {
        fputs("Application icon check failed: \(message)\n", stderr)
        exit(1)
    }
}

require(CommandLine.arguments.count == 2, "pass the path to ClipHarbor.app")
let app = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
guard let bundle = Bundle(url: app) else {
    fputs("Application icon check failed: invalid app bundle\n", stderr)
    exit(1)
}
require(bundle.object(forInfoDictionaryKey: "CFBundleIconFile") as? String == "ClipHarbor.icns",
        "Info.plist must declare CFBundleIconFile=ClipHarbor.icns")

for ext in ["icns", "png"] {
    guard let url = bundle.url(forResource: "ClipHarbor", withExtension: ext),
          let image = NSImage(contentsOf: url) else {
        fputs("Application icon check failed: missing or unreadable ClipHarbor.\(ext)\n", stderr)
        exit(1)
    }
    require(image.isValid, "ClipHarbor.\(ext) cannot be decoded by AppKit")
    require(image.representations.contains { $0.pixelsWide >= 1024 && $0.pixelsHigh >= 1024 },
            "ClipHarbor.\(ext) must include a 1024px representation")
    if ext == "png", let bitmap = image.representations.first as? NSBitmapImageRep {
        require(bitmap.hasAlpha, "application artwork must preserve transparent margins")
        require(bitmap.colorAt(x: 0, y: 0)?.alphaComponent == 0,
                "application artwork must not have an opaque rectangular background")
    }
}
print("PASS packaged icon declaration, AppKit decoding, 1024px artwork and transparency")
