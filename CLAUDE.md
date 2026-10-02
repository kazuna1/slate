# Slate

An always-on command bar for the desktop: press a hotkey, type a command, it runs in a terminal.
Two native apps in one repo, one folder per platform.

## Layout

```
windows/                C# / WPF on .NET 8
  src/Slate/            the app
  installer/Slate.iss   Inno Setup installer → SlateSetup.exe
  tools/publish.ps1     single-file exe + zip + installer → windows/publish/
  tools/make-icon.ps1   regenerates src/Slate/Assets/slate.ico
  npm/                  npm package `kazuna-slate` (downloads SlateSetup.exe)
  winget/               winget manifests (kazuna1.Slate)
mac/                    Swift / AppKit, Swift Package
  Sources/Slate/        the app
  Resources/            Info.plist template, Slate.icns
  build.sh              universal ad-hoc-signed Slate.app + zip → mac/publish/
  make-icon.swift       regenerates Resources/Slate.icns
VERSION                 the one version number, read by both builds
README.md               GitHub landing page
docs/config.md          user docs: config reference, shortcuts, themes, building
install.ps1, install.sh one-line installers (their URLs are public: don't move them)
.github/workflows/      release.yml builds both platforms into one GitHub release on a v* tag
```

Windows code goes in `windows/`, Mac code in `mac/`. The two apps mirror each other with the same
names (`Bar`/`MainWindow`, `Projects`, `Completer`, `CommandRunner`, `Updater`, `Themes`, `History`)
and the same `config.json` keys. When a user-facing feature changes on one platform, mention it so
the other platform can get it too.

## Build

- Windows: `dotnet build windows\src\Slate\Slate.csproj`, full release build `windows\tools\publish.ps1`
- macOS: `cd mac && swift build`, full release build `mac/build.sh`

## Release

1. `git pull --rebase`, bump `VERSION`, commit, push `main`.
2. Test build first: `gh workflow run release.yml --ref main`, wait for both jobs. It's the only way to
   compile the platform you're not on.
3. `git tag -a vX.Y.Z -m "Slate X.Y.Z"`, check it points at your commit (`git rev-list -n1 vX.Y.Z`), push the tag.
4. The release gets `SlateSetup.exe`, `Slate-X.Y.Z-win-x64.zip` and `Slate-macOS.zip`; installed apps on both platforms offer the update.

## Lessons

- Verify every `git commit` succeeded before tagging (a failed commit once put a tag on old code).
  It happened twice: run release steps under `set -e` (or one by one), never as a `;`-joined chain where a
  failed `git pull`/`git commit` lets the tag step continue. Check `git rev-list -n1 vX.Y.Z` equals `HEAD` before pushing the tag.
- Windows PowerShell 5: never `Get-Content x | Set-Content x` (it emptied a file), `-Encoding utf8` adds a BOM
  (breaks `package.json`), multi-line `git commit -m` can fail silently; use `git commit -F <file>`.
- The Windows checkout uses CRLF, so scripted edits matching `\n` miss. Prefer the Edit tool.
- macOS can't show Slate's notifications (not notarized); messages fall back to the bar itself.
- The bar must never look focused while keys go elsewhere; focus is always verified after summoning.
- Don't send keystrokes or move windows on the user's desktop to test; ask the user to try it.

## Open items

- winget: PR microsoft/winget-pkgs#444849 needs the user's CLA comment.
- npm: package ready in `windows/npm`; needs `npm login` + `npm publish` (or an `NPM_TOKEN` secret).
- macOS notarization (removes the Gatekeeper prompt) needs a paid Apple Developer account.
- Themes menu exists on both platforms; the list is empty. Add themes to both `Themes` files.
