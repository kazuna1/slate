import Foundation

/// A named look. Picking one from the menu applies it to `appearance` and saves the config.
struct Theme {
    let name: String
    let apply: (inout SlateConfig.Appearance) -> Void
}

enum Themes {
    /// Add themes here, one entry each; the Themes menu lists them automatically.
    /// Keep this list in the same order as windows/src/Slate/Themes.cs.
    static let all: [Theme] = [
        // Theme(name: "Midnight") { a in
        //     a.background = ["#0F172A", "#020617"]
        //     a.glowColor = "#38BDF8"
        // },
    ]
}
