<p align="center"><img src="docs/assets/icon.png" width="96" alt=""></p>

<h1 align="center">Scour</h1>

<p align="center">A free, open-source Windows cleaner: the tool CCleaner used to be, minus the bloat.<br>
It reclaims disk space, shows where the space went and tidies startup, and it never deletes anything you haven't seen first.</p>

---

## What it does

| | |
| --- | --- |
| **Clean** | Temp files, Windows Update and Delivery Optimisation caches, crash dumps, error reports, old logs, thumbnail and icon caches, GPU shader caches, browser caches (Chrome, Edge, Brave, Opera, Firefox), app caches (Discord, Spotify, Teams, Slack, VS Code), launcher caches (Steam, Epic, Battle.net, FiveM), dev caches (npm, Yarn, NuGet, pip, Gradle, Cargo) and the Recycle Bin. |
| **Analyse** | A treemap of any drive or folder. With admin it reads the NTFS master file table directly (seconds for a whole drive); without, it walks folders. Drill down, see size on disk, break it down by file type, open in Explorer, move to the Recycle Bin. |
| **Startup** | Run keys, Startup folders, logon/boot scheduled tasks and auto-start services, with publisher and signature checks and broken entries flagged. Switch them off the way Task Manager does, so it's reversible. |
| **Apps** | Every Win32 and Store app. Runs the app's own uninstaller, then finds what it left behind and shows you before removing anything. |
| **CLI** | `scour scan`, `scour clean --dry-run`, `scour analyse C:` and more, on the same engine. |

## Principles

- **Preview before delete.** Every clean is a scan first, with paths and sizes. There is no blind one-click wipe.
- **Nothing hidden.** No telemetry, no ads, no account, no tray icon, no background service. Update checks are opt-in.
- **Rules are data.** Every cleanable location is a JSON rule in [`rules/`](rules) that anyone can read and audit.
- **Safe by default.** Cookies, history, saved sessions and the Recycle Bin are off until you tick them.
- **Least privilege.** The app runs as you. A tiny helper is elevated only when you tick something that needs it.

Not included, on purpose: a registry cleaner, RAM "optimisers", driver updaters, "PC health" scores, Prefetch cleaning and secure wipe. None of them make Windows measurably better.

## Install

Download the MSI for your PC (x64 or ARM64) from [Releases](https://github.com/Mat2k/scour/releases), or:

```bash
winget install Scour.Scour
```

The installer adds Scour to the Start menu and puts `scour` on your PATH. A portable zip is also on the releases page.

## Using the CLI

| Command | What it does |
| --- | --- |
| `scour scan [--profile quick\|deep\|dev] [--all] [--json]` | What would be removed. Changes nothing. |
| `scour clean --dry-run` | Re-verifies every file as if deleting, deletes nothing. |
| `scour clean [--recycle] [--yes] [--elevate]` | Shows the preview, asks `[y/N]`, then cleans. `--elevate` runs admin rules via the helper. |
| `scour analyse C: [--types]` | Largest folders and files, and space by file type. |
| `scour startup list` / `enable` / `disable <id>` | Startup items. |
| `scour apps list` / `uninstall <name> [--remove-leftovers]` | Installed apps. |
| `scour rules list` / `lint` / `import-winapp2 <file>` | Rules and rule packs. |

Rules can be picked by id (`-r browser.chrome.*`), category (`-c Browsers`) or profile (`--profile quick`).

## How a clean stays safe

1. Rule targets are expanded (env vars, browser profiles, wildcard folders).
2. Files are listed, never following links or junctions, skipping excludes and anything newer than the rule's age limit.
3. Every target folder and file is checked against a hard deny-list rules can't override: drive roots, `C:\Windows` itself, `System32`, `WinSxS`, the `Program Files` and profile roots, and the whole of Documents, Desktop, Pictures, Music, Videos, Downloads and OneDrive.
4. You see the results.
5. On Clean, each file is reopened without following links, and its size, timestamp and *real* path must still match what you saw and still pass the deny-list.
6. It's deleted through that same handle (or sent to the Recycle Bin). Locked files are skipped and counted, never forced.
7. Every outcome is logged to `%LOCALAPPDATA%\Scour\logs`.

Admin rules go through `Scour.Elevator`. It's reachable only by the Scour process that started it, accepts rule ids rather than paths, rescans those rules itself and deletes only files that both its scan and yours found. See [SECURITY.md](SECURITY.md).

## Building

Requires Windows 10 22H2+ and the .NET 10 SDK.

```bash
dotnet build
```

```bash
dotnet test
```

```bash
./build/publish.ps1
```

`publish.ps1` produces self-contained x64 and ARM64 folders, zips and MSIs in `artifacts/`. Set the signing variables described at the top of the script to sign them.

```
src/Scour.Core/      engine: rules, scanner, cleaner, deny-list, analyser, startup, apps (no UI)
src/Scour.App/       WinUI 3 app (unpackaged)
src/Scour.Cli/       System.CommandLine front end
src/Scour.Elevator/  admin helper, named-pipe server
src/Scour.Ipc/       message contracts shared by Core and the helper
rules/               built-in JSON rules
installer/           WiX MSI
packaging/winget/    winget manifest templates
tests/               engine sandbox tests, deny-list tests, rule lint
```

## Contributing

Adding support for an app is usually one JSON file. See [CONTRIBUTING.md](CONTRIBUTING.md).

## Licence

[GPL-3.0](LICENSE). GPL makes sure nobody can repackage Scour with adware and keep the changes closed.
