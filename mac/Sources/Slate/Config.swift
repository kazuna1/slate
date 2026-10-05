import Foundation

/// Same shape as the Windows config, with macOS defaults.
struct SlateConfig: Codable {
    /// "Option+Space", "Ctrl+Space", "Cmd+Shift+K", ...
    var hotkey = "Option+Space"
    /// "auto" ($SHELL), "zsh", "bash", "fish", or a full path.
    var shell = "auto"
    /// Any app that opens .command files: "Terminal", "iTerm", ...
    var terminal = "Terminal"
    var workingDirectory = "~"
    var historySize = 500
    var checkForUpdates = true
    /// Start Slate when you log in. Kept in sync with the menu-bar toggle.
    var launchAtLogin = true
    /// Name of the last theme picked from the menu (shown with a checkmark).
    var theme = ""
    /// Built-in shortcuts: "<key> <folder>" finds the folder by name and runs the command there.
    /// An empty command just opens a terminal in that folder. Works on any Mac; no .zshrc setup needed.
    var shortcuts: [String: String] = [
        "z": "",
        "cc": "claude",
        "vs": "@open -a 'Visual Studio Code' .", // "@": runs hidden, no terminal left behind
    ]
    /// What typing just a project name runs inside it, with default options: "slate" → claude -c (continue),
    /// "slate -n" → claude (new conversation), "slate -r" → claude -r (typed options replace the defaults).
    /// Empty turns the feature off. Real commands with the same name always win.
    var projectCommand = "claude -c"
    var useZoxide = true
    /// Tab completion also offers every subfolder of these.
    var projectRoots = ["~/projects", "~/Developer", "~/code"]
    var appearance = Appearance()
    var animations = Animations()

    struct Appearance: Codable {
        var width: Double = 760
        var height: Double = 64
        /// 0 = main display (the one with the menu bar), 1..n = a specific display.
        var monitor = 0
        /// "top", "center" or "bottom". Dragging the bar updates the offsets.
        var anchor = "top"
        var offsetX: Double = 0
        var offsetY: Double = 48
        var cornerRadius: Double = 18
        var borderThickness: Double = 1.5
        var background = ["#2E1065", "#1E0B45", "#12062B"]
        var backgroundAngle: Double = 0
        var borderColor = "#7C4DFF"
        var borderHighlight = "#E9D5FF"
        var glowColor = "#8B5CF6"
        var glowSize: Double = 30
        var glowOpacity: Double = 0.8
        var textColor = "#F5F0FF"
        var placeholderColor = "#8B7BB3"
        var promptColor = "#C084FC"
        var prompt = "❯"
        var placeholder = "run anything..."
        var fontFamily = "SF Mono, Menlo"
        var fontSize: Double = 20
        var showHint = true
        /// Folder chip showing where commands run (the default folder).
        var showFolder = true
        /// "" or "vines" (branches and leaves along the edges; set by the Forest theme).
        var decoration = ""
        /// Tiny version number under the hint.
        var showVersion = true
        /// Overrides the hint; defaults to the hotkey, e.g. "⌥ Space".
        var hintText: String?
        /// Bar opacity while it sits on the desktop (1 when summoned).
        var idleOpacity: Double = 0.9
    }

    struct Animations: Codable {
        var enabled = true
        var summonPop = true
        var summonDurationMs: Double = 220
        var glowPulse = true
        var glowPulseSeconds: Double = 3.5
        var borderShimmer = true
        var borderShimmerSeconds: Double = 6
        var runFlash = true

        func on(_ feature: Bool) -> Bool { enabled && feature }
    }
}

/// Loads config.json (comments and trailing commas allowed, missing keys get defaults)
/// and calls `onChange` when the file is saved.
final class ConfigStore {
    private(set) var createdNew = false
    var onChange: (() -> Void)?

    private var source: DispatchSourceFileSystemObject?
    private var pending: DispatchWorkItem?
    private var ignoreUntil = Date.distantPast

    init() {
        if !FileManager.default.fileExists(atPath: Paths.config.path) {
            save(SlateConfig())
            createdNew = true
        }
        arm()
    }

    func load() throws -> SlateConfig {
        let data = try Data(contentsOf: Paths.config)
        guard let user = try JSONSerialization.jsonObject(with: data, options: [.json5Allowed]) as? [String: Any] else {
            throw SlateError("config.json must be a JSON object.")
        }
        let defaults = try JSONSerialization.jsonObject(with: JSONEncoder().encode(SlateConfig())) as? [String: Any] ?? [:]
        let merged = Self.merge(defaults, user)
        var config = try JSONDecoder().decode(SlateConfig.self, from: JSONSerialization.data(withJSONObject: merged))
        var changed = false
        // 1.3.4: the VS Code shortcut was renamed from "vc" to "vs".
        if let vsCode = config.shortcuts.removeValue(forKey: "vc") {
            if config.shortcuts["vs"] == nil { config.shortcuts["vs"] = vsCode }
            changed = true
        }
        // 1.3.5: VS Code opens without leaving a terminal behind.
        if config.shortcuts["vs"] == "open -a 'Visual Studio Code' ." {
            config.shortcuts["vs"] = "@open -a 'Visual Studio Code' ."
            changed = true
        }
        // 1.5.2: typing a project name continues the last conversation by default ("-n" starts a new one).
        if config.projectCommand == "claude" {
            config.projectCommand = "claude -c"
            changed = true
        }
        if changed { save(config) }
        return config
    }

    func save(_ config: SlateConfig) {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .withoutEscapingSlashes]
        do {
            ignoreUntil = Date().addingTimeInterval(0.8) // don't reload our own write
            try encoder.encode(config).write(to: Paths.config, options: .atomic)
        } catch {
            Log.error("Saving config", error)
        }
    }

    private static func merge(_ base: [String: Any], _ over: [String: Any]) -> [String: Any] {
        var result = base
        for (key, value) in over {
            // "shortcuts" is a user-owned list: replace it, so removing a default sticks.
            if key != "shortcuts", let b = base[key] as? [String: Any], let o = value as? [String: Any] {
                result[key] = merge(b, o)
            } else {
                result[key] = value
            }
        }
        return result
    }

    /// Watches the file itself; re-arms when an editor replaces it (atomic save).
    private func arm() {
        source?.cancel()
        source = nil
        let fd = open(Paths.config.path, O_EVTONLY)
        guard fd >= 0 else {
            DispatchQueue.main.asyncAfter(deadline: .now() + 1) { [weak self] in self?.arm() }
            return
        }
        let src = DispatchSource.makeFileSystemObjectSource(
            fileDescriptor: fd, eventMask: [.write, .extend, .delete, .rename, .attrib], queue: .main)
        src.setEventHandler { [weak self, unowned src] in
            guard let self else { return }
            if !src.data.intersection([.delete, .rename]).isEmpty {
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { self.arm() }
            }
            self.poke()
        }
        src.setCancelHandler { close(fd) }
        source = src
        src.resume()
    }

    private func poke() {
        guard Date() > ignoreUntil else { return }
        pending?.cancel()
        let work = DispatchWorkItem { [weak self] in self?.onChange?() }
        pending = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.25, execute: work)
    }
}
