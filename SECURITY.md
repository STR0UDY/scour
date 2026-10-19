# Security

A cleaner deletes files and sometimes runs as admin, so mistakes are expensive. This is how Scour limits the damage a bug, a bad rule or a compromised UI can do.

## Reporting a problem

Please report vulnerabilities privately through GitHub's "Report a vulnerability" (Security tab) rather than in a public issue.

## Elevation model

- `ScourApp.exe` and `scour.exe` are `asInvoker`: they never ask for admin on their own.
- When you tick a rule that needs admin (or use fast scan, or change a machine-wide startup item), Scour starts `Scour.Elevator.exe`. That's one UAC prompt per session.
- The helper creates a named pipe whose ACL allows only your user SID and denies network logons. It checks that the connecting process is the one that launched it, and that it runs from the helper's own folder.
- It exits when the pipe closes or after 5 idle minutes.

What the helper accepts:

| Request | What it trusts | What it re-derives |
| --- | --- | --- |
| Scan | Rule ids | Rules come from its own built-in `rules` folder only. User-imported packs never run as admin. |
| Clean | Rule ids, plus the files you saw | It rescans the rules itself and deletes only files that both scans found, with the same size and timestamp. |
| Startup toggle | An item id | It relists startup items and acts only on one that exists. It can disable or enable, never delete. |
| Leftovers | Paths | Only folders up to two levels under Program Files, ProgramData or the common Start menu, and never where an installed app lives. |
| Analyse | A drive root | Read-only MFT access. |

Every path still goes through the deny-list in the helper.

Install Scour per-machine (the MSI's default) so the helper's folder and built-in rules can only be changed by administrators.

## Deletion safety

- **Hard deny-list.** No rule can override it: drive roots and files directly in them, `C:\Windows` itself, `System32`, `SysWOW64`, `WinSxS`, the `Program Files`, `ProgramData`, user-profile and `AppData` roots, and everything under Desktop, Documents, Pictures, Music, Videos, Downloads and OneDrive for every profile. 8.3 short names are resolved before checking.
- **No link following.** Reparse points and junctions are never followed during scans.
- **Handle-based delete.** Each file is opened with `FILE_FLAG_OPEN_REPARSE_POINT`. Size, timestamp and the handle's final path must match the scan and pass the deny-list, and the delete happens through that same handle. A folder swapped for a junction between scan and clean is detected.
- **Nothing forced.** Locked files are skipped and counted.
- **Logged.** Every outcome goes to `%LOCALAPPDATA%\Scour\logs\clean-*.jsonl`.

## Network

Scour makes one kind of request, and only if you turn on update checks: an anonymous GET to `api.github.com/repos/<repo>/releases/latest`. No identifiers, version or machine details are sent.
