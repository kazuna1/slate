# Slate

**PowerShell in another skin.** A command bar that lives on your Windows desktop.
Press **Win + Space** anywhere, type a command, hit Enter. It runs in a terminal that stays open.

![Slate](docs/slate.png)

<p align="center">
  <a href="https://github.com/kazuna1/slate/releases/latest/download/SlateSetup.exe">
    <img src="https://img.shields.io/badge/Download_for_Windows-SlateSetup.exe-2ea44f?style=for-the-badge&logo=windows&logoColor=white" alt="Download Slate for Windows">
  </a>
</p>

## Features

- **Always there.** Sits on your desktop and stays put through Win + D.
- **Instant.** Win + Space from any app, and you're typing straight into the bar.
- **Runs anything.** Any PowerShell command, with your profile loaded, so your own shortcuts work.
- **Smart.** Tab completes folder names (via [zoxide](https://github.com/ajeetdsouza/zoxide)), and ↑ / ↓ brings back your history.
- **Yours.** Colors, size, glow, font, hotkey and animations are all configurable, and changes apply live.

## Install

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

Or click the download button above. No admin rights and no .NET install needed.
If Windows says *"Windows protected your PC"*, click **More info → Run anyway** (the app isn't code-signed yet).

## Use

| Key | Action |
|---|---|
| **Win + Space** | Open / close the bar |
| **Enter** | Run the command in a terminal |
| **Tab** | Complete folder names |
| **↑ / ↓** | Command history |
| **Esc** | Cancel |

Type `:config` in the bar to customize it, or `:help` for the other built-in commands.
The full reference is in [docs/config.md](docs/config.md).

**Uninstall:** Settings → Apps → Installed apps → Slate.

<sub>Windows 10/11 · x64 · [MIT](LICENSE)</sub>
