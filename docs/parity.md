# Cross-platform parity

Handoff list between the Windows and Mac sessions (see CLAUDE.md, rule 1).
When you ship a user-facing change on one platform, add a row. When you port it, tick the other column.

| Feature | Windows | macOS | Notes |
|---|---|---|---|
| Bar, hotkey, focus verification, run in terminal | ✅ | ✅ | |
| Survives Win + D / Show Desktop | ✅ | ✅ | |
| History, Tab completion, live config | ✅ | ✅ | |
| Glow / shimmer / pop / run-flash animations | ✅ | ✅ | |
| Smooth custom caret | ✅ | ⬜ | Optional on macOS |
| Updater (one-click, SHA-256 verified) | ✅ | ✅ | |
| Launch at login | ✅ | ✅ | |
| Version tag in the bar's corner | ✅ | ✅ | |
| Themes menu (empty list) | ✅ | ✅ | Add each theme to both `Themes` files |
| Built-in shortcuts `cc` / `vc` / `z` + project finder | ✅ | ✅ | |
| Focus works when Start menu / Search is open | ✅ | n/a | Windows-only issue |
| Bordered menu-bar icon | n/a | ✅ | Windows has a tray icon |
| npm / winget packages | ✅ | n/a | Homebrew tap would be the Mac equivalent |

## Waiting to be ported

_Nothing yet._ Add items like: `- [ ] macOS: <feature> (shipped on Windows in vX.Y.Z, see <file>)`
