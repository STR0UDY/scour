# Contributing to Scour

Thanks for helping. The most useful contribution is usually a new rule, and that's one JSON file with no C#.

## Adding a rule

Put the file in the folder for its category, named after its id, e.g. `rules/apps/app.obsidian.cache.json`:

```json
{
  "$schema": "../rule.schema.json",
  "id": "app.obsidian.cache",
  "name": "Obsidian cache",
  "description": "Cached web content. Notes and settings are not touched.",
  "category": "Apps",
  "safety": "safe",
  "defaultEnabled": true,
  "elevation": false,
  "detect": ["%APPDATA%\\obsidian"],
  "runningProcesses": ["Obsidian.exe"],
  "targets": [
    { "path": "%APPDATA%\\obsidian\\Cache", "pattern": "*", "recurse": true },
    { "path": "%APPDATA%\\obsidian\\GPUCache", "pattern": "*", "recurse": true }
  ],
  "exclude": [],
  "minAgeHours": 0
}
```

| Field | Meaning |
| --- | --- |
| `id` | Stable, dotted, lower-case. Used in settings, logs and the CLI. Never rename a shipped id. |
| `category` | `System`, `Browsers`, `Apps`, `Games` or `Dev`. |
| `safety` | `safe` for pure caches. `caution` if the user loses something they might want (cookies, history, sessions, downloaded packages). `advanced` for experts only. |
| `defaultEnabled` | Only `safe` rules may be on by default. |
| `elevation` | `true` if any target is under `%WINDIR%` or `%ProgramData%`, or otherwise needs admin. |
| `detect` | The rule is hidden unless one of these paths exists. Required. |
| `runningProcesses` | Image names that hold the files open. Scour offers to close or skip them. |
| `targets` | `path` (must start with an env var), `pattern` (file-name glob) and `recurse`. |
| `exclude` | Full-path globs that are never touched. They may start with an env var or `*`. |
| `minAgeHours` | Keep files modified more recently than this (use it for temp folders). |

Path tokens: `{chromiumProfile}` matches `Default`, `Profile N` and `Guest Profile`; `{firefoxProfile}` matches every Firefox profile; `*` and `?` work in folder names.

### Checklist

- Only target caches, temp files and logs that the app rebuilds by itself. Never user documents, saves, settings, downloads or offline content.
- Prefer several narrow targets over one broad one.
- Run `scour rules lint rules`, `scour scan -r your.rule.id --largest 10` and `dotnet test` before opening a pull request.

The rule tests also try to make your rule resolve into protected folders (System32, Documents…) and check that the deny-list refuses every target.

## Code

- `Scour.Core` holds all logic, and the app and CLI stay thin. Anything the app can do, the CLI and tests can do too.
- Every deletion goes through `Cleaner`, which re-verifies against the scan and the deny-list. Don't add another path to `File.Delete`.
- New admin-only operations go through `Scour.Elevator`, which must re-derive what it acts on rather than trust paths from the client.
- Match the surrounding style. `dotnet build` treats warnings as errors.
