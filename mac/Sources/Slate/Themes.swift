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
        // The original look; picking it restores the defaults.
        Theme(name: "Violet") { a in
            let d = SlateConfig.Appearance()
            a.background = d.background
            a.borderColor = d.borderColor
            a.borderHighlight = d.borderHighlight
            a.glowColor = d.glowColor
            a.glowOpacity = d.glowOpacity
            a.textColor = d.textColor
            a.placeholderColor = d.placeholderColor
            a.promptColor = d.promptColor
        },
        Theme(name: "Dark") { a in
            a.background = ["#1C1C1F", "#111113", "#09090B"]
            a.borderColor = "#3F3F46"
            a.borderHighlight = "#D4D4D8"
            a.glowColor = "#000000"
            a.glowOpacity = 0.75
            a.textColor = "#FAFAFA"
            a.placeholderColor = "#71717A"
            a.promptColor = "#E4E4E7"
        },
        Theme(name: "Light") { a in
            a.background = ["#FFFFFF", "#F7F7F8", "#F0F0F2"]
            a.borderColor = "#D4D4D8"
            a.borderHighlight = "#71717A"
            a.glowColor = "#000000"
            a.glowOpacity = 0.22
            a.textColor = "#18181B"
            a.placeholderColor = "#8A8A93"
            a.promptColor = "#3F3F46"
        },
    ]
}
