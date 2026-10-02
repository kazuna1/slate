import AppKit

final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private let store = ConfigStore()
    private var config = SlateConfig()
    private var history: History!
    private var bar: Bar!
    private let hotkeys = HotkeyManager()
    private let notifier = Notifier()
    private var statusItem: NSStatusItem!
    private var loginItem: NSMenuItem!
    private var updateItem: NSMenuItem!
    private var showItem: NSMenuItem!

    private var update: UpdateInfo?
    private var notifiedVersion: String?
    private var updating = false
    private var updateTimer: Timer?

    func applicationDidFinishLaunching(_ notification: Notification) {
        // One Slate at a time.
        if let id = Bundle.main.bundleIdentifier,
           NSRunningApplication.runningApplications(withBundleIdentifier: id).contains(where: { $0 != .current }) {
            NSApp.terminate(nil)
            return
        }

        var configError: String?
        do { config = try store.load() } catch { configError = error.localizedDescription }

        history = History(maxSize: config.historySize)
        let (hotkey, hotkeyError) = parseHotkey(config.hotkey)

        bar = Bar(config: config, history: history, hotkeyDisplay: hotkey.display)
        bar.onBuiltin = { [weak self] in self?.handleBuiltin($0) }
        bar.notify = { [weak self] in self?.notifier.post($0, $1) }
        bar.onMoved = { [weak self] moved in
            self?.config = moved
            self?.store.save(moved)
        }
        bar.show()

        buildStatusItem(hotkeyDisplay: hotkey.display)
        hotkeys.onPress = { [weak self] in self?.bar.toggle() }
        registerHotkey(hotkey)
        store.onChange = { [weak self] in self?.reload() }
        notifier.onUpdateClicked = { [weak self] in self?.updateNow() }
        notifier.fallback = { [weak self] title, body, isUpdate in
            self?.bar.showMessage(title, body, action: isUpdate ? { self?.updateNow() } : nil)
        }

        if store.createdNew {
            notifier.post("Slate is running", "Press \(hotkey.display) anywhere to summon the bar. The ❯ in the menu bar has options.")
        }
        ensureLoginItem()
        if let configError { notifier.post("Slate: config.json has an error", configError + " Using defaults.") }
        if let hotkeyError { notifier.post("Slate: bad hotkey", hotkeyError) }

        // First check shortly after start, then daily.
        DispatchQueue.main.asyncAfter(deadline: .now() + 30) { [weak self] in self?.scheduledUpdateCheck() }
        updateTimer = Timer.scheduledTimer(withTimeInterval: 24 * 3600, repeats: true) { [weak self] _ in
            self?.scheduledUpdateCheck()
        }
    }

    // MARK: Hotkey / config

    private func parseHotkey(_ text: String) -> (Hotkey, String?) {
        do { return (try Hotkey.parse(text), nil) } catch {
            return (try! Hotkey.parse(Hotkey.defaultText), error.localizedDescription + " Using \(Hotkey.defaultText).")
        }
    }

    private func registerHotkey(_ hotkey: Hotkey) {
        if !hotkeys.register(hotkey) {
            notifier.post("Slate", "\(hotkey.display) is already used by another app. Pick another hotkey with :config.")
        }
        showItem?.title = "Show Slate    \(hotkey.display)"
    }

    private func reload() {
        let newConfig: SlateConfig
        do { newConfig = try store.load() } catch {
            notifier.post("Slate: config.json has an error", error.localizedDescription + " Keeping the previous settings.")
            return
        }
        let (hotkey, hotkeyError) = parseHotkey(newConfig.hotkey)
        if let hotkeyError { notifier.post("Slate: bad hotkey", hotkeyError) }
        if newConfig.hotkey != config.hotkey { registerHotkey(hotkey) }
        config = newConfig
        history.maxSize = max(1, newConfig.historySize)
        bar.apply(newConfig, hotkeyDisplay: hotkey.display)
    }

    private func openConfig() {
        NSWorkspace.shared.open(Paths.config)
    }

    // MARK: Menu bar

    private func buildStatusItem(hotkeyDisplay: String) {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        statusItem.button?.title = "❯"
        statusItem.button?.font = .systemFont(ofSize: 15, weight: .bold)
        statusItem.button?.toolTip = "Slate \(Updater.currentVersion)"

        let menu = NSMenu()
        menu.delegate = self
        showItem = menu.addItem(withTitle: "Show Slate    \(hotkeyDisplay)", action: #selector(menuShow), keyEquivalent: "")
        menu.addItem(.separator())
        menu.addItem(withTitle: "Open Config", action: #selector(menuOpenConfig), keyEquivalent: ",")
        menu.addItem(withTitle: "Reload Config", action: #selector(menuReload), keyEquivalent: "")
        loginItem = menu.addItem(withTitle: "Launch at Login", action: #selector(menuToggleLogin), keyEquivalent: "")
        updateItem = menu.addItem(withTitle: "Check for Updates…", action: #selector(menuUpdate), keyEquivalent: "")
        menu.addItem(.separator())
        let version = menu.addItem(withTitle: "Slate \(Updater.currentVersion)", action: nil, keyEquivalent: "")
        version.isEnabled = false
        menu.addItem(withTitle: "Quit Slate", action: #selector(menuQuit), keyEquivalent: "q")
        for item in menu.items where item.action != nil { item.target = self }
        statusItem.menu = menu
    }

    func menuWillOpen(_ menu: NSMenu) {
        switch LoginItem.status {
        case .enabled: loginItem.state = .on
        case .requiresApproval: loginItem.state = .mixed
        default: loginItem.state = .off
        }
    }

    @objc private func menuShow() { DispatchQueue.main.async { self.bar.summon() } }
    @objc private func menuOpenConfig() { openConfig() }
    @objc private func menuReload() { reload() }
    @objc private func menuUpdate() { updateNow() }
    @objc private func menuQuit() { NSApp.terminate(nil) }

    @objc private func menuToggleLogin() {
        setLoginItem(!LoginItem.isEnabled)
    }

    private func setLoginItem(_ on: Bool) {
        config.launchAtLogin = on
        store.save(config)
        do {
            try LoginItem.set(on)
        } catch {
            Log.error("Changing login item", error)
        }
        Log.write("Launch at login: \(LoginItem.statusText)")
        if on && LoginItem.status == .requiresApproval {
            askToApproveLoginItem()
        } else if on && !LoginItem.isEnabled {
            notifier.post("Slate", "Couldn't turn on Launch at Login (\(LoginItem.statusText)).")
        } else {
            notifier.post("Slate", on ? "Slate will start when you log in." : "Slate won't start when you log in.")
        }
    }

    /// Starting at login is the point of Slate, so register on every launch until it sticks
    /// (unless the user turned it off). Only installed copies register, not dev builds.
    private func ensureLoginItem() {
        guard Updater.appBundle != nil, config.launchAtLogin else { return }
        let before = LoginItem.status
        if before == .notRegistered || before == .notFound {
            do { try LoginItem.set(true) } catch { Log.error("Registering login item", error) }
        }
        Log.write("Launch at login: \(LoginItem.statusText)")
        if LoginItem.status == .requiresApproval && before != .requiresApproval {
            askToApproveLoginItem()
        }
    }

    private func askToApproveLoginItem() {
        bar.showMessage("Allow Slate to start at login", "Click here, then switch Slate on in Login Items.",
                        action: { LoginItem.openSettings() })
    }

    // MARK: Built-in commands

    private func handleBuiltin(_ text: String) {
        let parts = text.dropFirst().split(separator: " ", maxSplits: 1).map { $0.trimmingCharacters(in: .whitespaces).lowercased() }
        let cmd = parts.first ?? "", arg = parts.count > 1 ? parts[1] : ""

        switch (cmd, arg) {
        case ("exit", _), ("quit", _): NSApp.terminate(nil)
        case ("config", _): openConfig()
        case ("reload", _): reload(); notifier.post("Slate", "Config reloaded.")
        case ("update", _): updateNow()
        case ("version", _): notifier.post("Slate", "Version \(Updater.currentVersion)")
        case ("history", "clear"): history.clear(); notifier.post("Slate", "History cleared.")
        case ("autostart", "on"): setLoginItem(true)
        case ("autostart", "off"): setLoginItem(false)
        case ("help", _): notifier.post("Slate commands", ":config  :reload  :update  :version  :autostart on|off  :history clear  :exit")
        default: notifier.post("Slate", "Unknown command \"\(text)\". Try :help")
        }
    }

    // MARK: Updates

    private func scheduledUpdateCheck() {
        if config.checkForUpdates { checkForUpdate(manual: false) }
    }

    private func checkForUpdate(manual: Bool) {
        Task { @MainActor in
            do {
                update = try await Updater.check()
            } catch {
                Log.error("Checking for updates", error)
                if manual { notifier.post("Slate", "Couldn't check for updates. Are you online?") }
                return
            }
            updateItem.title = update.map { "Install Update \($0.version)" } ?? "Check for Updates…"

            guard let update else {
                if manual { notifier.post("Slate is up to date", "You have the latest version (\(Updater.currentVersion)).") }
                return
            }
            // Nag once per version, not every day.
            if manual || notifiedVersion != update.version {
                notifiedVersion = update.version
                notifier.post("Slate \(update.version) is available", "Click to update now. Your settings are kept.", isUpdate: true)
            }
        }
    }

    /// Installs the known update, checking first if none is known yet.
    private func updateNow() {
        guard !updating else { return }
        guard let update else {
            checkForUpdate(manual: true) // its notification offers the install
            return
        }
        guard Updater.appBundle != nil else {
            NSWorkspace.shared.open(update.pageURL)
            return
        }

        updating = true
        notifier.post("Updating Slate", "Downloading \(update.version)… Slate will restart by itself.")
        Task { @MainActor in
            do {
                try await Updater.install(update)
                NSApp.terminate(nil) // the helper swaps the app and relaunches it
            } catch {
                Log.error("Installing update", error)
                notifier.post("Slate update failed", error.localizedDescription)
                updating = false
            }
        }
    }
}
