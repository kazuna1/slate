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
