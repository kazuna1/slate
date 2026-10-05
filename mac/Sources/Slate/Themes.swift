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
            a.decoration = ""
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
            a.decoration = ""
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
            a.decoration = ""
        },
        Theme(name: "Forest") { a in
            a.background = ["#123321", "#0C2617", "#07170E"]
            a.borderColor = "#3F8F4F"
            a.borderHighlight = "#BBF7D0"
            a.glowColor = "#22C55E"
            a.glowOpacity = 0.55
            a.textColor = "#ECFDF3"
            a.placeholderColor = "#7FA88C"
            a.promptColor = "#6EE787"
            a.decoration = "vines"
        },
        Theme(name: "Dune") { a in
            a.background = ["#5C3418", "#3A200E", "#1E1007"]
            a.borderColor = "#C98A4B"
            a.borderHighlight = "#FFD8A8"
            a.glowColor = "#E8913A"
            a.glowOpacity = 0.55
            a.textColor = "#FFF1E0"
            a.placeholderColor = "#B98B66"
            a.promptColor = "#F4A259"
            a.decoration = "dunes"
        },
        Theme(name: "Galaxy") { a in
            a.background = ["#140F3A", "#0B0B2A", "#05050F"]
            a.borderColor = "#6D5BD0"
            a.borderHighlight = "#E0D7FF"
            a.glowColor = "#7C3AED"
            a.glowOpacity = 0.6
            a.textColor = "#F5F3FF"
            a.placeholderColor = "#8A84B8"
            a.promptColor = "#A78BFA"
            a.decoration = "stars"
        },
    ]
}
