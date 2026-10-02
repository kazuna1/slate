import AppKit
import QuartzCore

enum DismissReason { case escape, clickAway, ran, hotkey, focusFailed }

/// The bar. Two states:
///  - idle: just above the desktop icons, on every Space, never steals focus;
///  - active: floating above all windows with keyboard focus in the input.
final class Bar: NSObject, NSWindowDelegate, NSTextFieldDelegate {
    static let idleLevel = NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.desktopIconWindow)) + 1)
    private static let violet = NSColor(srgbRed: 0x8B / 255, green: 0x5C / 255, blue: 0xF6 / 255, alpha: 1)
    private static let idleGlow: Float = 0.55
    private static let maxFocusAttempts = 5

    let panel = BarPanel()
    let view = BarView()
    private(set) var config: SlateConfig
    private let history: History
    private let projects = Projects()
    private lazy var completer = Completer(projects: projects)

    var onBuiltin: ((String) -> Void)?
    var notify: ((String, String) -> Void)?
    var onMoved: ((SlateConfig) -> Void)?

    private(set) var isActive = false
    private var summonedAt = Date.distantPast
    private var focusAttempts = 0
    private var previousApp: NSRunningApplication?
    private var positioning = false
    private var saveMove: DispatchWorkItem?

    // Completion: Tab / Shift+Tab cycle through matches for the last word; the ghost previews the first.
    private var cycle: [String] = []
    private var cycleIndex = -1
    private var tokenStart = 0
    private var completing = false
    private var ghostMatch: String?

    // In-bar messages (fallback for notifications).
    private var messageAction: (() -> Void)?
    private var messageHide: DispatchWorkItem?
    private var pendingMessage: (String, String, (() -> Void)?)?

    init(config: SlateConfig, history: History, hotkeyDisplay: String) {
        self.config = config
        self.history = history
        super.init()

        panel.contentView = view
        panel.delegate = self
        panel.level = Self.idleLevel
        view.input.delegate = self
        view.input.onFocus = { [weak self] in self?.styleFieldEditor() }

        NotificationCenter.default.addObserver(forName: NSApplication.didChangeScreenParametersNotification,
                                               object: nil, queue: .main) { [weak self] _ in self?.position() }

        apply(config, hotkeyDisplay: hotkeyDisplay)
        completer.refresh(config)
    }

    func show() {
        panel.orderFrontRegardless()
        panel.alphaValue = CGFloat(config.appearance.idleOpacity)
    }

    // MARK: Summon / dismiss

    func toggle() {
        if isActive { dismiss(.hotkey) } else { summon() }
    }

    func summon() {
        guard !isActive else { return }
        hideMessage()
        isActive = true
        summonedAt = Date()
        focusAttempts = 0
        previousApp = NSWorkspace.shared.frontmostApplication
        completer.refresh(config)

        panel.level = .floating
        panel.orderFrontRegardless()
        takeFocus()
        animateState(active: true, pop: true)

        // Never trust that it worked: check once the event loop has caught up.
        DispatchQueue.main.async { self.verifyFocus() }
    }

    private func takeFocus() {
        panel.makeKey()
        panel.makeFirstResponder(view.input)
        moveCaretToEnd()
    }

    private func verifyFocus() {
        guard isActive else { return }
        if panel.isKeyWindow, view.input.currentEditor() != nil { return }

        focusAttempts += 1
        if focusAttempts > Self.maxFocusAttempts {
            // Better to back off visibly than to look focused while keys go elsewhere.
            Log.write("Focus failed; frontmost is \(NSWorkspace.shared.frontmostApplication?.localizedName ?? "?")")
            dismiss(.focusFailed)
            notify?("Slate", "Couldn't take keyboard focus. Press the hotkey again.")
            return
        }

        // Fallback: activate Slate itself, then retry.
        NSApp.activate(ignoringOtherApps: true)
        takeFocus()
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.04) { self.verifyFocus() }
    }

    func dismiss(_ reason: DismissReason) {
        guard isActive else { return }
        isActive = false
        history.resetNavigation()
        if reason == .escape { setInput("") }

        // Drop back to the desktop layer. Re-ordering also gives up key status.
        panel.orderOut(nil)
        panel.level = Self.idleLevel
        panel.orderFrontRegardless()

        // Hand focus back where it was, except after running a command (the terminal takes it).
        if reason != .ran && reason != .clickAway, let app = previousApp, app != .current {
            app.activate()
        }
        animateState(active: false, pop: false)

        if let (title, body, action) = pendingMessage {
            pendingMessage = nil
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { self.showMessage(title, body, action: action) }
        }
    }

    // MARK: Messages

    /// Raises the bar (without taking focus) and shows a message in place of the input for a few seconds.
    /// With an action, clicking the bar runs it.
    func showMessage(_ title: String, _ body: String, action: (() -> Void)? = nil) {
        guard !isActive else {
            pendingMessage = (title, body, action) // show once the user is done typing
            return
        }
        let a = config.appearance
        let font = NSFont.firstAvailable(a.fontFamily, size: CGFloat(max(8, a.fontSize * 0.8)))
        let text = NSMutableAttributedString(string: title, attributes: [
            .font: NSFontManager.shared.convert(font, toHaveTrait: .boldFontMask),
            .foregroundColor: NSColor.hex(a.textColor, fallback: .white)])
        text.append(NSAttributedString(string: "   " + body, attributes: [
            .font: font, .foregroundColor: NSColor.hex(a.placeholderColor, fallback: .gray)]))
        view.message.attributedStringValue = text
        view.message.isHidden = false
        view.input.isHidden = true
        view.ghost.isHidden = true
        view.needsLayout = true
        messageAction = action

        panel.level = .floating
        panel.orderFrontRegardless()
        NSAnimationContext.runAnimationGroup { $0.duration = 0.2; panel.animator().alphaValue = 1 }

        messageHide?.cancel()
        let work = DispatchWorkItem { [weak self] in self?.hideMessage() }
        messageHide = work
        DispatchQueue.main.asyncAfter(deadline: .now() + (action == nil ? 4.5 : 10), execute: work)
    }

    private func hideMessage() {
        guard !view.message.isHidden else { return }
        messageHide?.cancel()
        messageAction = nil
        view.message.isHidden = true
        view.input.isHidden = false
        updateGhost()
        if !isActive {
            panel.level = Self.idleLevel
            panel.orderFrontRegardless()
            animateState(active: false, pop: false)
        }
    }

    func windowDidBecomeKey(_ notification: Notification) {
        guard !isActive else { return }
        if let action = messageAction, NSApp.currentEvent?.type == .leftMouseDown {
            hideMessage() // clicked a message: do its action instead of summoning
            DispatchQueue.main.async { self.panel.orderOut(nil); self.panel.orderFrontRegardless() }
            action()
            return
        }
        if NSApp.currentEvent?.type == .leftMouseDown {
            summon() // clicked the idle bar
        } else {
            // macOS made us key on its own (e.g. at launch): don't sit there looking focused.
            DispatchQueue.main.async { self.panel.orderOut(nil); self.panel.orderFrontRegardless() }
        }
    }

    func windowDidResignKey(_ notification: Notification) {
        guard isActive, Date().timeIntervalSince(summonedAt) > 0.2 else { return }
        DispatchQueue.main.async {
            if self.isActive && !self.panel.isKeyWindow { self.dismiss(.clickAway) }
        }
    }

    // MARK: Position / drag

    func position() {
        let a = config.appearance
        let screens = NSScreen.screens
        guard let screen = (a.monitor >= 1 && a.monitor <= screens.count) ? screens[a.monitor - 1] : screens.first
        else { return }
        let vf = screen.visibleFrame
        let m = view.margin
        let w = CGFloat(max(200, a.width)), h = CGFloat(max(28, a.height))

        let x = vf.midX - w / 2 + CGFloat(a.offsetX)
        let y: CGFloat
        switch a.anchor.lowercased() {
        case "bottom": y = vf.minY + CGFloat(a.offsetY)
        case "center": y = vf.midY - h / 2 - CGFloat(a.offsetY)
        default: y = vf.maxY - CGFloat(a.offsetY) - h
        }

        positioning = true
        panel.setFrame(NSRect(x: x - m, y: y - m, width: w + 2 * m, height: h + 2 * m), display: true)
        positioning = false
    }

    func windowDidMove(_ notification: Notification) {
        guard !positioning else { return }
        saveMove?.cancel()
        let work = DispatchWorkItem { [weak self] in self?.savePosition() }
        saveMove = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.4, execute: work)
    }

    /// Converts the dragged window position back into display + anchor offsets.
    private func savePosition() {
        guard let screen = panel.screen else { return }
        let vf = screen.visibleFrame
        let bar = panel.frame.insetBy(dx: view.margin, dy: view.margin)
        var a = config.appearance
        a.monitor = screen == NSScreen.screens.first ? 0 : (NSScreen.screens.firstIndex(of: screen) ?? 0) + 1
        a.offsetX = (bar.minX - (vf.midX - bar.width / 2)).rounded()
        switch a.anchor.lowercased() {
        case "bottom": a.offsetY = (bar.minY - vf.minY).rounded()
        case "center": a.offsetY = (vf.midY - bar.height / 2 - bar.minY).rounded()
        default: a.offsetY = (vf.maxY - bar.maxY).rounded()
        }
        config.appearance = a
        onMoved?(config)
    }

    // MARK: Input

    func control(_ control: NSControl, textView: NSTextView, doCommandBy selector: Selector) -> Bool {
        switch selector {
        case #selector(NSResponder.insertNewline(_:)):
            submit()
        case #selector(NSResponder.cancelOperation(_:)):
            dismiss(.escape)
        case #selector(NSResponder.moveUp(_:)):
            if let text = history.previous(current: view.input.stringValue) { setInput(text) }
        case #selector(NSResponder.moveDown(_:)):
            if let text = history.next() { setInput(text) }
        case #selector(NSResponder.insertTab(_:)):
            cycleCompletion(1)
        case #selector(NSResponder.insertBacktab(_:)):
            cycleCompletion(-1)
        case #selector(NSResponder.moveRight(_:)), #selector(NSResponder.moveToEndOfLine(_:)),
             #selector(NSResponder.moveToEndOfDocument(_:)):
            guard ghostMatch != nil, caretAtEnd else { return false }
            acceptGhost()
        default:
            return false
        }
        return true
    }

    func controlTextDidChange(_ obj: Notification) {
        if !completing { cycleIndex = -1 } // typed: start a fresh completion next Tab
        updateGhost()
    }

    private func submit() {
        let text = view.input.stringValue.trimmingCharacters(in: .whitespaces)
        if text.isEmpty {
            dismiss(.escape)
            return
        }
        if text.hasPrefix(":") {
            setInput("")
            dismiss(.ran)
            onBuiltin?(text)
            return
        }

        // Built-in shortcut ("cc slate", "vc new airlink"): find the folder, run the command inside it.
        var command = text
        var folder: String?
        let parts = text.split(separator: " ", maxSplits: 1).map(String.init)
        if let shortcut = config.shortcuts[parts[0]] {
            let query = parts.count > 1 ? parts[1].trimmingCharacters(in: .whitespaces) : ""
            if !query.isEmpty {
                guard let found = projects.resolve(query, config: config) else {
                    notify?("Slate", "No folder matches \"\(query)\". Open it once in a terminal, or add its parent to projectRoots.")
                    return // keep the text so it can be fixed
                }
                projects.visited(found, config: config)
                folder = found
            }
            command = shortcut
        }

        history.add(text)
        do {
            try CommandRunner.run(command, config: config, folder: folder)
        } catch {
            Log.error("Running \"\(text)\"", error)
            notify?("Slate couldn't start the terminal", error.localizedDescription)
            return
        }
        playRunFlash()
        setInput("")
        dismiss(.ran)
    }

    private var caretAtEnd: Bool {
        guard let editor = view.input.currentEditor() else { return true }
        let r = editor.selectedRange
        return r.length == 0 && r.location == (view.input.stringValue as NSString).length
    }

    private func setInput(_ text: String) {
        view.input.stringValue = text
        moveCaretToEnd()
        if !completing { cycleIndex = -1 }
        updateGhost()
    }

    private func moveCaretToEnd() {
        view.input.currentEditor()?.selectedRange = NSRange(location: (view.input.stringValue as NSString).length, length: 0)
    }

    private func styleFieldEditor() {
        guard let editor = view.input.currentEditor() as? NSTextView else { return }
        let a = config.appearance
        editor.insertionPointColor = .hex(a.promptColor, fallback: Self.violet)
        editor.selectedTextAttributes = [.backgroundColor: NSColor.hex(a.glowColor, fallback: Self.violet).withAlphaComponent(0.55)]
    }

    // MARK: Completion

    /// The word being typed, if the caret is at the end and it's an argument (not the command itself).
    private func currentToken() -> (start: Int, token: String)? {
        let text = view.input.stringValue as NSString
        guard caretAtEnd else { return nil }
        let space = text.range(of: " ", options: .backwards)
        guard space.location != NSNotFound else { return nil }
        let start = space.location + 1
        return (start, text.substring(from: start))
    }

    private func cycleCompletion(_ direction: Int) {
        if cycleIndex < 0 {
            guard let (start, token) = currentToken() else { return }
            tokenStart = start
            cycle = completer.match(token.trimmingCharacters(in: CharacterSet(charactersIn: "'")))
            guard !cycle.isEmpty else { return }
            cycleIndex = direction > 0 ? 0 : cycle.count - 1
        } else {
            cycleIndex = (cycleIndex + direction + cycle.count) % cycle.count
        }
        replaceToken(from: tokenStart, with: shellQuote(cycle[cycleIndex]))
    }

    private func acceptGhost() {
        guard let match = ghostMatch, let (start, _) = currentToken() else { return }
        replaceToken(from: start, with: match)
        cycleIndex = -1
        updateGhost()
    }

    private func replaceToken(from start: Int, with value: String) {
        completing = true
        defer { completing = false }
        setInput((view.input.stringValue as NSString).substring(to: start) + value)
    }

    private func updateGhost() {
        ghostMatch = nil
        var suffix = ""
        if cycleIndex < 0, let (_, token) = currentToken(), !token.isEmpty,
           let match = completer.match(token).first,
           match.count > token.count, match.lowercased().hasPrefix(token.lowercased()),
           shellQuote(match) == match { // only names that need no quoting preview as a plain suffix
            ghostMatch = match
            suffix = String(match.dropFirst(token.count))
        }
        view.ghost.stringValue = suffix
        view.ghost.isHidden = suffix.isEmpty
        view.layoutGhost()
    }

    // MARK: Config → visuals

    func apply(_ config: SlateConfig, hotkeyDisplay: String) {
        self.config = config
        let a = config.appearance
        let violet = Self.violet

        let glowSize = CGFloat(max(0, a.glowSize))
        view.margin = glowSize + 10
        view.cornerRadius = CGFloat(max(0, a.cornerRadius))
        view.borderThickness = CGFloat(max(0, a.borderThickness))

        let glowColor = NSColor.hex(a.glowColor, fallback: violet)
        view.glow.backgroundColor = glowColor.cgColor
        view.glow.shadowColor = glowColor.cgColor
        view.glow.shadowRadius = glowSize / 2
        view.glow.shadowOpacity = Float(min(1, max(0, a.glowOpacity)))

        let stops = a.background.isEmpty ? ["#1E0B45"] : a.background
        let colors = stops.map { NSColor.hex($0, fallback: violet).cgColor }
        view.background.colors = colors.count == 1 ? [colors[0], colors[0]] : colors
        // Same convention as WPF: 0° = left → right, angles turn clockwise.
        let rad = CGFloat(a.backgroundAngle) * .pi / 180
        view.background.startPoint = CGPoint(x: 0.5 - cos(rad) / 2, y: 0.5 + sin(rad) / 2)
        view.background.endPoint = CGPoint(x: 0.5 + cos(rad) / 2, y: 0.5 - sin(rad) / 2)

        let highlight = NSColor.hex(a.borderHighlight, fallback: .white)
        view.border.backgroundColor = NSColor.hex(a.borderColor, fallback: violet).cgColor
        view.sheen.colors = [highlight.withAlphaComponent(0).cgColor, highlight.cgColor, highlight.withAlphaComponent(0).cgColor]
        view.flash.backgroundColor = highlight.cgColor

        let font = NSFont.firstAvailable(a.fontFamily, size: CGFloat(max(8, a.fontSize)))
        let promptColor = NSColor.hex(a.promptColor, fallback: violet)
        let placeholderColor = NSColor.hex(a.placeholderColor, fallback: .gray)

        view.prompt.stringValue = a.prompt
        view.prompt.font = font
        view.prompt.textColor = promptColor

        view.input.font = font
        view.input.textColor = .hex(a.textColor, fallback: .white)
        view.input.placeholderAttributedString = NSAttributedString(
            string: a.placeholder, attributes: [.font: font, .foregroundColor: placeholderColor])
        styleFieldEditor()

        view.ghost.font = font
        view.ghost.textColor = placeholderColor

        view.hintBox.isHidden = !a.showHint
        view.hint.stringValue = (a.hintText?.isEmpty == false ? a.hintText! : hotkeyDisplay)
        view.hint.font = .systemFont(ofSize: 12)
        view.hint.textColor = placeholderColor
        view.version.isHidden = !a.showVersion
        view.version.stringValue = "v\(Updater.currentVersion)"
        view.version.font = .systemFont(ofSize: 10.5, weight: .semibold)
        view.version.textColor = promptColor.withAlphaComponent(0.9)
        view.version.layer?.shadowColor = glowColor.cgColor
        view.hintBox.layer?.borderColor = NSColor.hex(a.borderColor, fallback: violet).withAlphaComponent(0.45).cgColor
        view.hintBox.layer?.backgroundColor = glowColor.withAlphaComponent(0.12).cgColor

        position()
        view.needsLayout = true
        view.layoutSubtreeIfNeeded()
        startIdleAnimations()
        animateState(active: isActive, pop: false)
        updateGhost()
    }

    // MARK: Animations

    private func startIdleAnimations() {
        let anim = config.animations
        let peak = Float(min(1, max(0, config.appearance.glowOpacity)))

        // Breathing glow.
        view.glow.removeAnimation(forKey: "pulse")
        if anim.on(anim.glowPulse) {
            let breathe = CABasicAnimation(keyPath: "shadowOpacity")
            breathe.fromValue = peak * 0.5
            breathe.toValue = peak
            breathe.duration = max(0.4, anim.glowPulseSeconds / 2)
            breathe.autoreverses = true
            breathe.repeatCount = .infinity
            breathe.timingFunction = CAMediaTimingFunction(name: .easeInEaseOut)
            view.glow.add(breathe, forKey: "pulse")
        }

        // Highlight sweeping along the border.
        view.sheen.removeAnimation(forKey: "shimmer")
        if anim.on(anim.borderShimmer) {
            let width = view.barRect.width
            let band = width * 0.3
            let sweep = 1.8
            let period = max(sweep, anim.borderShimmerSeconds)
            let move = CAKeyframeAnimation(keyPath: "position.x")
            move.values = [-band / 2, width + band / 2, width + band / 2]
            move.keyTimes = [0, NSNumber(value: sweep / period), 1]
            move.timingFunctions = [CAMediaTimingFunction(name: .easeInEaseOut), CAMediaTimingFunction(name: .linear)]
            move.duration = period
            move.repeatCount = .infinity
            view.sheen.add(move, forKey: "shimmer")
        }
    }

    private func animateState(active: Bool, pop: Bool) {
        let anim = config.animations
        let duration = anim.enabled ? max(0.001, anim.summonDurationMs / 1000) : 0.001
        let targetAlpha = active ? 1 : CGFloat(min(1, max(0.05, config.appearance.idleOpacity)))

        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = duration
            ctx.timingFunction = CAMediaTimingFunction(name: .easeOut)
            panel.animator().alphaValue = targetAlpha
        }

        let glowTarget: Float = active ? 1 : Self.idleGlow
        let fade = CABasicAnimation(keyPath: "opacity")
        fade.fromValue = view.glow.presentation()?.opacity ?? view.glow.opacity
        fade.toValue = glowTarget
        fade.duration = duration
        view.glow.opacity = glowTarget
        view.glow.add(fade, forKey: "fade")

        if pop, anim.on(anim.summonPop), let layer = view.layer {
            // Scale around the centre (the view's layer is anchored at its corner).
            let c = CGPoint(x: view.bounds.midX, y: view.bounds.midY)
            let s: CGFloat = 0.965
            var t = CATransform3DMakeTranslation(-c.x, -c.y, 0)
            t = CATransform3DConcat(t, CATransform3DMakeScale(s, s, 1))
            t = CATransform3DConcat(t, CATransform3DMakeTranslation(c.x, c.y, 0))
            let scale = CASpringAnimation(keyPath: "transform")
            scale.fromValue = t
            scale.toValue = CATransform3DIdentity
            scale.damping = 14
            scale.stiffness = 260
            scale.duration = scale.settlingDuration
            layer.add(scale, forKey: "pop")
        }
    }

    private func playRunFlash() {
        let anim = config.animations
        guard anim.on(anim.runFlash) else { return }

        let flash = CAKeyframeAnimation(keyPath: "opacity")
        flash.values = [0, 0.22, 0]
        flash.keyTimes = [0, 0.16, 1]
        flash.duration = 0.38
        view.flash.add(flash, forKey: "flash")

        let nudge = CAKeyframeAnimation(keyPath: "transform.translation.x")
        nudge.values = [0, 7, 0]
        nudge.keyTimes = [0, 0.28, 1]
        nudge.timingFunctions = [CAMediaTimingFunction(name: .easeOut), CAMediaTimingFunction(name: .easeInEaseOut)]
        nudge.duration = 0.32
        view.prompt.layer?.add(nudge, forKey: "nudge")
    }

    // MARK: Preview

    /// Renders the bar, as it looks when summoned, to a PNG on a dark backdrop.
    /// Slate --render-preview out.png "text"
    func renderPreview(to path: String, text: String) throws {
        view.glow.removeAllAnimations()
        view.sheen.removeAllAnimations()
        view.glow.opacity = 1
        view.sheen.position.x = view.barRect.width * 0.32
        view.input.stringValue = text
        updateGhost()

        // Lay out and draw off-screen.
        panel.setFrameOrigin(NSPoint(x: -20000, y: -20000))
        panel.alphaValue = 1
        panel.orderFrontRegardless()
        panel.makeFirstResponder(nil)
        view.layoutSubtreeIfNeeded()
        view.display()
        CATransaction.flush()

        let scale: CGFloat = 2
        let size = view.bounds.size
        guard let ctx = CGContext(data: nil, width: Int(size.width * scale), height: Int(size.height * scale),
                                  bitsPerComponent: 8, bytesPerRow: 0, space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
            throw SlateError("Couldn't create a bitmap.")
        }
        ctx.scaleBy(x: scale, y: scale)
        ctx.setFillColor(NSColor(srgbRed: 0x0C / 255, green: 0x0A / 255, blue: 0x14 / 255, alpha: 1).cgColor)
        ctx.fill(CGRect(origin: .zero, size: size))
        view.layer?.render(in: ctx)
        panel.orderOut(nil)

        guard let image = ctx.makeImage(),
              let data = NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:]) else {
            throw SlateError("Couldn't encode the PNG.")
        }
        try data.write(to: URL(fileURLWithPath: path))
    }
}
