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

    /// Slate has no menu bar, and ⌘V / ⌘C / ⌘X / ⌘A / ⌘Z normally reach the text field through the
    /// app's Edit menu. Without one they'd do nothing, so route them to the text field directly.
    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        let flags = event.modifierFlags.intersection([.command, .shift, .option, .control])
        let action: Selector?
        switch (flags, event.charactersIgnoringModifiers?.lowercased()) {
        case (.command, "v"): action = #selector(NSText.paste(_:))
        case (.command, "c"): action = #selector(NSText.copy(_:))
        case (.command, "x"): action = #selector(NSText.cut(_:))
        case (.command, "a"): action = #selector(NSText.selectAll(_:))
        case (.command, "z"): action = Selector(("undo:"))
        case ([.command, .shift], "z"): action = Selector(("redo:"))
        default: action = nil
        }
        if event.type == .keyDown, let action, let responder = firstResponder, responder.tryToPerform(action, with: self) {
            return true
        }
        return super.performKeyEquivalent(with: event)
    }
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
    /// Thin strip along the bottom edge for in-place commands: a fill (0...1) or a sliding segment (nil).
    /// Theme decoration ("vines"), clipped to the bar, between the background and the text.
    let decor = CALayer()
    let decorBranch = CAShapeLayer()
    let decorLeavesA = CAShapeLayer()
    let decorLeavesB = CAShapeLayer()
    var decoration = ""
    let progressClip = CALayer()
    let progressFill = CALayer()
    private(set) var progressValue: Double?
    private(set) var progressShown = false

    let prompt = NSTextField(labelWithString: "❯")
    let input = InputField()
    let ghost = NSTextField(labelWithString: "")
    /// Messages shown in the bar when system notifications aren't allowed.
    let message = NSTextField(labelWithString: "")
    /// Where commands run: a small "folder" chip just left of the hotkey hint.
    let folderBox = NSView()
    let folder = NSTextField(labelWithString: "")
    let hintBox = NSView()
    let hint = NSTextField(labelWithString: "")
    /// Tiny version tag in the bottom-right corner, under the hotkey hint.
    let version = NSTextField(labelWithString: "")

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
        decor.masksToBounds = true
        for shape in [decorBranch, decorLeavesA, decorLeavesB] { decor.addSublayer(shape) }
        decorBranch.fillColor = nil
        decorBranch.lineWidth = 1.8
        decorBranch.lineCap = .round
        decorBranch.lineJoin = .round
        base.addSublayer(decor)
        base.addSublayer(flash)
        progressClip.masksToBounds = true
        progressClip.addSublayer(progressFill)
        progressClip.isHidden = true
        base.addSublayer(progressClip)
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
        for label in [prompt, ghost, hint, message, version] {
            label.isSelectable = false
            label.drawsBackground = false
            label.isBordered = false
        }
        prompt.wantsLayer = true
        version.wantsLayer = true
        version.layer?.shadowOffset = .zero
        version.layer?.shadowRadius = 3
        version.layer?.shadowOpacity = 0.9

        hintBox.wantsLayer = true
        hintBox.layer?.cornerRadius = 7
        hintBox.layer?.borderWidth = 1
        hintBox.addSubview(hint)

        folderBox.wantsLayer = true
        folderBox.layer?.cornerRadius = 7
        folder.isSelectable = false
        folder.drawsBackground = false
        folder.isBordered = false
        folder.lineBreakMode = .byTruncatingHead
        folderBox.addSubview(folder)

        addSubview(prompt)
        addSubview(ghost)
        addSubview(input)
        addSubview(message)
        addSubview(hintBox)
        addSubview(folderBox)
        addSubview(version)
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
        // The strip sits just inside the bottom edge, inset past the rounded corners.
        progressClip.frame = CGRect(x: bar.minX + r * 0.6, y: bar.minY + t + 1, width: bar.width - r * 1.2, height: 2)
        progressClip.cornerRadius = 1
        layoutDecoration(in: bar.insetBy(dx: t, dy: t), radius: max(0, r - t))
        layoutProgressFill()
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
        if !folderBox.isHidden {
            let f = folder.fittingSize
            let w = min(f.width, 200) // long paths are cut from the left: "…/code/slate"
            let box = NSSize(width: w + 16, height: f.height + 8)
            folderBox.frame = NSRect(x: right - box.width, y: (bar.midY - box.height / 2).rounded(), width: box.width, height: box.height)
            folder.frame = NSRect(x: 8, y: 4, width: w, height: f.height)
            right = folderBox.frame.minX - 12
        }

        // Version tag tucked into the bar's bottom-right corner, clear of the rounded edge.
        let v = version.fittingSize
        let inset = max(10, min(cornerRadius, bar.height / 2) * 0.75)
        version.frame = NSRect(x: (bar.maxX - inset - v.width).rounded(), y: (bar.minY + 3).rounded(), width: v.width, height: v.height)

        let x = prompt.frame.maxX + 14
        let h = input.fittingSize.height
        input.frame = NSRect(x: x, y: (bar.midY - h / 2).rounded(), width: max(40, right - x), height: h)
        let mh = message.fittingSize.height
        message.frame = NSRect(x: x, y: (bar.midY - mh / 2).rounded(), width: max(40, right - x), height: mh)
        layoutGhost()
    }

    private func layoutDecoration(in rect: CGRect, radius: CGFloat) {
        decor.frame = rect
        decor.cornerRadius = radius
        decor.isHidden = decoration != "vines"
        guard !decor.isHidden else { return }
        let h = rect.height
        let branch = CGMutablePath(), leavesA = CGMutablePath(), leavesB = CGMutablePath()
        let flip = { (p: CGPoint) in CGPoint(x: p.x, y: h - p.y) } // geometry is y-down; layers are y-up
        for vine in Decoration.vines(width: rect.width, height: h, radius: radius) {
            branch.addLines(between: vine.branch.map(flip))
            for (i, leaf) in vine.leaves.enumerated() {
                (i % 2 == 0 ? leavesA : leavesB).addLines(between: leaf.map(flip))
                (i % 2 == 0 ? leavesA : leavesB).closeSubpath()
            }
        }
        for shape in [decorBranch, decorLeavesA, decorLeavesB] { shape.frame = decor.bounds }
        decorBranch.path = branch
        decorLeavesA.path = leavesA
        decorLeavesB.path = leavesB
    }

    /// nil = indeterminate (a segment slides back and forth); 0...1 = filled up to that point.
    func setProgress(_ value: Double?, shown: Bool) {
        progressShown = shown
        progressValue = value
        progressClip.isHidden = !shown
        guard shown else {
            progressFill.removeAllAnimations()
            return
        }
        CATransaction.begin()
        CATransaction.setAnimationDuration(0.2)
        layoutProgressFill()
        CATransaction.commit()
    }

    private func layoutProgressFill() {
        let w = progressClip.bounds.width, h = progressClip.bounds.height
        if let value = progressValue {
            progressFill.removeAnimation(forKey: "slide")
            progressFill.frame = CGRect(x: 0, y: 0, width: w * CGFloat(min(1, max(0, value))), height: h)
        } else {
            progressFill.frame = CGRect(x: 0, y: 0, width: w * 0.25, height: h)
            guard progressShown, progressFill.animation(forKey: "slide") == nil else { return }
            let slide = CABasicAnimation(keyPath: "position.x")
            slide.fromValue = w * 0.125
            slide.toValue = w * 0.875
            slide.duration = 0.9
            slide.autoreverses = true
            slide.repeatCount = .infinity
            slide.timingFunction = CAMediaTimingFunction(name: .easeInEaseOut)
            progressFill.add(slide, forKey: "slide")
        }
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
