import Foundation

/// Opens a terminal window running a command in your interactive shell, leaving it open afterwards.
///
/// The command is written into a throwaway .command script as base64 and `eval`ed by an interactive
/// shell, so quotes and special characters arrive untouched and your rc file (aliases, functions,
/// zoxide) is loaded. After it finishes, the window drops into a normal shell in whatever directory
/// the command left you in.
enum CommandRunner {
    /// `folder` runs the command inside that folder; an empty `command` just opens a shell there.
    /// Commands starting with "@" run hidden, with no terminal window ("@open -a 'Visual Studio Code' .").
    static let backgroundPrefix: Character = "@"

    static func run(_ command: String, config: SlateConfig, folder: String? = nil,
                    onBackgroundFailure: ((String) -> Void)? = nil) throws {
        let trimmed = command.trimmingCharacters(in: .whitespaces)
        if trimmed.first == backgroundPrefix {
            try runHidden(String(trimmed.dropFirst()), config: config, folder: folder, onFailure: onBackgroundFailure)
            return
        }
        cleanUpOldScripts()
        let url = Paths.scripts.appendingPathComponent("run-\(UUID().uuidString.prefix(8)).command")
        try script(for: command, config: config, folder: folder).write(to: url, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: url.path)

        if !open(url, with: config.terminal), config.terminal != "Terminal" {
            Log.write("Couldn't open \(config.terminal); falling back to Terminal")
            guard open(url, with: "Terminal") else { throw SlateError("Couldn't open Terminal.") }
        }
    }

    /// Runs a launcher-style command in a hidden login shell (so Homebrew's PATH is there) that exits when
    /// the command returns, leaving no window behind. Output isn't captured: the launched app would inherit
    /// the pipe and keep it open. The exit code is enough.
    private static func runHidden(_ command: String, config: SlateConfig, folder: String?,
                                  onFailure: ((String) -> Void)?) throws {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: resolveShell(config.shell))
        p.arguments = ["-l", "-c", command]
        p.currentDirectoryURL = URL(fileURLWithPath: folder ?? workingDirectory(config.workingDirectory))
        p.standardInput = FileHandle.nullDevice
        p.standardOutput = FileHandle.nullDevice
        p.standardError = FileHandle.nullDevice
        p.terminationHandler = { proc in
            guard proc.terminationStatus != 0 else { return }
            Log.write("Background command \"\(command)\" exited with code \(proc.terminationStatus)")
            DispatchQueue.main.async {
                onFailure?("\"\(command)\" failed (exit code \(proc.terminationStatus)). Run it without @ to see the error.")
            }
        }
        try p.run()
    }

    /// The .command script for a command (separate so it can be tested without opening a terminal).
    static func script(for command: String, config: SlateConfig, folder: String? = nil) throws -> String {
        let shell = resolveShell(config.shell)
        guard !shell.contains("'") else { throw SlateError("Shell path can't contain a quote: \(shell)") }
        let dir = folder ?? workingDirectory(config.workingDirectory)

        guard !command.trimmingCharacters(in: .whitespaces).isEmpty else {
            return """
            #!/bin/sh
            cd \(shellQuote(dir)) 2>/dev/null
            clear
            exec '\(shell)' -i

            """
        }

        let encoded = Data(command.utf8).base64EncodedString()
        let decode = "printf %s \(encoded) | /usr/bin/base64 --decode"
        let isFish = (shell as NSString).lastPathComponent == "fish"
        let body = isFish ? "eval (\(decode) | string collect)" : "eval \"$(\(decode))\""

        return """
        #!/bin/sh
        cd \(shellQuote(dir)) 2>/dev/null
        clear
        exec '\(shell)' -i -c '\(body); exec '"'"'\(shell)'"'"' -i'

        """
    }

    private static func open(_ script: URL, with app: String) -> Bool {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/open")
        p.arguments = ["-a", app, script.path]
        p.standardError = FileHandle.nullDevice
        do {
            try p.run()
            p.waitUntilExit()
            return p.terminationStatus == 0
        } catch {
            return false
        }
    }

    private static func resolveShell(_ setting: String) -> String {
        let s = setting.trimmingCharacters(in: .whitespaces)
        switch s.lowercased() {
        case "", "auto":
            return ProcessInfo.processInfo.environment["SHELL"] ?? "/bin/zsh"
        case "zsh": return "/bin/zsh"
        case "bash": return "/bin/bash"
        case "fish":
            return ["/opt/homebrew/bin/fish", "/usr/local/bin/fish"].first { FileManager.default.fileExists(atPath: $0) } ?? "fish"
        default:
            return expandPath(s)
        }
    }

    private static func workingDirectory(_ setting: String) -> String {
        let dir = expandPath(setting)
        var isDir: ObjCBool = false
        return FileManager.default.fileExists(atPath: dir, isDirectory: &isDir) && isDir.boolValue
            ? dir : NSHomeDirectory()
    }

    /// Terminal reads the script right away; anything older than an hour is safe to delete.
    private static func cleanUpOldScripts() {
        let fm = FileManager.default
        guard let files = try? fm.contentsOfDirectory(at: Paths.scripts, includingPropertiesForKeys: [.contentModificationDateKey]) else { return }
        let cutoff = Date().addingTimeInterval(-3600)
        for file in files where file.pathExtension == "command" {
            let date = (try? file.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate ?? .distantPast
            if date < cutoff { try? fm.removeItem(at: file) }
        }
    }
}
