// Renders the app icon (purple rounded square with a ❯) and builds Resources/Slate.icns.
//   swift mac/make-icon.swift
import AppKit

let root = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
let iconset = FileManager.default.temporaryDirectory.appendingPathComponent("Slate.iconset")
try? FileManager.default.removeItem(at: iconset)
try FileManager.default.createDirectory(at: iconset, withIntermediateDirectories: true)

func render(_ px: Int) -> Data {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: px, bitsPerSample: 8,
                               samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                               bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    let s = CGFloat(px)
    // macOS icon grid: the tile is ~80% of the canvas.
    let tile = NSRect(x: s * 0.1, y: s * 0.1, width: s * 0.8, height: s * 0.8)
    let path = NSBezierPath(roundedRect: tile, xRadius: s * 0.18, yRadius: s * 0.18)
    NSGradient(starting: NSColor(srgbRed: 0x4C / 255, green: 0x1D / 255, blue: 0x95 / 255, alpha: 1),
               ending: NSColor(srgbRed: 0x14 / 255, green: 0x08 / 255, blue: 0x2A / 255, alpha: 1))!.draw(in: path, angle: -45)
    NSColor(srgbRed: 0xA7 / 255, green: 0x8B / 255, blue: 0xFA / 255, alpha: 1).setStroke()
    path.lineWidth = max(1, s * 0.02)
    path.stroke()

    let font = NSFont.systemFont(ofSize: s * 0.42, weight: .bold)
    let text = NSAttributedString(string: "❯", attributes: [
        .font: font, .foregroundColor: NSColor(srgbRed: 0xE9 / 255, green: 0xD5 / 255, blue: 0xFF / 255, alpha: 1)])
    let size = text.size()
    text.draw(at: NSPoint(x: tile.midX - size.width / 2, y: tile.midY - size.height / 2))
    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

for base in [16, 32, 128, 256, 512] {
    try render(base).write(to: iconset.appendingPathComponent("icon_\(base)x\(base).png"))
    try render(base * 2).write(to: iconset.appendingPathComponent("icon_\(base)x\(base)@2x.png"))
}

let out = root.appendingPathComponent("Resources/Slate.icns")
let p = Process()
p.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
p.arguments = ["-c", "icns", iconset.path, "-o", out.path]
try p.run()
p.waitUntilExit()
print(p.terminationStatus == 0 ? "Wrote \(out.path)" : "iconutil failed")
