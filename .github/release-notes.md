## Install on Windows

Download **SlateSetup.exe** below and run it. No admin rights and no .NET install needed.

Windows may show **"Windows protected your PC"** because the installer isn't code-signed.
Click **More info → Run anyway**.

After installing, press **Win + Space** anywhere to summon the bar.

Prefer no installer? Download `Slate-<version>-win-x64.zip`, unzip it, and run `Slate.exe`.

## Install on macOS

```bash
curl -fsSL https://raw.githubusercontent.com/kazuna1/slate/main/install.sh | sh
```

Or download **Slate-macOS.zip**, unzip, and move Slate to Applications. The app isn't notarized yet, so the first
launch needs **System Settings → Privacy & Security → Open Anyway**. Then press **⌥ Space**.

## What's in Slate

- Always-on command bar on the desktop that survives Win + D
- Win + Space (⌥ Space on macOS) to summon, Enter runs the command in a terminal that stays open
- Your shell profile is loaded (PowerShell on Windows, zsh on macOS), so your own shortcuts work
- Finds any folder by name (no zoxide needed), Tab completion, ↑/↓ history
- Fully themeable via `config.json` (live reload), with glow and animations
