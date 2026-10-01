# Slate

**PowerShell in another skin.** An always-visible command bar on your Windows desktop.
Press **Win + Space** from anywhere, type a command, hit Enter, and it runs in a new terminal that stays open.

![Slate](docs/slate.png)

- Lives on the desktop and **survives Win + D**.
- **Win + Space** brings it to the front with the keyboard focus in the bar. If focus can't be taken, the bar backs off instead of letting your keys go somewhere else.
- Runs **anything** PowerShell runs, with your profile loaded, so your own shortcuts work (`cc animu`).
  Quotes and special characters are passed through exactly as typed.
- **Tab completion** of folder names from [zoxide](https://github.com/ajeetdsouza/zoxide), ranked by how much you use them.
- **↑ / ↓ history**, drag to move, a dark purple glow, gentle animations, and everything is configurable.

## Install

1. Download `Slate-<version>-win-x64.zip` from Releases and unzip `Slate.exe` anywhere (e.g. `%LOCALAPPDATA%\Programs\Slate`).
   No .NET install needed.
2. Run it. A `❯` icon appears in the tray.
3. Right-click the tray icon → **Run at startup**, or type `:autostart on` in the bar.

Requires Windows 10/11. Windows Terminal is used if installed; otherwise a classic console window opens.

## Using it

| Key | Action |
|---|---|
| **Win + Space** | Summon the bar (press again to dismiss) |
| **Enter** | Run the command in a new terminal |
| **Esc** | Clear and go back to the window you were in |
| Click elsewhere | Dismiss, keeping what you typed |
| **↑ / ↓** | Command history |
| **Tab / Shift+Tab** | Cycle folder-name completions for the current word |
| **→ / End** | Accept the grey completion preview |
| Drag the frame | Move the bar (position is saved) |

Built-in commands:

| Command | |
|---|---|
| `:config` | Open `config.json` |
| `:reload` | Reload the config (it also reloads automatically when you save it) |
| `:autostart on` / `off` | Start with Windows |
| `:history clear` | Forget command history |
| `:help` | List these |
| `:exit` | Quit Slate |

## Configuration

`%APPDATA%\Slate\config.json` is created on first run. Changes apply as soon as you save. Comments (`//`) and trailing commas are allowed.

```jsonc
{
  "hotkey": "Win+Space",          // e.g. "Ctrl+Alt+Space", "Alt+`"
  "shell": "auto",                // auto | powershell | pwsh | full path
  "terminal": "wt",               // wt (new window) | wt-tab (tab in last window) | console
  "workingDirectory": "%USERPROFILE%",
  "historySize": 500,
  "useZoxide": true,              // completion source: zoxide's ranked folders
  "projectRoots": [],             // completion source: subfolders of these, e.g. ["D:\\code"]

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

## How it works

- **Hotkey:** a low-level keyboard hook, because Windows reserves Win + Space and `RegisterHotKey` can't take it.
  The hook taps an unassigned key so releasing Win doesn't open Start.
- **Win + D:** while idle, the bar's owner is the shell's desktop window, the same trick Rainmeter uses.
  When summoned it detaches, goes topmost and takes focus, then re-attaches when it's dismissed.
- **Running commands:** `wt -w new new-tab <shell> -NoExit -EncodedCommand <base64>`.
  Base64 UTF-16 means there's no quoting layer to break.

## Building

Requires the .NET 8 SDK.

```powershell
dotnet build src\Slate\Slate.csproj                                       # debug build
powershell -ExecutionPolicy Bypass -File tools\publish.ps1                # single-file release exe + zip in publish\
powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1              # regenerate the icon
```

Stack: C# / WPF on .NET 8, with a WinForms tray icon. The project started as "Glint".
