# Take-off bridge with MynaCAD

MynaCAD writes the take-off as records in the shared `.osp` (`takeoff.record`: a header plus one record per wall, opening, column, lintel, room, slab, electrical board and circuit). MynaOps reads them (Project > Take-off).

- The contract is in `docs/PROJECT-FILE.md`, `docs/bridge/` and `docs/bridge/LEVEL-IDS.md`. Record kinds 0, 2 and 10 to 255 are reserved; adding a kind or an optional field is not a new version.
- **Level ids:** rows optionally carry `level_id` next to the level name, and each header `levels` entry optionally carries `id` and `elevation_mm`. Old readers ignore the extra fields; a drawing without named levels writes what it always wrote. The importer resolves a row's level by id first, so a renamed level still lands on the right floor. `TakeoffLevels` groups and filters quantities per level.
- Levels are matched by id, then by name, then by position. `Lvl0` (plinth) belongs to MynaOps and an import never changes it.
- **Several levels in one run:** `takeoff.record` with `levels: "all"` (or a list) measures every level and stamps each row with its `level_id`; the header's optional `level_status` says which levels were recorded, when, and (via `takeoff.levels`) which changed since.
- **Project > Take-off** shows a per-level summary of the selected drawing (counts, wall length and area, room and slab area, the AQC level each lands on, recording status). The **import preview** has a tick box per level and imports only the ticked ones. Details and what was run by hand: `docs/bridge/LEVEL-IDS.md`.
- The double-count warning compares floors by `elevation_mm` when both sides state one, else by level.
- **Not built:** live refresh of a running MynaOps when MynaCAD records (MynaOps polls the project file and the Take-off page reloads when the file changes). The stale display was checked by unit tests only, not in the running app.

## Live sync

The status strip shows `Synced`, `Updated by MynaCAD at hh:mm`, `Take-off out of date`, `Locked`, `Project file was replaced` or `Project file unreadable` for the open `.osp`. It only reads the file; the Take-off page reloads when it is open. Code and what was run by hand: `docs/bridge/LIVE-SYNC.md`.
