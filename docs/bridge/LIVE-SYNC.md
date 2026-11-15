# Live sync of the shared project file (MynaOps side)

The design, the `meta.sync.*` keys, the states and the failure modes are in MynaCAD `docs/live-sync.md`; this page lists the MynaOps code.

| Piece | File |
|---|---|
| Read-only probe, state rules, status words | `Office.Shared/OdpSync.cs` |
| Accepted revision per file, `Changed` event | `Office.Shared/SyncStatusService.cs` |
| Debounced (300 ms) watcher on the `.osp` and its `-wal`; only calls the poll earlier | `Office.Shared/ProjectFileWatcher.cs` |
| `sync.last` (`app: mynaops`) written in the same transaction as every revision | `Office.Shared/OdpFile.cs` (`Write`) |
| The existing 2 s poll also refreshes the status; own saves are accepted | `BBSApp/Services/Odp/ServedProject.cs` |
| Status text in the status strip (`ProjectSyncStatus`); watcher started with the poll | `BBSApp/MainWindow.xaml.cs` |
| Take-off page: accepts the file after it reloaded (selection kept by `FillList`), header note when out of date or locked | `BBSApp/Views/TakeoffPage.cs` |

Tests: `Office.Shared.Tests/OdpSyncTests.cs` (11, pass). `BBSApp` builds in Release x64. What was run by hand with both apps, and what was not, is in MynaCAD `docs/live-sync.md`, "Run by hand".

MynaOps does not write `takeoff_*`, `drawing` or `detail` rows, and MynaCAD does not write `project_doc`; no table or column is added.

## What was run by hand

Both apps open on one test file (2026-11-15, merged branches): MynaCAD recording showed `Updated by MynaCAD at hh:mm` in MynaOps; another writer made both apps show `Updated by ...`; an exclusive lock showed `Locked` in both; a copy of another project over the file showed `Project file was replaced`; a text file in its place showed `Project file unreadable`; both stayed responsive. Not run: the Take-off page reload and accept, and a real MynaOps save. The full table is in MynaCAD `docs/live-sync.md`.
