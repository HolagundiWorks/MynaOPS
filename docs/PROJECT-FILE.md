# The project file (.osp)

A MynaOps project is a **project file, `.osp`**: one SQLite file that holds the drawing, the take-off MynaCAD recorded, and everything MynaOps keeps for the project (estimate and revisions, running bills, tasks, quantities, purchase orders, contracts, stores, rates, settings). MynaCAD and MynaOps open the same file. **Migration from ShilpiDB / `.vdb`:** [MIGRATION-OSP.md](MIGRATION-OSP.md). The company file stays Studio's and is read-only here (name, address, GSTIN, letterhead). The names, roles and links of the whole suite are in [SUITE.md](SUITE.md); the format and sync plan are in the MynaHub repository, `docs/ODP-PROJECT-FILE-AND-SYNC.md`.

## What MynaOps does with it

| Action | Behaviour |
|---|---|
| File > Open | `.osp`; older `.bbsproj` / `.json` still open |
| File > Save / Save as | Writes the project record into the `.osp`; Save as to a new name copies the file first so the drawing and take-off come with it. Older formats are saved as `.osp` |
| Company file (suite-linked) | On save or calculate, mirrors **`aqc.project`** summaries (estimate + RA bills) into the company file so **MynaHub** project dashboards and fee proposals can read them; full JSON remains in the `.osp` |
| Take-off page | Reads headers, rows and annotated PDFs from the `.osp` (`OdpTakeoffAdapter` feeds the same importer as before) |
| Live | Every 2 s `ServedProject.PollAsync` reads `meta.revision`; a revision this app did not write raises "MynaCAD updated the take-off". Own saves are ignored (`NoteOwnRevision`) |
| Opening MynaCAD's project | The active-project pointer `%LOCALAPPDATA%\O-Studio\active-project.json` names the `.osp` MynaCAD has open |
| Double-click | The installer registers `.osp` with "Open in MynaOps" in the right-click menu |

Code: `Office.Shared/OdpFile.cs` (the format), `BBSApp/Services/ProjectFiles.cs`, `Services/Odp/{ServedProject,OdpTakeoffAdapter}.cs`, `Views/TakeoffPage.cs`.

## Project database (MynaOps page)

**Project > Project database** (also **File > Project database**) looks after the open `.osp` as a database; **Choose file** inspects another one. Code: `Office.Shared/ProjectDatabase.cs` (tests `Office.Shared.Tests/ProjectDatabaseTests.cs`), page `BBSApp/Views/ProjectDatabasePage.cs`.

| Action | What it does |
|---|---|
| Info | Format and version (`meta.format`, `meta.version`), revision, project name and id, size of the file and of its write-ahead log, free pages (what Compact gives back), journal mode, folder, rows in every table |
| Check integrity | `PRAGMA integrity_check`; lists what SQLite reports |
| Compact | `VACUUM`, then folds the write-ahead log in. Contents, tables and revision unchanged. Refused while the file is open elsewhere |
| Back up now | `VACUUM INTO` a temporary name in `<name>.backups\`, renamed to `<name> <UTC yyyyMMddTHHmmssZ> manual.osp`: the folder and names MynaCAD's engine uses (MynaCAD `docs/AUTOSAVE-BACKUP.md`). Safe while MynaCAD has the file open. Nothing is pruned here; MynaCAD prunes to its `BACKUPCOUNT` on its next save, counting these too |
| Backups | The backups of the file, newest first; other files in the folder are ignored |
| Restore selected | Only a listed backup; keeps the file as it is as a `before-restore` backup first, copies the backup to a temporary file, checkpoints, renames it over the file, removes `-wal`/`-shm`; MynaOps then reopens the project. Refused while the file is open elsewhere |
| Open elsewhere | MynaCAD has it open when the active-project pointer names it and MynaCAD (`ODraft.WinUi`) runs; any other program is seen by its handle on the file (an exclusive open fails) |

Nothing on the page changes the format: no table or column is added or altered and the revision does not move (a restore brings back the backup's own revision).

## Retired

Legacy `.vdb` sidecars, the shared `shilpid` server, and ShilpiDB project files are **not** used anymore. Projects are **`.osp` only**. There is **no shipped importer** from old `.vdb` take-off files into `.osp`; see [MIGRATION-OSP.md](MIGRATION-OSP.md) for what is done and what remains.

Take-off **record layout** (kinds, slots, JSON rows, annotated PDF chunks) is unchanged; only the storage moved from ShilpiDB records into `takeoff_header` / `takeoff_row` tables. The JSON header still uses `"format": "hcw-takeoff-shilpi"` on the wire.
