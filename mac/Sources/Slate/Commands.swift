import Foundation

/// Knows which words are real commands, so a project with the same name as a program
/// ("node", "git") never hijacks it: real commands always win. Mirrors windows/src/Slate/Commands.cs.
enum Commands {
    // Shell builtins and keywords that aren't files on PATH.
    private static let shellWords: Set<String> = [
        "cd", "echo", "exit", "export", "source", "alias", "unalias", "set", "unset", "type", "which", "where",
        "history", "fc", "jobs", "fg", "bg", "kill", "wait", "pushd", "popd", "dirs", "eval", "exec", "read",
        "print", "printf", "test", "true", "false", "if", "for", "while", "until", "case", "function", "return",
        "z", "zi", "time", "builtin", "command", "hash", "rehash", "autoload", "setopt", "unsetopt",
    ]

    /// GUI apps get a short PATH, so also look where package managers and nvm put programs.
    private static let onPath: Set<String> = {
        var dirs = (ProcessInfo.processInfo.environment["PATH"] ?? "").split(separator: ":").map(String.init)
        dirs += ["/usr/bin", "/bin", "/usr/sbin", "/sbin", "/opt/homebrew/bin", "/opt/homebrew/sbin", "/usr/local/bin",
                 NSHomeDirectory() + "/.local/bin", NSHomeDirectory() + "/.cargo/bin", NSHomeDirectory() + "/bin"]
        let nvm = NSHomeDirectory() + "/.nvm/versions/node"
        for version in (try? FileManager.default.contentsOfDirectory(atPath: nvm)) ?? [] { dirs.append("\(nvm)/\(version)/bin") }

        var names = Set<String>()
        for dir in Set(dirs) {
            for file in (try? FileManager.default.contentsOfDirectory(atPath: dir)) ?? [] {
                if FileManager.default.isExecutableFile(atPath: "\(dir)/\(file)") { names.insert(file) }
            }
        }
        return names
    }()

    /// True if `word` runs something on its own (a program on PATH or a shell word).
    static func isCommand(_ word: String) -> Bool {
        shellWords.contains(word) || onPath.contains(word)
    }
}

/// What typing a project name runs. `projectCommand` holds the command plus its default options
/// ("claude -c": continue the last conversation). Options you type replace the defaults
/// ("slate -r" → "claude -r"), and "-n" means "no defaults" ("slate -n" → "claude", a new conversation).
/// Mirrors windows/src/Slate/Commands.cs.
enum ProjectCommand {
    static func build(_ projectCommand: String, folder: String, args: String) -> String {
        let parts = projectCommand.trimmingCharacters(in: .whitespaces).split(separator: " ", maxSplits: 1).map(String.init)
        guard let base = parts.first else { return "" }
        var defaults = parts.count > 1 ? parts[1] : ""

        if args == "-n" || args.hasPrefix("-n ") {
            return (base + " " + args.dropFirst(2).trimmingCharacters(in: .whitespaces)).trimmingCharacters(in: .whitespaces)
        }
        if !args.isEmpty { return "\(base) \(args)" }

        // "claude -c" with nothing to continue would just fail: start a new conversation instead.
        if base == "claude", defaults.split(separator: " ").contains("-c"), !ClaudeSessions.exist(in: folder) {
            defaults = defaults.split(separator: " ").filter { $0 != "-c" }.joined(separator: " ")
        }
        return (base + " " + defaults).trimmingCharacters(in: .whitespaces)
    }
}

/// Claude Code stores a folder's conversations in ~/.claude/projects/<path with non-alphanumerics as "-">.
enum ClaudeSessions {
    static func exist(in folder: String) -> Bool {
        let slug = String(folder.map { $0.isASCII && ($0.isLetter || $0.isNumber) ? $0 : "-" })
        let dir = NSHomeDirectory() + "/.claude/projects/" + slug
        let files = (try? FileManager.default.contentsOfDirectory(atPath: dir)) ?? []
        return files.contains { $0.hasSuffix(".jsonl") }
    }
}
