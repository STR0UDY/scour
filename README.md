# Scour

A free, open-source Windows cleaner: the tool CCleaner used to be, minus the bloat. It reclaims
disk space and never deletes anything you haven't seen first.

> **Status: Milestone 1** — the engine and the CLI. The WinUI 3 app, elevated helper, disk
> analyser, startup manager and uninstaller come in later milestones.

## Principles

- **Preview before delete.** Every clean is a scan first. There is no blind wipe.
- **Nothing hidden.** No telemetry, ads, account or background service.
- **Rules are data.** Every cleanable location is a JSON file under [`rules/`](rules).
- **Safe by default.** Cookies, history and sessions are off until you ask for them.
- **Least privilege.** Runs as you. Rules that need admin are skipped unless you're already elevated.

## Using the CLI

```bash
dotnet build
```

```bash
src/Scour.Cli/bin/Debug/net10.0-windows/scour.exe scan --largest 3
```

| Command | What it does |
| --- | --- |
| `scour scan` | Shows what the default-on rules would remove. Changes nothing. |
| `scour scan --all` | Includes rules that are off by default (cookies, history, dev caches). |
| `scour scan -r browser.chrome.* -c Apps` | Picks rules by id (with `.*` families) or category. |
| `scour scan --json [--list-files]` | Machine-readable output, for scripts and snapshot tests. |
| `scour clean --dry-run` | Scans, then re-verifies every file as if deleting, and deletes nothing. |
| `scour clean` | Scans, shows the preview and asks `[y/N]` before deleting. |
| `scour clean --recycle` | Sends files to the Recycle Bin instead. |
| `scour clean --yes` | Skips the prompt (for Task Scheduler). Never implied. |
| `scour rules list` / `scour rules lint` | Lists rules, or validates a rules folder (CI uses this). |

Rules whose app is running are skipped by `clean` unless you pass `--ignore-running`. Every real
clean writes a log to `%LOCALAPPDATA%\Scour\logs\clean-*.jsonl` (time, rule id, path, size and
outcome for each file).

## How a clean stays safe

1. Expand the rule's targets (env vars, `{chromiumProfile}` / `{firefoxProfile}`, `*` folders).
2. Enumerate files, skipping reparse points, excludes and files newer than `minAgeHours`.
3. Check every target folder and every file against the [deny-list](src/Scour.Core/Safety/DenyList.cs).
4. Show the results.
5. On clean, open each file **without following links** and check that its size, timestamp and the
   handle's real path still match what was shown and that the path is still allowed.
6. Delete through that same handle (or send it to the Recycle Bin).
7. Log the outcome. Locked files are skipped and counted, never forced.

The deny-list can't be overridden by rules. It covers drive roots, `C:\Windows` itself,
`System32`, `SysWOW64`, `WinSxS`, the `Program Files` and `ProgramData` roots, every user profile
and `AppData` root, and the whole of Desktop, Documents, Pictures, Music, Videos, Downloads and
OneDrive.

## Writing a rule

Adding support for an app is one JSON file in the right category folder, named after its id:

```json
{
  "$schema": "../rule.schema.json",
  "id": "app.discord.cache",
  "name": "Discord cache",
  "description": "Cached images, scripts and media.",
  "category": "Apps",
  "safety": "safe",
  "defaultEnabled": true,
  "elevation": false,
  "detect": ["%APPDATA%\\discord"],
  "runningProcesses": ["Discord.exe"],
  "targets": [
    { "path": "%APPDATA%\\discord\\Cache", "pattern": "*", "recurse": true }
  ],
  "exclude": [],
  "minAgeHours": 0
}
```

- Paths must start with an environment variable. Absolute paths are rejected.
- `{chromiumProfile}` matches `Default`, `Profile N` and `Guest Profile`. `{firefoxProfile}`
  matches every Firefox profile folder. `*` and `?` work in folder names too.
- `pattern` matches file names. `exclude` holds full-path globs and may start with `*`.
- Anything that loses user data (cookies, history, sessions) is `caution` and off by default.
- Rules that touch `%WINDIR%` or `%ProgramData%` need `"elevation": true`.

Run `scour rules lint rules` and `dotnet test` before opening a pull request. The rule tests also
point every variable in your rule at protected folders and check that the deny-list refuses it.

## Layout

```
src/Scour.Core/     engine: rule model, loader, scanner, cleaner, deny-list (no UI)
src/Scour.Cli/      System.CommandLine front end
rules/              built-in rules, one file each, plus rule.schema.json
tests/              engine sandbox tests, deny-list tests, rule lint
```

## Not in Milestone 1

- Recycle Bin rule: it needs a shell-based handler (per-drive `$Recycle.Bin`, "older than N days")
  rather than a path rule.
- `node_modules` / `bin` / `obj` finder under user-chosen roots: it needs settings.
- The elevated helper (`Scour.Elevator`, `Scour.Ipc`). Admin rules work today only from an
  elevated prompt.
- Offering to close a running app. The CLI only skips it.
- `scour analyse`, winapp2.ini import, profiles and exclusion settings.
