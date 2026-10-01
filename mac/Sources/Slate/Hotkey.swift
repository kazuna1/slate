import Carbon.HIToolbox

/// A parsed hotkey such as "Option+Space" or "Cmd+Shift+K".
struct Hotkey: Equatable {
    static let defaultText = "Option+Space"

    let keyCode: UInt32
    let modifiers: UInt32 // Carbon modifier flags
    let display: String   // "⌥ Space"

    static func parse(_ text: String) throws -> Hotkey {
        var mods: UInt32 = 0
        var key: (code: Int, name: String)?

        for raw in text.split(separator: "+") {
            let part = raw.trimmingCharacters(in: .whitespaces)
            switch part.lowercased() {
            case "cmd", "command", "⌘": mods |= UInt32(cmdKey)
            case "option", "opt", "alt", "⌥": mods |= UInt32(optionKey)
            case "ctrl", "control", "⌃": mods |= UInt32(controlKey)
            case "shift", "⇧": mods |= UInt32(shiftKey)
            case "": continue
            default:
                guard key == nil else { throw SlateError("Hotkey \"\(text)\" has more than one non-modifier key.") }
                guard let code = keyCodes[part.lowercased()] else { throw SlateError("Unknown key \"\(part)\" in hotkey \"\(text)\".") }
                key = (code, part.count == 1 ? part.uppercased() : part.prefix(1).uppercased() + part.dropFirst().lowercased())
            }
        }

        guard let key else { throw SlateError("Hotkey \"\(text)\" has no key, e.g. \"Option+Space\".") }
        guard mods & UInt32(cmdKey | optionKey | controlKey) != 0 else {
            throw SlateError("Hotkey \"\(text)\" needs Cmd, Option or Ctrl.")
        }

        var symbols = ""
        if mods & UInt32(controlKey) != 0 { symbols += "⌃" }
        if mods & UInt32(optionKey) != 0 { symbols += "⌥" }
        if mods & UInt32(shiftKey) != 0 { symbols += "⇧" }
        if mods & UInt32(cmdKey) != 0 { symbols += "⌘" }
        return Hotkey(keyCode: UInt32(key.code), modifiers: mods, display: "\(symbols) \(key.name)")
    }

    private static let keyCodes: [String: Int] = {
        var map: [String: Int] = [
            "space": kVK_Space, "return": kVK_Return, "enter": kVK_Return, "tab": kVK_Tab,
            "escape": kVK_Escape, "esc": kVK_Escape, "`": kVK_ANSI_Grave, "grave": kVK_ANSI_Grave,
            "backtick": kVK_ANSI_Grave, "-": kVK_ANSI_Minus, "=": kVK_ANSI_Equal, ",": kVK_ANSI_Comma,
            ".": kVK_ANSI_Period, "/": kVK_ANSI_Slash, ";": kVK_ANSI_Semicolon, "'": kVK_ANSI_Quote,
            "[": kVK_ANSI_LeftBracket, "]": kVK_ANSI_RightBracket, "\\": kVK_ANSI_Backslash,
        ]
        let letters: [Int] = [kVK_ANSI_A, kVK_ANSI_B, kVK_ANSI_C, kVK_ANSI_D, kVK_ANSI_E, kVK_ANSI_F, kVK_ANSI_G,
                              kVK_ANSI_H, kVK_ANSI_I, kVK_ANSI_J, kVK_ANSI_K, kVK_ANSI_L, kVK_ANSI_M, kVK_ANSI_N,
                              kVK_ANSI_O, kVK_ANSI_P, kVK_ANSI_Q, kVK_ANSI_R, kVK_ANSI_S, kVK_ANSI_T, kVK_ANSI_U,
                              kVK_ANSI_V, kVK_ANSI_W, kVK_ANSI_X, kVK_ANSI_Y, kVK_ANSI_Z]
        for (i, code) in letters.enumerated() { map[String(UnicodeScalar(UInt8(97 + i)))] = code }
        let digits: [Int] = [kVK_ANSI_0, kVK_ANSI_1, kVK_ANSI_2, kVK_ANSI_3, kVK_ANSI_4,
                             kVK_ANSI_5, kVK_ANSI_6, kVK_ANSI_7, kVK_ANSI_8, kVK_ANSI_9]
        for (i, code) in digits.enumerated() { map["\(i)"] = code }
        let fkeys: [Int] = [kVK_F1, kVK_F2, kVK_F3, kVK_F4, kVK_F5, kVK_F6,
                            kVK_F7, kVK_F8, kVK_F9, kVK_F10, kVK_F11, kVK_F12]
        for (i, code) in fkeys.enumerated() { map["f\(i + 1)"] = code }
        return map
    }()
}

/// Global hotkey through Carbon's RegisterEventHotKey: works without the Accessibility permission.
final class HotkeyManager {
    var onPress: (() -> Void)?
    private var hotKeyRef: EventHotKeyRef?
    private var handlerRef: EventHandlerRef?

    init() {
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, _, userData in
            guard let userData else { return noErr }
            Unmanaged<HotkeyManager>.fromOpaque(userData).takeUnretainedValue().onPress?()
            return noErr
        }, 1, &spec, Unmanaged.passUnretained(self).toOpaque(), &handlerRef)
    }

    /// False if another app already owns this combination.
    @discardableResult
    func register(_ hotkey: Hotkey) -> Bool {
        unregister()
        let id = EventHotKeyID(signature: OSType(0x534C_5445), id: 1) // 'SLTE'
        return RegisterEventHotKey(hotkey.keyCode, hotkey.modifiers, id, GetApplicationEventTarget(), 0, &hotKeyRef) == noErr
    }

    func unregister() {
        if let hotKeyRef { UnregisterEventHotKey(hotKeyRef) }
        hotKeyRef = nil
    }
}
