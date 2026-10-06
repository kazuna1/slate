import AppKit

/// The theme gallery: every theme rendered as a real Slate bar, two per row. Clicking one picks it.
/// Shown on first launch, from the menu (Themes → Theme Gallery…) and with ":themes".
/// Mirrors windows/src/Slate/ThemePicker.cs.
final class ThemePicker: NSObject, NSWindowDelegate {
    private static var current: ThemePicker?

    private let window: NSWindow
    private var onPick: ((Theme?) -> Void)?

    /// Opens the gallery (or brings it forward). `onPick` gets the chosen theme, or nil if closed without one.
    static func show(config: SlateConfig, hotkeyDisplay: String, welcome: Bool, onPick: @escaping (Theme?) -> Void) {
        if let open = current {
            open.window.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            return
        }
        let picker = ThemePicker(config: config, hotkeyDisplay: hotkeyDisplay, welcome: welcome)
        picker.onPick = onPick
        current = picker
        picker.window.center()
        picker.window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true) // a menu-bar app has to come forward for its window
    }

    /// Draws the gallery off-screen to a PNG (Slate --render-gallery out.png), for docs and testing.
    static func render(config: SlateConfig, hotkeyDisplay: String, to path: String) throws {
        let picker = ThemePicker(config: config, hotkeyDisplay: hotkeyDisplay, welcome: true)
        guard let view = picker.window.contentView else { throw SlateError("No content.") }
        view.layoutSubtreeIfNeeded()
        let bounds = view.bounds
        let scale: CGFloat = 2
        guard let ctx = CGContext(data: nil, width: Int(bounds.width * scale), height: Int(bounds.height * scale),
                                  bitsPerComponent: 8, bytesPerRow: 0, space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { throw SlateError("No bitmap.") }
        ctx.scaleBy(x: scale, y: scale)
        ctx.setFillColor(picker.window.backgroundColor.cgColor)
        ctx.fill(bounds)
        let rep = view.bitmapImageRepForCachingDisplay(in: bounds)!
        view.cacheDisplay(in: bounds, to: rep)
        ctx.draw(rep.cgImage!, in: bounds)
        guard let image = ctx.makeImage(),
              let data = NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:]) else { throw SlateError("No PNG.") }
        try data.write(to: URL(fileURLWithPath: path))
        picker.onPick = nil
    }

    private init(config: SlateConfig, hotkeyDisplay: String, welcome: Bool) {
        let cardWidth: CGFloat = 400, gap: CGFloat = 18, pad: CGFloat = 28
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: cardWidth * 2 + gap + pad * 2, height: 500),
                          styleMask: [.titled, .closable, .fullSizeContentView], backing: .buffered, defer: false)
        super.init()
        window.title = welcome ? "Welcome to Slate" : "Themes"
        window.titlebarAppearsTransparent = true
        window.appearance = NSAppearance(named: .darkAqua)
        window.backgroundColor = NSColor(srgbRed: 0x0C / 255, green: 0x0A / 255, blue: 0x14 / 255, alpha: 1)
        window.isReleasedWhenClosed = false
        window.delegate = self

        let title = NSTextField(labelWithString: welcome ? "Choose your look" : "Pick a theme")
        title.font = .systemFont(ofSize: 24, weight: .bold)
        title.textColor = .white
        let subtitle = NSTextField(labelWithString: welcome
            ? "Press \(hotkeyDisplay) anywhere to open Slate. You can change the theme any time from the ❯ menu."
            : "Click a bar to use it.")
        subtitle.font = .systemFont(ofSize: 13)
        subtitle.textColor = NSColor(white: 1, alpha: 0.6)

        // Each card is a real bar with that theme, rendered off-screen.
        let grid = NSGridView()
        grid.rowSpacing = gap
        grid.columnSpacing = gap
        var row: [NSView] = []
        for theme in Themes.all {
            var themed = config
            theme.apply(&themed.appearance)
            let preview = Bar(config: themed, history: History(maxSize: 1), hotkeyDisplay: hotkeyDisplay)
            guard let image = try? preview.renderImage(text: "") else { continue }
            let card = ThemeCard(theme: theme, image: image, width: cardWidth, selected: theme.name == config.theme)
            card.onClick = { [weak self] in self?.pick(theme) }
            row.append(card)
            if row.count == 2 { grid.addRow(with: row); row = [] }
        }
        if !row.isEmpty { grid.addRow(with: row + [NSGridCell.emptyContentView]) }

        let stack = NSStackView(views: [title, subtitle, grid])
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 6
        stack.setCustomSpacing(22, after: subtitle)
        stack.edgeInsets = NSEdgeInsets(top: pad + 18, left: pad, bottom: pad, right: pad)
        stack.translatesAutoresizingMaskIntoConstraints = false

        let content = NSView()
        content.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: content.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: content.trailingAnchor),
            stack.topAnchor.constraint(equalTo: content.topAnchor),
            stack.bottomAnchor.constraint(equalTo: content.bottomAnchor),
        ])
        window.contentView = content
        window.setContentSize(stack.fittingSize)
    }

    private func pick(_ theme: Theme) {
        let callback = onPick
        onPick = nil
        window.close()
        callback?(theme)
    }

    func windowWillClose(_ notification: Notification) {
        let callback = onPick
        onPick = nil
        ThemePicker.current = nil
        callback?(nil) // closed without choosing
    }
}

/// One theme in the gallery: its bar preview and name, with a highlight on hover and a check if current.
private final class ThemeCard: NSView {
    var onClick: (() -> Void)?
    private let selected: Bool
    private var hovering = false { didSet { needsDisplay = true } }

    init(theme: Theme, image: CGImage, width: CGFloat, selected: Bool) {
        self.selected = selected
        // The render includes the whole glow margin (40 pt a side at 2x); keep only a little of it.
        let cut = 52
        let cropped = image.cropping(to: CGRect(x: cut, y: cut, width: image.width - cut * 2, height: image.height - cut * 2)) ?? image
        let pad: CGFloat = 8
        let imageWidth = width - pad * 2
        let imageHeight = (imageWidth * CGFloat(cropped.height) / CGFloat(cropped.width)).rounded()
        let height = imageHeight + 34 + pad
        super.init(frame: NSRect(x: 0, y: 0, width: width, height: height))

        let picture = NSImageView(frame: NSRect(x: pad, y: 34, width: imageWidth, height: imageHeight))
        picture.image = NSImage(cgImage: cropped, size: NSSize(width: imageWidth, height: imageHeight))
        picture.imageScaling = .scaleProportionallyUpOrDown
        addSubview(picture)

        let name = NSTextField(labelWithString: theme.name + (selected ? "  ✓" : ""))
        name.font = .systemFont(ofSize: 14, weight: .semibold)
        name.textColor = NSColor(white: 1, alpha: 0.85)
        name.frame = NSRect(x: pad + 6, y: 8, width: width - pad * 2 - 12, height: 20)
        addSubview(name)

        addTrackingArea(NSTrackingArea(rect: bounds, options: [.mouseEnteredAndExited, .activeInKeyWindow, .inVisibleRect],
                                       owner: self, userInfo: nil))
        widthAnchor.constraint(equalToConstant: width).isActive = true
        heightAnchor.constraint(equalToConstant: height).isActive = true
    }

    required init?(coder: NSCoder) { fatalError() }

    override func draw(_ dirtyRect: NSRect) {
        let card = NSBezierPath(roundedRect: bounds.insetBy(dx: 1, dy: 1), xRadius: 14, yRadius: 14)
        if hovering {
            NSColor(white: 1, alpha: 0.06).setFill()
            card.fill()
        }
        let border: CGFloat = hovering ? 0.55 : (selected ? 0.3 : 0)
        guard border > 0 else { return }
        NSColor(white: 1, alpha: border).setStroke()
        card.lineWidth = 2
        card.stroke()
    }

    override func mouseEntered(with event: NSEvent) {
        hovering = true
        NSCursor.pointingHand.set()
    }

    override func mouseExited(with event: NSEvent) {
        hovering = false
        NSCursor.arrow.set()
    }

    override func mouseUp(with event: NSEvent) {
        if bounds.contains(convert(event.locationInWindow, from: nil)) { onClick?() }
    }

    override func mouseDown(with event: NSEvent) {} // keep the click for mouseUp
}
