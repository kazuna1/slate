import Foundation

/// Opens a terminal window running a command in your interactive shell, leaving it open afterwards.
///
/// The command is written into a throwaway .command script as base64 and `eval`ed inside your normal
/// interactive shell, so quotes and special characters arrive untouched and your rc file (aliases,
/// functions, zoxide) is loaded once. Afterwards you're in that same shell, wherever the command left you.
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

        // Don't wait for the terminal to start (that froze the bar); fall back to Terminal if the app is missing.
        let terminal = config.terminal
        try open(url, with: terminal) {
            Log.write("Couldn't open \(terminal)")
            guard terminal != "Terminal" else {
                onBackgroundFailure?("Couldn't open Terminal.")
                return
            }
            try? open(url, with: "Terminal") { onBackgroundFailure?("Couldn't open \(terminal) or Terminal.") }
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
    ///
    /// Terminal runs it from its own login shell; the script then replaces itself with ONE interactive shell
    /// that loads your rc file, runs the command, records it in history and stays open. (It used to start a
    /// shell for the command and another one afterwards: three rc loads instead of two.)
    static func script(for command: String, config: SlateConfig, folder: String? = nil) throws -> String {
        let shell = resolveShell(config.shell)
        guard !shell.contains("'") else { throw SlateError("Shell path can't contain a quote: \(shell)") }
        let dir = folder ?? workingDirectory(config.workingDirectory)
        let header = "#!/bin/sh\ncd \(shellQuote(dir)) 2>/dev/null\nclear\n"

        guard !command.trimmingCharacters(in: .whitespaces).isEmpty else {
            return header + "exec '\(shell)' -i\n"
        }

        let encoded = Data(command.utf8).base64EncodedString() // base64: no quoting can break
        let wrap = try wrapperDirectory()
        switch (shell as NSString).lastPathComponent {
        case "zsh":
            // zsh reads .zshenv/.zshrc from $ZDOTDIR; Slate's pair loads yours, then runs the command.
            return header + "SLATE_RUN=\(encoded) SLATE_ZDOTDIR=\"${ZDOTDIR-}\" ZDOTDIR=\(shellQuote(wrap.path)) exec '\(shell)' -i\n"
        case "bash":
            return header + "SLATE_RUN=\(encoded) exec '\(shell)' --rcfile \(shellQuote(wrap.appendingPathComponent("bashrc").path)) -i\n"
        case "fish":
            let run = "set -l c (printf %s $SLATE_RUN | /usr/bin/base64 --decode | string collect); set -e SLATE_RUN; eval $c"
            return header + "SLATE_RUN=\(encoded) exec '\(shell)' -C \(shellQuote(run))\n"
        default:
            // Unknown shell: run the command, then a fresh interactive shell.
            let body = "eval \"$(printf %s \(encoded) | /usr/bin/base64 --decode)\""
            return header + "exec '\(shell)' -i -c '\(body); exec '\"'\"'\(shell)'\"'\"' -i'\n"
        }
    }

    /// Startup files that load the user's own config and then run $SLATE_RUN inside the same shell.
    private static func wrapperDirectory() throws -> URL {
        let dir = Paths.scripts.appendingPathComponent("shell", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let files = [
            ".zshenv": """
            # Slate: your own .zshenv, then .zshrc below.
            [[ -f ${SLATE_ZDOTDIR:-$HOME}/.zshenv ]] && source ${SLATE_ZDOTDIR:-$HOME}/.zshenv

            """,
            ".zshrc": """
            # Slate: put ZDOTDIR back, load your .zshrc, then run the command in this same shell.
            if [[ -n $SLATE_ZDOTDIR ]]; then ZDOTDIR=$SLATE_ZDOTDIR; else unset ZDOTDIR; fi
            unset SLATE_ZDOTDIR
            [[ -f ${ZDOTDIR:-$HOME}/.zshrc ]] && source ${ZDOTDIR:-$HOME}/.zshrc
            if [[ -n $SLATE_RUN ]]; then
              __slate_cmd=$(print -r -- $SLATE_RUN | /usr/bin/base64 --decode)
              unset SLATE_RUN
              print -s -- $__slate_cmd
              eval -- $__slate_cmd
              unset __slate_cmd
            fi

            """,
            "bashrc": """
            # Slate: load your .bashrc, then run the command in this same shell.
            [ -f ~/.bashrc ] && . ~/.bashrc
            if [ -n "$SLATE_RUN" ]; then
              __slate_cmd=$(printf %s "$SLATE_RUN" | /usr/bin/base64 --decode)
              unset SLATE_RUN
              history -s "$__slate_cmd"
              eval "$__slate_cmd"
              unset __slate_cmd
            fi

            """,
        ]
        for (name, content) in files {
            let url = dir.appendingPathComponent(name)
            if (try? String(contentsOf: url, encoding: .utf8)) != content {
                try content.write(to: url, atomically: true, encoding: .utf8)
            }
        }
        return dir
    }

    /// Asks Launch Services to open the script in `app` and returns at once; `onFailure` runs on the main thread.
    private static func open(_ script: URL, with app: String, onFailure: @escaping () -> Void) throws {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/open")
        p.arguments = ["-a", app, script.path]
        p.standardError = FileHandle.nullDevice
        p.terminationHandler = { proc in
            if proc.terminationStatus != 0 { DispatchQueue.main.async(execute: onFailure) }
        }
        try p.run()
    }

    /// The shell to use for the `shell` setting (also used by in-place commands).
    static func shellPath(_ setting: String) -> String { resolveShell(setting) }

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

    /// The default folder (config `workingDirectory`), or home if it's missing.
    static func workingDirectory(_ setting: String) -> String {
        defaultFolderExists(setting) ? expandPath(setting) : NSHomeDirectory()
    }

    static func defaultFolderExists(_ setting: String) -> Bool {
        var isDir: ObjCBool = false
        return FileManager.default.fileExists(atPath: expandPath(setting), isDirectory: &isDir) && isDir.boolValue
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
