import Foundation

/// "Learn from terminal": a small, clearly marked block in ~/.zshrc (and ~/.bashrc if you have one) that
/// appends each folder you `cd` into to visits.log. Slate folds that log into its folder ranking, so it
/// learns from every terminal, like zoxide. Off by default; Slate adds or removes only its own block.
/// Mirrors windows/src/Slate/ShellHook.cs.
enum ShellHook {
    static var visitsLog: URL { Paths.appSupport.appendingPathComponent("visits.log") }

    private static let begin = "# >>> slate: learn folders >>>"
    private static let end = "# <<< slate: learn folders <<<"

    private static let zshBlock = """
    \(begin)
    # Added by Slate (menu bar > Learn from Terminal). Remove with :learn off, or delete this block.
    zmodload zsh/datetime 2>/dev/null
    __slate_chpwd() { print -r -- "$EPOCHSECONDS"$'\\t'"$PWD" >> "$HOME/Library/Application Support/Slate/visits.log" 2>/dev/null }
    typeset -ga chpwd_functions
    chpwd_functions+=(__slate_chpwd)
    \(end)
    """

    private static let bashBlock = """
    \(begin)
    # Added by Slate (menu bar > Learn from Terminal). Remove with :learn off, or delete this block.
    __slate_prompt() {
      if [ "$PWD" != "$__slate_last" ]; then
        __slate_last=$PWD
        printf '%s\\t%s\\n' "$(date +%s)" "$PWD" >> "$HOME/Library/Application Support/Slate/visits.log" 2>/dev/null
      fi
    }
    PROMPT_COMMAND="__slate_prompt${PROMPT_COMMAND:+; $PROMPT_COMMAND}"
    \(end)
    """

    /// ~/.zshrc always (the macOS default shell); ~/.bashrc only if it already exists.
    private static var files: [(URL, String)] {
        let home = URL(fileURLWithPath: NSHomeDirectory())
        var result = [(home.appendingPathComponent(".zshrc"), zshBlock)]
        let bashrc = home.appendingPathComponent(".bashrc")
        if FileManager.default.fileExists(atPath: bashrc.path) { result.append((bashrc, bashBlock)) }
        return result
    }

    static var isInstalled: Bool {
        files.contains { (url, _) in (try? String(contentsOf: url, encoding: .utf8))?.contains(begin) == true }
    }

    static func install() throws {
        for (url, block) in files {
            let text = (try? String(contentsOf: url, encoding: .utf8)) ?? ""
            guard !text.contains(begin) else { continue }
            let sep = text.isEmpty || text.hasSuffix("\n") ? "" : "\n"
            try (text + sep + "\n" + block + "\n").write(to: url, atomically: true, encoding: .utf8)
        }
    }

    static func remove() throws {
        for (url, _) in files {
            guard let text = try? String(contentsOf: url, encoding: .utf8),
                  let start = text.range(of: begin), let stop = text.range(of: end), start.lowerBound < stop.lowerBound else { continue }
            var head = String(text[..<start.lowerBound])
            var tail = String(text[stop.upperBound...])
            while head.hasSuffix("\n") { head.removeLast() }
            while tail.hasPrefix("\n") { tail.removeFirst() }
            try (head + (head.isEmpty ? "" : "\n") + tail).write(to: url, atomically: true, encoding: .utf8)
        }
    }
}
