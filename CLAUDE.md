# Slate

## RULE 1: one platform per machine, one session per platform

Slate is developed as two parallel tracks, each in its own Claude Code session on its own machine:

| Session | Machine | Owns | Builds and tests |
|---|---|---|---|
| **Windows** | the Windows PC | `windows/` | `dotnet build`, `windows/tools/publish.ps1`, real Win + Space / Win + D |
| **Mac** | the Mac | `mac/` | `swift build`, `mac/build.sh`, real ⌥ Space / Show Desktop |

- **Find out which session you are from the platform** (`win32` or `darwin`) and stay in your lane.
  Never edit the other platform's folder. If something must change there, add it to `docs/parity.md` instead.
- **Shared files** (everything outside `windows/` and `mac/`) may be edited by either session, but always
  `git pull --rebase` first and keep the edit small, because the other session may be editing them too.
- A new user-facing feature on one platform is not done until it has a row in `docs/parity.md`, so the
  other session can port it. When you port one, tick it off there.
- **Start every session with `git pull --rebase`** and read `docs/parity.md` for work waiting for you.
- `main` must always be releasable: only push when your platform builds and you've tried the change for real.

## Layout

```
VERSION                 the single version number for both platforms (read by both builds)
README.md, LICENSE      public face of the repo (README is the GitHub landing page)
docs/config.md          user docs: config reference, shortcuts, themes, building, releasing
docs/parity.md          cross-platform handoff list (features to port, by platform)
install.ps1             Windows one-liner installer   (URL is public; do not move)
install.sh              macOS one-liner installer     (URL is public; do not move)
.github/workflows/      release.yml builds BOTH platforms on a v* tag into one GitHub release
.github/release-notes.md
windows/
  src/Slate/            C# / WPF on .NET 8 (the app)
  installer/Slate.iss   Inno Setup installer → SlateSetup.exe
  tools/publish.ps1     single-file exe + zip + installer → windows/publish/
  tools/make-icon.ps1   regenerates src/Slate/Assets/slate.ico
  npm/                  npm package `kazuna-slate` (downloads SlateSetup.exe)
  winget/               winget manifests (kazuna1.Slate)
mac/
  Sources/Slate/        Swift / AppKit (the app), Swift Package
  Resources/            Info.plist template (version filled from VERSION), Slate.icns
  build.sh              universal ad-hoc-signed Slate.app + zip → mac/publish/
  make-icon.swift       regenerates Resources/Slate.icns
```

Mirrored concepts keep the same names on both sides: `Bar`/`MainWindow`, `Projects`, `Completer`,
`CommandRunner`, `Updater`, `Themes`, `History`, config keys in `config.json`.

## Releasing (either session, one at a time)

1. `git pull --rebase`. Make sure the other platform isn't mid-change on `main` (check `docs/parity.md` / ask the user).
2. Bump `VERSION` (semver). Commit.
3. Push `main`, then run a **test build first**: `gh workflow run release.yml --ref main`. Wait for both jobs to pass.
   This is the only way to compile the other platform, so never skip it.
4. Tag and push: `git tag -a vX.Y.Z -m "Slate X.Y.Z" && git push origin vX.Y.Z`.
   **Check the tag points at your commit** (`git rev-list -n1 vX.Y.Z`) before pushing it.
5. The workflow builds Windows, then macOS, and publishes one release with `SlateSetup.exe`,
   `Slate-X.Y.Z-win-x64.zip` and `Slate-macOS.zip`. Installed apps on both platforms offer the update.

Every release reaches users of both platforms, even if only one platform changed.

## Hard-won lessons

- Verify every `git commit` succeeded before tagging (a failed commit once put a tag on old code and published a wrong release).
- Windows PowerShell 5: never pipe `Get-Content x | Set-Content x` (it truncated a file once), `Set-Content -Encoding utf8`
  adds a BOM (breaks `package.json`), and multi-line `git commit -m` strings can silently fail; use `git commit -F <file>`.
- Windows checkout uses CRLF; scripted edits that match `\n` will miss. Prefer the Edit tool.
- macOS: Slate can't post system notifications (not notarized), so messages fall back to the bar itself.
- The bar must never look focused while keys go elsewhere; focus is always verified after summoning.
- Never send keystrokes or move windows on the user's desktop to test; ask the user to test interactive behaviour.

## Open items

- winget: PR microsoft/winget-pkgs#444849 needs the user's CLA comment.
- npm: package ready in `windows/npm`; needs `npm login` + `npm publish` by the user (or an `NPM_TOKEN` secret).
- macOS notarization (removes the Gatekeeper prompt) needs a paid Apple Developer account.
- Themes menu exists on both platforms but the list is empty; add themes to both `Themes` files.
