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
