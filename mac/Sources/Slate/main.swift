import AppKit

let app = NSApplication.shared
let args = CommandLine.arguments

if args.count >= 3, args[1] == "--render-preview" {
    // Writes a PNG of the bar with the current config, then exits. Doesn't touch a running Slate.
    app.setActivationPolicy(.prohibited)
    var config = SlateConfig()
    if FileManager.default.fileExists(atPath: Paths.config.path), let loaded = try? ConfigStore().load() { config = loaded }
    let hotkey = (try? Hotkey.parse(config.hotkey)) ?? (try! Hotkey.parse(Hotkey.defaultText))
    let bar = Bar(config: config, history: History(maxSize: 1), hotkeyDisplay: hotkey.display)
    do {
        try bar.renderPreview(to: args[2], text: args.count >= 4 ? args[3] : "code slate")
        exit(0)
    } catch {
        FileHandle.standardError.write(Data("\(error.localizedDescription)\n".utf8))
        exit(1)
    }
}

if args.count >= 3, args[1] == "--render-gallery" {
    app.setActivationPolicy(.prohibited)
    var config = SlateConfig()
    config.theme = "Violet"
    let hotkey = (try? Hotkey.parse(config.hotkey)) ?? (try! Hotkey.parse(Hotkey.defaultText))
    do {
        try ThemePicker.render(config: config, hotkeyDisplay: hotkey.display, to: args[2])
        exit(0)
    } catch {
        FileHandle.standardError.write(Data("\(error.localizedDescription)\n".utf8))
        exit(1)
    }
}

let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.accessory) // menu-bar app: no Dock icon
app.run()
