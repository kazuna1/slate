import AppKit
import QuartzCore

/// Borderless panel that can take keyboard focus without activating Slate,
/// so the app you were in stays active (like Spotlight).
final class BarPanel: NSPanel {
    init() {
        super.init(contentRect: NSRect(x: 0, y: 0, width: 800, height: 140),
                   styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        isOpaque = false
        backgroundColor = .clear
        hasShadow = false
        hidesOnDeactivate = false
        isMovableByWindowBackground = true
        isReleasedWhenClosed = false
        // On every Space, untouched by Mission Control / Show Desktop, never in the window cycle.
        collectionBehavior = [.canJoinAllSpaces, .stationary, .ignoresCycle, .fullScreenAuxiliary]
    }

    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }
}

final class InputField: NSTextField {
    var onFocus: (() -> Void)?

    override func becomeFirstResponder() -> Bool {
        let ok = super.becomeFirstResponder()
        if ok { onFocus?() }
        return ok
    }
}

/// Draws the bar. Layers (back to front): glow, border (gradient + moving sheen), background, flash.
/// The border is the strip of the border layer left visible around the inset background,
/// so it needs no mask and renders everywhere (including --render-preview).
final class BarView: NSView {
    let backdrop = NSView()
    let glow = CALayer()
    let border = CALayer()
    let sheen = CAGradientLayer()
    let background = CAGradientLayer()
    let flash = CALayer()

    let prompt = NSTextField(labelWithString: "❯")
    let input = InputField()
    let ghost = NSTextField(labelWithString: "")
    /// Messages shown in the bar when system notifications aren't allowed.
    let message = NSTextField(labelWithString: "")
    let hintBox = NSView()
    let hint = NSTextField(labelWithString: "")

    var margin: CGFloat = 40
    var cornerRadius: CGFloat = 18
    var borderThickness: CGFloat = 1.5
    var barRect: NSRect { bounds.insetBy(dx: margin, dy: margin) }

    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true

        backdrop.wantsLayer = true
        let base = backdrop.layer!
        base.addSublayer(glow)
        base.addSublayer(border)
        border.addSublayer(sheen)
        border.masksToBounds = true
        base.addSublayer(background)
        background.masksToBounds = true
        base.addSublayer(flash)
        flash.opacity = 0
        glow.shadowOffset = .zero
        sheen.startPoint = CGPoint(x: 0, y: 0.5)
        sheen.endPoint = CGPoint(x: 1, y: 0.5)
        addSubview(backdrop)

        input.isBordered = false
        input.isBezeled = false
        input.drawsBackground = false
        input.focusRingType = .none
        input.usesSingleLineMode = true
        input.cell?.wraps = false
        input.cell?.isScrollable = true
        input.lineBreakMode = .byClipping

        message.isHidden = true
        message.lineBreakMode = .byTruncatingTail
        for label in [prompt, ghost, hint, message] {
            label.isSelectable = false
            label.drawsBackground = false
            label.isBordered = false
        }
        prompt.wantsLayer = true

        hintBox.wantsLayer = true
        hintBox.layer?.cornerRadius = 7
        hintBox.layer?.borderWidth = 1
        hintBox.addSubview(hint)

        addSubview(prompt)
        addSubview(ghost)
        addSubview(input)
        addSubview(message)
        addSubview(hintBox)
    }

    required init?(coder: NSCoder) { fatalError() }

    override func layout() {
        super.layout()
        backdrop.frame = bounds
        let bar = barRect
        let r = min(cornerRadius, bar.height / 2)
        let t = borderThickness

        CATransaction.begin()
        CATransaction.setDisableActions(true)
        glow.frame = bar
        glow.cornerRadius = r
        glow.shadowPath = CGPath(roundedRect: CGRect(origin: .zero, size: bar.size), cornerWidth: r, cornerHeight: r, transform: nil)
        border.frame = bar
        border.cornerRadius = r
        sheen.frame = CGRect(x: -bar.width * 0.3, y: 0, width: bar.width * 0.3, height: bar.height)
        background.frame = bar.insetBy(dx: t, dy: t)
        background.cornerRadius = max(0, r - t)
        flash.frame = bar
        flash.cornerRadius = r
        CATransaction.commit()

        let p = prompt.fittingSize
        prompt.frame = NSRect(x: bar.minX + 22, y: (bar.midY - p.height / 2).rounded(), width: p.width, height: p.height)

        var right = bar.maxX - 14
        if !hintBox.isHidden {
            let h = hint.fittingSize
            let box = NSSize(width: h.width + 18, height: h.height + 8)
            hintBox.frame = NSRect(x: right - box.width, y: (bar.midY - box.height / 2).rounded(), width: box.width, height: box.height)
            hint.frame = NSRect(x: 9, y: 4, width: h.width, height: h.height)
            right = hintBox.frame.minX - 12
        }

        let x = prompt.frame.maxX + 14
        let h = input.fittingSize.height
        input.frame = NSRect(x: x, y: (bar.midY - h / 2).rounded(), width: max(40, right - x), height: h)
        let mh = message.fittingSize.height
        message.frame = NSRect(x: x, y: (bar.midY - mh / 2).rounded(), width: max(40, right - x), height: mh)
        layoutGhost()
    }

    /// The ghost (completion preview) sits right after the typed text.
    func layoutGhost() {
        guard let font = input.font, !ghost.stringValue.isEmpty else { return }
        let typed = (input.stringValue as NSString).size(withAttributes: [.font: font]).width
        let g = ghost.fittingSize
        let x = input.frame.minX + 2 + typed
        ghost.isHidden = x + g.width > input.frame.maxX
        ghost.frame = NSRect(x: x, y: input.frame.minY + (input.frame.height - g.height) / 2, width: g.width, height: g.height)
    }
}
