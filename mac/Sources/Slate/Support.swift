import AppKit

enum Paths {
    static let appSupport: URL = {
        let url = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Slate", isDirectory: true)
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }()

    /// Throwaway .command scripts handed to the terminal.
    static let scripts: URL = {
        let url = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Slate", isDirectory: true)
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }()

    static var config: URL { appSupport.appendingPathComponent("config.json") }
    static var history: URL { appSupport.appendingPathComponent("history.txt") }
    static var log: URL { appSupport.appendingPathComponent("slate.log") }
}

enum Log {
    private static let formatter: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd HH:mm:ss"
        return f
    }()

    static func write(_ line: String) {
        let entry = "[\(formatter.string(from: Date()))] \(line)\n"
        if Bundle.main.bundleIdentifier == nil { FileHandle.standardError.write(Data(entry.utf8)) }
        guard let data = entry.data(using: .utf8) else { return }
        if let handle = try? FileHandle(forWritingTo: Paths.log) {
            handle.seekToEndOfFile()
            handle.write(data)
            try? handle.close()
        } else {
            try? data.write(to: Paths.log)
        }
    }

    static func error(_ context: String, _ error: Error) {
        write("\(context): \(error.localizedDescription)")
    }
}

struct SlateError: LocalizedError {
    let message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}

/// "~/x" and "$HOME/x" → absolute path.
func expandPath(_ path: String) -> String {
    var p = path.trimmingCharacters(in: .whitespaces)
    for (key, value) in ProcessInfo.processInfo.environment where p.contains("$") {
        p = p.replacingOccurrences(of: "${\(key)}", with: value).replacingOccurrences(of: "$\(key)", with: value)
    }
    return (p as NSString).expandingTildeInPath
}

/// Single-quotes a word for POSIX shells when it needs it.
func shellQuote(_ s: String) -> String {
    let safe = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "@%+=:,./-_"))
    if !s.isEmpty && s.unicodeScalars.allSatisfy({ safe.contains($0) }) { return s }
    return "'" + s.replacingOccurrences(of: "'", with: "'\\''") + "'"
}

extension NSColor {
    /// "#RGB", "#RRGGBB" or "#AARRGGBB" (same format as the Windows config).
    static func hex(_ text: String?, fallback: NSColor) -> NSColor {
        guard var s = text?.trimmingCharacters(in: .whitespaces), s.hasPrefix("#") else { return fallback }
        s.removeFirst()
        if s.count == 3 { s = s.map { "\($0)\($0)" }.joined() }
        guard s.count == 6 || s.count == 8, let v = UInt64(s, radix: 16) else { return fallback }
        let a = s.count == 8 ? CGFloat((v >> 24) & 0xFF) / 255 : 1
        return NSColor(srgbRed: CGFloat((v >> 16) & 0xFF) / 255,
                       green: CGFloat((v >> 8) & 0xFF) / 255,
                       blue: CGFloat(v & 0xFF) / 255,
                       alpha: a)
    }
}

extension NSFont {
    /// First available font from a comma-separated list; "SF Mono" / "monospaced" mean the system monospace font.
    static func firstAvailable(_ families: String, size: CGFloat) -> NSFont {
        for raw in families.split(separator: ",") {
            let name = raw.trimmingCharacters(in: .whitespaces)
            switch name.lowercased() {
            case "sf mono", "monospaced", "system-mono", "system mono":
                return .monospacedSystemFont(ofSize: size, weight: .regular)
            default:
                if let f = NSFontManager.shared.font(withFamily: name, traits: [], weight: 5, size: size) { return f }
                if let f = NSFont(name: name, size: size) { return f }
            }
        }
        return .monospacedSystemFont(ofSize: size, weight: .regular)
    }
}
