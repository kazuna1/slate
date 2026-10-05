import AppKit

/// Commands Slate runs in place, with no terminal: a spinner (or progress bar) in the bar, then ✓ or ✗.
/// Only quick, non-interactive commands qualify; everything else still opens a terminal, and
/// Shift+Return forces a terminal for one command. Mirrors windows/src/Slate/Inline.cs.
enum InlinePlan {
    /// Runs a process; `summary` turns its output into the result line.
    case process(InlineTask)
    /// Done in Slate itself (calculator, move to Trash).
    case action(title: String, run: () -> InlineResult)
}

struct InlineResult {
    let ok: Bool
    let text: String
    /// Copied to the clipboard on success (calculator, quick answers).
    var copy: String?
    /// Full output, viewable by clicking a failure.
    var log: String?
}

struct InlineTask {
    let title: String                    // "Cloning kazuna1/ladder"
    let shell: [String]                  // executable + arguments
    let directory: String
    var environment: [String: String] = [:]
    /// Reads progress (0...1) from the output so far.
    var progress: ((String) -> Double?)?
    let summary: (_ status: Int32, _ output: String) -> InlineResult
}

enum Inline {
    // MARK: Deciding

    /// The in-place plan for `text`, or nil to use a terminal. No side effects (also used for the hint).
    static func plan(_ text: String, config: SlateConfig, resolveProject: (String) -> String?) -> InlinePlan? {
        let words = Shell.split(text)
        guard let first = words.first else { return nil }
        let dir = CommandRunner.workingDirectory(config.workingDirectory)
        let shell = CommandRunner.shellPath(config.shell)
        let rest = Array(words.dropFirst())

        // "= 24*365": calculator.
        if text.hasPrefix("=") {
            let expr = String(text.dropFirst())
            guard let value = Calculator.evaluate(expr) else { return nil }
            let shown = Calculator.format(value)
            return .action(title: "= \(shown)") { InlineResult(ok: true, text: "\(expr.trimmingCharacters(in: .whitespaces)) = \(shown)", copy: shown) }
        }

        // "@anything": run it here, show the last line of output.
        if first.hasPrefix("@") {
            let command = String(text.drop(while: { $0 == "@" || $0 == " " }))
            guard !command.isEmpty else { return nil }
            return .process(InlineTask(title: command, shell: [shell, "-l", "-i", "-c", command], directory: dir,
                                       summary: { status, out in genericSummary(status, out, ok: "Done") }))
        }

        switch first {
        case "mkdir", "touch", "cp", "mv":
            guard !rest.isEmpty, !hasGlob(rest) else { return nil }
            let targets = rest.filter { !$0.hasPrefix("-") }
            let done: String
            switch first {
            case "mkdir", "touch": done = "Created " + targets.map { abbreviatePath(absolute($0, dir)) }.joined(separator: ", ")
            case "cp": done = "Copied to " + abbreviatePath(absolute(targets.last ?? "", dir))
            default: done = "Moved to " + abbreviatePath(absolute(targets.last ?? "", dir))
            }
            return .process(InlineTask(title: text, shell: ["/bin/sh", "-c", text], directory: dir,
                                       summary: { status, out in genericSummary(status, out, ok: done) }))

        case "rm", "trash":
            // Moves to the Trash instead of deleting: a typo stays recoverable.
            let targets = rest.filter { !$0.hasPrefix("-") }
            guard !targets.isEmpty, !hasGlob(rest) else { return nil }
            let paths = targets.map { absolute($0, dir) }
            return .action(title: "Moving to Trash") { trash(paths) }

        case "pull", "push", "status":
            // "pull slate": git for a project by name.
            guard rest.count == 1, !Commands.isCommand(first), let folder = resolveProject(rest[0]) else { return nil }
            return git(first, folder: folder, name: (folder as NSString).lastPathComponent)

        case "kill":
            // "kill :3000" stops whatever listens on the port ("kill 3000" stays a normal kill of a PID).
            guard rest.count == 1, rest[0].hasPrefix(":"), Int(rest[0].dropFirst()) != nil else { return nil }
            return killPort(String(rest[0].dropFirst()), dir: dir)

        case "which", "pwd", "date", "whoami", "hostname", "uname", "where":
            return quickAnswer(text, shell: shell, dir: dir)

        case "brew", "npm", "pnpm", "yarn", "pip", "pip3", "pipx", "gem", "cargo":
            guard let verb = rest.first, ["install", "i", "add", "uninstall", "remove", "upgrade", "update"].contains(verb) else { break }
            // JS package managers only for global installs; a project install belongs in a terminal in that project.
            let global = rest.contains("-g") || rest.contains("--global") || (first == "yarn" && verb == "global")
            if ["npm", "pnpm", "yarn"].contains(first), !global { break }
            return .process(InlineTask(title: text, shell: [shell, "-l", "-i", "-c", text], directory: dir,
                                       summary: { status, out in genericSummary(status, out, ok: "\(first) \(verb) finished") }))
        default:
            break
        }

        // "node -v", "git --version": a one-line answer.
        if words.count == 2, ["-v", "-V", "--version", "version"].contains(words[1]) {
            return quickAnswer(text, shell: shell, dir: dir)
        }
        return nil
    }

    /// "slate" style clones: progress from git, then ✓ with the folder.
    static func clone(repo: String, into dir: String) -> InlinePlan {
        let name = String(repo.split(separator: "/").last ?? Substring(repo))
        let gh = GitHubRepos.ghPath ?? "gh"
        let target = (dir as NSString).appendingPathComponent(name)
        return .process(InlineTask(
            title: "Cloning \(repo)", shell: [gh, "repo", "clone", repo, "--", "--progress"], directory: dir,
            environment: ["GIT_TERMINAL_PROMPT": "0"],
            progress: gitProgress,
            summary: { status, out in
                status == 0 ? InlineResult(ok: true, text: "Cloned \(repo) → \(abbreviatePath(target))")
                            : InlineResult(ok: false, text: lastLine(out) ?? "Clone failed", log: out)
            }))
    }

    // MARK: Running

    /// Runs a plan off the main thread. `update` gets progress (nil = spinner); `done` gets the result. Both on main.
    static func run(_ plan: InlinePlan, update: @escaping (Double?) -> Void, done: @escaping (InlineResult) -> Void) {
        switch plan {
        case .action(_, let work):
            DispatchQueue.global(qos: .userInitiated).async {
                let result = work()
                DispatchQueue.main.async { done(result) }
            }
        case .process(let task):
            runProcess(task, update: update, done: done)
        }
    }

    static func title(of plan: InlinePlan) -> String {
        switch plan {
        case .action(let title, _): return title
        case .process(let task): return task.title
        }
    }

    private static func runProcess(_ task: InlineTask, update: @escaping (Double?) -> Void, done: @escaping (InlineResult) -> Void) {
        // Output goes to a file, not a pipe: an app the command launches would inherit a pipe and keep it open.
        let log = FileManager.default.temporaryDirectory.appendingPathComponent("slate-\(UUID().uuidString.prefix(8)).log")
        FileManager.default.createFile(atPath: log.path, contents: nil)
        guard let handle = try? FileHandle(forWritingTo: log) else {
            done(InlineResult(ok: false, text: "Couldn't create a log file."))
            return
        }
        let p = Process()
        p.executableURL = URL(fileURLWithPath: task.shell[0])
        p.arguments = Array(task.shell.dropFirst())
        p.currentDirectoryURL = URL(fileURLWithPath: task.directory)
        // macOS zsh's Terminal session save/restore would add "Restored session…" lines to the output.
        var env = ProcessInfo.processInfo.environment.merging(task.environment) { $1 }
        env["SHELL_SESSIONS_DISABLE"] = "1"
        env["TERM_PROGRAM"] = nil
        env["TERM_SESSION_ID"] = nil
        p.environment = env
        p.standardInput = FileHandle.nullDevice
        p.standardOutput = handle
        p.standardError = handle

        var timer: Timer?
        if let parse = task.progress {
            timer = Timer.scheduledTimer(withTimeInterval: 0.2, repeats: true) { _ in
                let tail = (try? String(contentsOf: log, encoding: .utf8)).map { String($0.suffix(400)) } ?? ""
                update(parse(tail))
            }
        }
        p.terminationHandler = { proc in
            try? handle.close()
            let output = (try? String(contentsOf: log, encoding: .utf8)) ?? ""
            try? FileManager.default.removeItem(at: log)
            let result = task.summary(proc.terminationStatus, output)
            DispatchQueue.main.async {
                timer?.invalidate()
                done(result)
            }
        }
        do {
            try p.run()
        } catch {
            timer?.invalidate()
            done(InlineResult(ok: false, text: error.localizedDescription))
        }
    }

    // MARK: Pieces

    private static func git(_ verb: String, folder: String, name: String) -> InlinePlan {
        let args: [String]
        switch verb {
        case "status": args = ["status", "--short", "--branch"]
        case "pull": args = ["pull", "--progress"]
        default: args = ["push", "--progress"]
        }
        return .process(InlineTask(
            title: "git \(verb) · \(name)", shell: ["/usr/bin/git"] + args, directory: folder,
            environment: ["GIT_TERMINAL_PROMPT": "0"], progress: verb == "status" ? nil : gitProgress,
            summary: { status, out in
                guard status == 0 else { return InlineResult(ok: false, text: "\(name): " + (lastLine(out) ?? "git \(verb) failed"), log: out) }
                switch verb {
                case "status": return InlineResult(ok: true, text: "\(name): " + statusSummary(out), log: out)
                case "pull":
                    return InlineResult(ok: true, text: "\(name): " + (out.contains("Already up to date") ? "already up to date" : (lastLine(out) ?? "pulled")), log: out)
                default:
                    return InlineResult(ok: true, text: "\(name): " + (out.contains("Everything up-to-date") ? "nothing to push" : "pushed"), log: out)
                }
            }))
    }

    /// "## main...origin/main [ahead 1]" + file lines → "main · 2 changed · ahead 1" / "main · clean".
    private static func statusSummary(_ out: String) -> String {
        let lines = out.split(separator: "\n").map(String.init)
        var branch = "?"
        var extra = ""
        if let head = lines.first, head.hasPrefix("## ") {
            let h = head.dropFirst(3)
            branch = String(h.split(separator: ".").first ?? h).trimmingCharacters(in: .whitespaces)
            if let bracket = h.range(of: "[") { extra = " · " + h[bracket.upperBound...].replacingOccurrences(of: "]", with: "") }
        }
        let files = lines.dropFirst().filter { !$0.isEmpty }
        let changes = files.isEmpty ? "clean" : "\(files.count) changed"
        return "\(branch) · \(changes)\(extra)"
    }

    private static func killPort(_ port: String, dir: String) -> InlinePlan {
        let script = """
        pids=$(/usr/sbin/lsof -ti tcp:\(port) -sTCP:LISTEN)
        [ -z "$pids" ] && { echo "Nothing is listening on port \(port)"; exit 1; }
        for p in $pids; do echo "$(basename "$(ps -p $p -o comm=)") (pid $p)"; done
        kill $pids
        """
        return .process(InlineTask(title: "Stopping port \(port)", shell: ["/bin/sh", "-c", script], directory: dir,
                                   summary: { status, out in
            let who = out.split(separator: "\n").map(String.init).joined(separator: ", ")
            return status == 0 ? InlineResult(ok: true, text: "Stopped \(who)") : InlineResult(ok: false, text: lastLine(out) ?? "Couldn't stop port \(port)")
        }))
    }

    private static func quickAnswer(_ text: String, shell: String, dir: String) -> InlinePlan {
        .process(InlineTask(title: text, shell: [shell, "-l", "-i", "-c", text], directory: dir, summary: { status, out in
            let answer = out.split(separator: "\n").map { $0.trimmingCharacters(in: .whitespaces) }.first { !$0.isEmpty } ?? ""
            return status == 0 && !answer.isEmpty
                ? InlineResult(ok: true, text: answer, copy: answer, log: out)
                : InlineResult(ok: false, text: lastLine(out) ?? "\(text) failed", log: out)
        }))
    }

    private static func trash(_ paths: [String]) -> InlineResult {
        var moved: [String] = []
        for path in paths {
            do {
                try FileManager.default.trashItem(at: URL(fileURLWithPath: path), resultingItemURL: nil)
                moved.append((path as NSString).lastPathComponent)
            } catch {
                let reason = FileManager.default.fileExists(atPath: path) ? error.localizedDescription : "No such file: \(abbreviatePath(path))"
                return InlineResult(ok: false, text: moved.isEmpty ? reason : "Moved \(moved.joined(separator: ", ")) to Trash; then: \(reason)")
            }
        }
        return InlineResult(ok: true, text: "Moved \(moved.joined(separator: ", ")) to Trash")
    }

    private static func genericSummary(_ status: Int32, _ out: String, ok: String) -> InlineResult {
        status == 0 ? InlineResult(ok: true, text: lastLine(out) ?? ok, log: out)
                    : InlineResult(ok: false, text: lastLine(out) ?? "Failed (exit code \(status))", log: out)
    }

    /// Last meaningful line, without git's progress noise.
    static func lastLine(_ out: String) -> String? {
        out.replacingOccurrences(of: "\r", with: "\n").split(separator: "\n")
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .last { !$0.isEmpty && !$0.contains("%") && !$0.hasPrefix("remote:") }
    }

    /// git's "Receiving objects:  45% (..)" / "Resolving deltas: 80%" → overall 0...1.
    static func gitProgress(_ tail: String) -> Double? {
        let line = tail.replacingOccurrences(of: "\r", with: "\n").split(separator: "\n").last { $0.contains("%") }.map(String.init)
        guard let line, let pct = line.range(of: #"(\d+)%"#, options: .regularExpression),
              let n = Double(line[pct].dropLast()) else { return nil }
        if line.contains("Resolving deltas") { return 0.8 + 0.2 * n / 100 }
        if line.contains("Receiving objects") || line.contains("Writing objects") { return 0.1 + 0.7 * n / 100 }
        return 0.1 * n / 100
    }

    private static func absolute(_ path: String, _ dir: String) -> String {
        let p = expandPath(path)
        return p.hasPrefix("/") ? p : (dir as NSString).appendingPathComponent(p)
    }

    private static func hasGlob(_ words: [String]) -> Bool {
        words.contains { $0.contains("*") || $0.contains("?") || $0.contains("[") }
    }
}

/// Splits a command line into words, honouring '…' and "…" quotes.
enum Shell {
    static func split(_ text: String) -> [String] {
        var words: [String] = []
        var current = ""
        var quote: Character?
        var hasWord = false
        for c in text {
            if let q = quote {
                if c == q { quote = nil } else { current.append(c) }
            } else if c == "'" || c == "\"" {
                quote = c
                hasWord = true
            } else if c == " " || c == "\t" {
                if hasWord { words.append(current); current = ""; hasWord = false }
            } else {
                current.append(c)
                hasWord = true
            }
        }
        if hasWord { words.append(current) }
        return words
    }
}

/// `= 24*365`: + - * / % ^ and parentheses. A small parser rather than NSExpression, which crashes on bad input.
enum Calculator {
    static func evaluate(_ text: String) -> Double? {
        var p = Parser(chars: Array(text.replacingOccurrences(of: " ", with: "").replacingOccurrences(of: ",", with: "")))
        guard !p.chars.isEmpty, let v = p.expression(), p.i == p.chars.count, v.isFinite else { return nil }
        return v
    }

    static func format(_ v: Double) -> String {
        if v == v.rounded(), abs(v) < 1e15 { return String(Int64(v)) }
        return String(format: "%.10g", v)
    }

    private struct Parser {
        let chars: [Character]
        var i = 0

        mutating func expression() -> Double? {
            guard var v = term() else { return nil }
            while i < chars.count, chars[i] == "+" || chars[i] == "-" {
                let op = chars[i]; i += 1
                guard let r = term() else { return nil }
                v = op == "+" ? v + r : v - r
            }
            return v
        }

        mutating func term() -> Double? {
            guard var v = power() else { return nil }
            while i < chars.count, "*/%x×÷".contains(chars[i]) {
                let op = chars[i]; i += 1
                guard let r = power() else { return nil }
                switch op {
                case "/", "÷": v /= r
                case "%": v = v.truncatingRemainder(dividingBy: r)
                default: v *= r
                }
            }
            return v
        }

        mutating func power() -> Double? {
            guard let base = unary() else { return nil }
            if i < chars.count, chars[i] == "^" {
                i += 1
                guard let exp = power() else { return nil }
                return pow(base, exp)
            }
            return base
        }

        mutating func unary() -> Double? {
            if i < chars.count, chars[i] == "-" { i += 1; return unary().map { -$0 } }
            if i < chars.count, chars[i] == "+" { i += 1; return unary() }
            return primary()
        }

        mutating func primary() -> Double? {
            guard i < chars.count else { return nil }
            if chars[i] == "(" {
                i += 1
                guard let v = expression(), i < chars.count, chars[i] == ")" else { return nil }
                i += 1
                return v
            }
            let start = i
            while i < chars.count, chars[i].isNumber || chars[i] == "." { i += 1 }
            return i > start ? Double(String(chars[start..<i])) : nil
        }
    }
}
