# Configuring Slate

Type `:config` in the bar to open `%APPDATA%\Slate\config.json`. Changes apply as soon as you save.
Comments (`//`) and trailing commas are allowed.

```jsonc
{
  "hotkey": "Win+Space",          // e.g. "Ctrl+Alt+Space", "Alt+`"
  "shell": "auto",                // auto | powershell | pwsh | full path
  "terminal": "wt",               // wt (new window) | wt-tab (tab in last window) | console
  "workingDirectory": "%USERPROFILE%",
  "historySize": 500,
  "checkForUpdates": true,        // check GitHub at startup and daily, offer one-click updates
  "theme": "",                    // last theme picked from the Themes menu
  "shortcuts": {                  // "<key> <folder>": find the folder, run the command in it
    "z": "",                      //   z slate   → terminal in the slate folder
    "cc": "claude",               //   cc slate  → Claude Code in it
    "vs": "code ."                //   vs slate  → VS Code in it
  },
  "useZoxide": true,              // use zoxide's ranking too, when it's installed
  "projectRoots": [],             // also treat subfolders of these as projects, e.g. ["D:\\code"]

  "appearance": {
    "width": 760, "height": 64,
    "monitor": 0,                 // 0 = primary, 1..n = specific monitor
    "anchor": "top",              // top | center | bottom
    "offsetX": 0, "offsetY": 48,  // updated when you drag the bar
    "cornerRadius": 18,
    "borderThickness": 1.5,
    "background": ["#2E1065", "#1E0B45", "#12062B"],  // gradient stops; one = solid
    "backgroundAngle": 0,
    "borderColor": "#7C4DFF",
    "borderHighlight": "#E9D5FF",
    "glowColor": "#8B5CF6",
    "glowSize": 30,
    "glowOpacity": 0.8,
    "textColor": "#F5F0FF",
    "placeholderColor": "#8B7BB3",
    "promptColor": "#C084FC",
    "prompt": "❯",
    "placeholder": "run anything...",
    "fontFamily": "Cascadia Code, Cascadia Mono, Consolas",
    "fontSize": 20,
    "showHint": true,
    "hintText": null,             // defaults to the hotkey
    "showVersion": true,          // tiny version tag under the hint
    "idleOpacity": 0.9            // opacity while sitting on the desktop
  },

  "animations": {
    "enabled": true,              // master switch
    "summonPop": true, "summonDurationMs": 220,
    "glowPulse": true, "glowPulseSeconds": 3.5,
    "borderShimmer": true, "borderShimmerSeconds": 6,
    "runFlash": true,
    "smoothCaret": true
  }
}
```

**Shell `auto`** uses PowerShell 7 (`pwsh`) if your profile is in `Documents\PowerShell` and `pwsh` is installed.
Otherwise it uses Windows PowerShell, so the shortcuts from your profile are always there.

**Preview a theme** without touching the running bar:

```powershell
Slate.exe --render-preview preview.png "cc animu"
```

Logs go to `%APPDATA%\Slate\slate.log`.

## Shortcuts

`cc slate`, `vs slate` and `z slate` work on any PC or Mac with no setup. Slate finds the folder itself:

1. A full path, if you type one.
2. [zoxide](https://github.com/ajeetdsouza/zoxide), if installed (best ranking, learns from every `cd`).
3. Folders you've opened through Slate before, most used first.
4. Subfolders of `projectRoots`.
5. Git repositories Slate finds on its own, plus the folders that contain them. It scans your home folder and
   every other fixed drive (on Windows) in the background at most every 6 hours, and keeps the list in `projects.json`.

Options after the folder name go to the command: `cc slate -r` runs `claude -r` in `slate`,
`cc slate -c` continues the last conversation, `vs slate --new-window` opens a new VS Code window,
and `cc -c` (no folder) works too. Anything from the first ` -` on counts as options.

Within each source, an exact folder name beats a prefix, which beats a partial match. Multi-word names work:
`vs new airlink`. Add your own: `"gh": "gh repo view --web"` makes `gh slate` open its GitHub page.
On macOS the default `vs` is `open -a 'Visual Studio Code' .`, which works without VS Code's `code` command.

Shortcuts take priority over functions with the same name in your shell profile.

## Themes

**Themes** in the tray / menu-bar menu applies a ready-made look by rewriting the `appearance` colors in your config:
**Violet** (the original), **Dark** and **Light**. Preview one without applying it:
`Slate.exe --render-preview out.png "vs slate" Light` (Windows).
Themes are defined in `windows/src/Slate/Themes.cs` (Windows) and `mac/Sources/Slate/Themes.swift` (macOS); add one entry to each list.

## Built-in commands

| Command | |
|---|---|
| `:config` | Open `config.json` |
| `:reload` | Reload the config |
| `:update` | Check for a new version and install it |
| `:version` | Show the installed version |
| `:autostart on` / `off` | Start with Windows |
| `:history clear` | Forget command history |
| `:help` | List these |
| `:exit` | Quit Slate |

## macOS

The config lives in `~/Library/Application Support/Slate/config.json` and has the same keys, with these differences:

| Key | macOS default | Notes |
|---|---|---|
| `hotkey` | `"Option+Space"` | Modifiers: `Cmd`, `Option`, `Ctrl`, `Shift`. Cmd + Space belongs to Spotlight unless you turn Spotlight's shortcut off in System Settings → Keyboard → Keyboard Shortcuts. Ctrl + Space is the input-source switcher. |
| `shell` | `"auto"` | Your login shell (`$SHELL`). Also `zsh`, `bash`, `fish` or a full path. Your `.zshrc` is loaded. |
| `terminal` | `"Terminal"` | Any app that opens `.command` files, e.g. `"iTerm"`. Falls back to Terminal. |
| `workingDirectory` | `"~"` | |
| `launchAtLogin` | `true` | Start Slate when you log in. Slate re-registers itself on each start until it sticks; the menu-bar toggle updates this. |
| `projectRoots` | `["~/projects", "~/Developer", "~/code"]` | Missing folders are skipped. |
| `appearance.fontFamily` | `"SF Mono, Menlo"` | |
| `appearance.monitor` | `0` | 0 = the display with the menu bar. |
| `animations.smoothCaret` | – | Windows only. |

The **❯** in the menu bar has Show Slate, Open Config, Reload Config, Themes, Launch at Login, Check for Updates and Quit.
If macOS doesn't allow Slate's notifications, messages (like "update available") appear briefly in the bar itself; click it to act on them.

The bar never needs the Accessibility permission. The hotkey uses the system's hotkey API, and commands run through `.command` scripts that your terminal opens.

## Updates

Slate checks GitHub for a newer release 30 seconds after it starts, then once a day.
When one exists, a notification offers it, and the tray menu shows **Install update x.y.z**.
Clicking it downloads `SlateSetup.exe`, verifies its SHA-256 checksum against the one GitHub publishes,
installs it silently and restarts Slate. Settings and history are kept.
Portable (zip) copies open the download page instead.
On macOS the same check downloads `Slate-macOS.zip`, verifies it, swaps the app in place and relaunches it.

## How it works

- **Hotkey:** a low-level keyboard hook, because Windows reserves Win + Space and `RegisterHotKey` can't take it.
  The hook taps an unassigned key so releasing Win doesn't open Start.
- **Win + D:** while idle, the bar's owner is the shell's desktop window, the same trick Rainmeter uses.
  When summoned it detaches, goes topmost and takes focus, then re-attaches when it's dismissed.
- **Running commands:** `wt -w new new-tab <shell> -NoExit -EncodedCommand <base64>`.
  Base64 UTF-16 means there's no quoting layer to break.

## Building

Requires the .NET 8 SDK. The installer also needs [Inno Setup 6](https://jrsoftware.org/isinfo.php).

```powershell
dotnet build windows\src\Slate\Slate.csproj                                # debug build
powershell -ExecutionPolicy Bypass -File windows\tools\publish.ps1   # exe, zip and SlateSetup.exe in windows\publish\
```

**macOS:** needs Xcode (or the Command Line Tools with Swift 5.9+).

```bash
cd mac && swift build && .build/debug/Slate     # debug run
./mac/build.sh                                  # universal Slate.app + mac/publish/Slate-macOS.zip
swift mac/make-icon.swift                       # regenerate the icon
```

The version comes from the `VERSION` file at the repo root, so both platforms always ship the same number.

**Releasing:** bump `VERSION`, commit, then `git tag v1.0.1; git push origin main v1.0.1`.
GitHub Actions builds everything on a clean runner and publishes the release.
With the repo secrets `NPM_TOKEN` and `WINGET_TOKEN` set, it also publishes to npm and opens the winget update PR.
The winget manifests for the first version are in `windows/winget/` for reference.
