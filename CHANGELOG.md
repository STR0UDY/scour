# Changelog

## 1.0.0

First release.

- **Cleaner.** 51 built-in JSON rules across System, Browsers, Apps, Games and Dev, including the Recycle Bin (with "older than N days"). Preview first, a hard deny-list, handle-based deletes that re-verify every file, optional Recycle Bin mode and a log of every outcome.
- **Profiles.** Quick, Deep and Dev box, plus your own ticks. Exclusions per file or folder.
- **winapp2.ini import.** Imported rules are flagged Caution and off by default.
- **Disk analyser.** Reads the NTFS MFT directly via the admin helper, or walks folders without admin. Squarified treemap, sortable tree list, file-type breakdown, open in Explorer, move to the Recycle Bin, never-clean exclusions.
- **Startup manager.** Run keys, Startup folders, logon and boot tasks, and services, with Authenticode publisher checks (including catalog-signed files). Reversible toggles; Microsoft items hidden by default.
- **Uninstaller.** Win32 and MSIX apps. Runs the app's own uninstaller, then a leftover scan with everything unticked.
- **Scheduled cleans** via a Task Scheduler entry that runs the CLI. No background service.
- **CLI.** `scan`, `clean`, `analyse`, `startup`, `apps` and `rules`.
- **Admin helper** over an ACL-restricted named pipe; it accepts rule ids, not paths.
- Opt-in update check against GitHub releases.
