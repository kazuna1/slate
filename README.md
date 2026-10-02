# Slate

**Your shell in another skin.** A command bar that lives on your desktop, on Windows and macOS.
Press **Win + Space** (or **⌥ Space** on a Mac) anywhere, type a command, hit Enter. It runs in a terminal that stays open.

![Slate](docs/slate.png)

<p align="center">
  <a href="https://github.com/kazuna1/slate/releases/latest/download/SlateSetup.exe">
    <img src="https://img.shields.io/badge/Download_for_Windows-SlateSetup.exe-2ea44f?style=for-the-badge&logo=windows&logoColor=white" alt="Download Slate for Windows">
  </a>
  &nbsp;
  <a href="https://github.com/kazuna1/slate/releases/latest/download/Slate-macOS.zip">
    <img src="https://img.shields.io/badge/Download_for_macOS-Slate--macOS.zip-2ea44f?style=for-the-badge&logo=apple&logoColor=white" alt="Download Slate for macOS">
  </a>
</p>

## Features

- **Always there.** Sits on your desktop and stays put through Win + D / Show Desktop.
- **Instant.** One hotkey from any app, and you're typing straight into the bar.
- **Runs anything.** Any command in your own shell (PowerShell or zsh), with your profile loaded, so your shortcuts work.
- **Smart.** Tab completes folder names (via [zoxide](https://github.com/ajeetdsouza/zoxide) and your project folders), and ↑ / ↓ brings back your history.
- **Yours.** Colors, size, glow, font, hotkey and animations are all configurable, and changes apply live.
- **Up to date.** Tells you when a new version is out and updates itself in one click.

## Install on Windows

**PowerShell**

```powershell
irm https://raw.githubusercontent.com/kazuna1/slate/main/install.ps1 | iex
```

**npm**

```bash
npm install -g kazuna-slate
```

**winget**

```powershell
winget install kazuna1.Slate
```

Or click the Windows button above. No admin rights and no .NET install needed.
If Windows says *"Windows protected your PC"*, click **More info → Run anyway** (the app isn't code-signed yet).

## Install on macOS

**Terminal**

```bash
curl -fsSL https://raw.githubusercontent.com/kazuna1/slate/main/install.sh | sh
```

This puts Slate in Applications and starts it. Run it again later to update.

Or click the macOS button above, unzip, and move **Slate** to Applications. Because the app isn't notarized yet,
macOS blocks the first launch: open **System Settings → Privacy & Security** and click **Open Anyway**.
The Terminal install above skips this.

macOS 13 or later, Apple Silicon and Intel.

## Use

| Windows | macOS | Action |
|---|---|---|
| **Win + Space** | **⌥ Space** | Open / close the bar |
| **Enter** | **Return** | Run the command in a terminal |
| **Tab** | **Tab** | Complete folder names |
| **↑ / ↓** | **↑ / ↓** | Command history |
| **Esc** | **Esc** | Cancel |

**Built-in shortcuts** work on any machine with no setup. Slate finds the folder by name:

| Type | Does |
|---|---|
| `cc slate` | Claude Code in your `slate` folder |
| `vc slate` | VS Code in it |
| `z slate` | A terminal in it |

Type `:config` in the bar to customize it, or `:help` for the other built-in commands.
The full reference is in [docs/config.md](docs/config.md).

**Uninstall:** on Windows, Settings → Apps → Installed apps → Slate.
On macOS, quit Slate from the **❯** menu-bar icon and move it to the Trash.

<sub>Windows 10/11 · macOS 13+ · [MIT](LICENSE)</sub>
