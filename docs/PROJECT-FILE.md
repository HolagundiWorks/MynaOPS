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
| Open elsewhere | MynaCAD has it open when it holds `<hash>.cad.lock` (the lease, below); another MynaOps when it holds `<hash>.ops.lock`; the older marker (the active-project pointer and a running `ODraft.WinUi`) still counts until package E removes it; any other program is seen by its handle on the file (an exclusive open fails). With MynaCAD holding the project, the page asks it to close first (**Close MynaCAD**) before Compact or Restore, and refuses when it stays open |

Nothing on the page changes the format: no table or column is added or altered and the revision does not move (a restore brings back the backup's own revision).

## Lease, session and database online/offline (Ops-first package A)

Code: `Office.Shared/ProjectLease.cs`, `ProjectSession.cs`, `OdpFile.Session.cs`; tests `ProjectLeaseTests.cs`, `ProjectSessionTests.cs` (what was run: `docs/ops-first-workflow.md`, section 11).

| Piece | What it is |
|---|---|
| Lease | `%LOCALAPPDATA%\O-Studio\leases\<hash>.ops.lock` (MynaOps) and `.cad.lock` (MynaCAD): a file kept open while the project is open; it goes with the process, a leftover file is not a lease. `<hash>` is the first 16 hex digits of SHA-256 of the lower-case full path. Details of the handle: `ops-first-workflow.md` 2.1 (clarifications) |
| `session.holder` | A **local** meta key written by MynaOps (no revision, no `change` row, no `sync.last`): `{machine, user, ops_pid, started_utc, heartbeat_utc}`, refreshed every 30 s and cleared on close; a heartbeat under 120 s from another machine or process makes MynaOps ask "Take over" before it opens the project. Only `session.*` keys can be written this way (`OdpFile.PutLocalMeta`) |
| Database online | MynaOps holds the project: its lease and the holder record |
| Database offline | MynaCAD was asked to close and has; the write-ahead log is folded into the file (`wal_checkpoint(TRUNCATE)`); the holder record is cleared and the lease released. Compact, Restore and copying the file are then safe. Project > Home has the button |
| Adoption | A file without `config.schema` goes through the Adopt window; **Adopt** makes a `manual` backup, then writes the `config.*` keys in one revision and nothing else, so every `drawing` row stays byte for byte as it was (`ProjectAdoptionTests`). The proposal comes from MynaCAD's `config.proposal`, the take-off header's levels (`id`, `elevation_mm`, `height_mm`), and the MynaOps project document, in that order; same-named levels are one entry and keep MynaCAD's id as `cid` |
| Save | The project document is written, then the Settings and Levels page values are merged into `config.*` (`ConfigPublisher`): one more revision, none when nothing differs |

## Project configuration (`meta.config.*`)

MynaOps is the only writer of the project configuration; MynaCAD reads it and applies it to the drawing (`docs/ops-first-workflow.md`, sections 4 and 7a; MynaCAD `docs/PROJECT-FILE.md` has the same description). It is additive: new `meta` keys with the prefix `config.`, no table added or changed, `meta.format` and `meta.version` unchanged. Every value is JSON text of at most 256 KiB and carries `"v": 1`; a key with an unknown `v` is left out and reported, unknown fields are ignored, and a part that breaks a rule (more than 64 levels, repeated ids or names, a number that is not finite or out of range, a `..` in a file pattern, a unit that does not exist) rejects the whole configuration with a `ConfigException`.

| Key | Written by | Meaning |
|---|---|---|
| `config.schema` | MynaOps | `{v, min_reader}`: present = configured by MynaOps; a reader older than `min_reader` refuses |
| `config.rev` | MynaOps | `{v, rev, app, time}`: rises by one per configuration change |
| `config.project`, `config.team`, `config.units`, `config.levels`, `config.layer_sets`, `config.tabs`, `config.sheets`, `config.naming`, `config.titleblock` | MynaOps | The parts (fields: ops-first-workflow.md 4.2; `config.tabs` also has an optional `next_tid`) |
| `config.applied.cad` | MynaCAD, local (no revision) | What MynaCAD applied: `{v, rev, time, result: ok\|partial\|failed, has_content, items}` |
| `config.proposal` | MynaCAD, local, once | What a legacy drawing already has, for the Adopt page; written only while the file has no `config.schema` |

`config.plot` is not defined: no project-wide plot setting exists (ops-first-workflow.md, 3.1).

Code: `Office.Shared/ProjectConfig.cs` (types, strict parse and validation, `Read`, the read-only `Probe`, `Write`), `Office.Shared/OdpFile.Config.cs` (`MetaLength`, `MetaText`, `WriteMeta`). **One revision per write:** `ProjectConfig.Write(odp, config)` puts every part that differs from the file and the bumped `config.rev` in one transaction, so `meta.revision` rises once, one `change` row names each key, and `sync.last` names `mynaops`; it writes nothing when no part differs, never deletes a key, and `WriteMeta` refuses the keys that describe the file. The Rust side is `aad_odp::config`; both read the same JSON fixtures (`Office.Shared.Tests/Fixtures/config`, identical in MynaCAD `crates/odp/tests/fixtures/config`) and each reads the other's file (`config-rust.osp`, `config-csharp.osp`). Tests: `Office.Shared.Tests/ProjectConfigTests.cs`.

## Retired

Legacy `.vdb` sidecars, the shared `shilpid` server, and ShilpiDB project files are **not** used anymore. Projects are **`.osp` only**. There is **no shipped importer** from old `.vdb` take-off files into `.osp`; see [MIGRATION-OSP.md](MIGRATION-OSP.md) for what is done and what remains.

Take-off **record layout** (kinds, slots, JSON rows, annotated PDF chunks) is unchanged; only the storage moved from ShilpiDB records into `takeoff_header` / `takeoff_row` tables. The JSON header still uses `"format": "hcw-takeoff-shilpi"` on the wire.
