# Level ids on take-off records (additive, no version bump)

Status: implemented on both sides (MynaCAD writes, MynaOps reads). The per-level summary, the import level picker and `level_status` were added 2026-11-15; "What was run" below says exactly what was run and what was not.

## Why

MynaCAD has named levels (`level.*` commands). A take-off is measured on the **active level** only, and every row already carries `level`, the level's name. A name can change, and two drawings can use the same name for different floors, so MynaOps could not tell reliably which floor a quantity belongs to. A stable id and an elevation fix that without touching anything a reader depends on.

## The change

Adds optional fields only. `format` stays `hcw-takeoff-shilpi` / `hcw-aqc-bridge`, `version` stays `1`, units stay millimetres, record kinds and ids are unchanged. Per `PROJECT-FILE.md` and the schema rule, a new optional field is not a new version.

### On every take-off row (wall, opening, column, lintel, room, slab, board, circuit, plumbing source and fixture; and the `opening_schedule` rows)

| Field | Type | Meaning |
|---|---|---|
| `level` | string | Already present. The level's **name** (or the floor name of a floor region, or the `level` the take-off was stamped with). Stays the fallback key. |
| `level_id` | integer ≥ 0, optional | The MynaCAD level's stable id (`LevelId`, never reused; names may change). Written only when the drawing has explicit named levels **and** `level` names one of them. Absent otherwise: a drawing with one implicit level, a floor region or stamped name that no level has. |

### In the header (and the bridge file) `levels` list

Each entry keeps its existing fields (`name`, `height_mm` = floor to floor, `slab_thickness_mm`, `beam_depth_mm`, `ceiling_mm`, `lintel_bottom_mm`) and gains, when the drawing has explicit levels:

| Field | Type | Meaning |
|---|---|---|
| `id` | integer ≥ 0, optional | The level's stable id: the value rows carry in `level_id`. |
| `elevation_mm` | number, optional | The level's floor elevation above the site datum, in millimetres. |
| `config_id` | integer ≥ 0, optional | **Added with the project configuration** (Ops-first, package C). The `cid` of the configured level (`meta.config.levels`, MynaOps' own id, never reused) that this MynaCAD level is bound to. Written next to `id` only when the drawing has applied a project configuration and the level is bound (`config.levelmap` in the drawing's project data). For a level adopted from an existing drawing `cid` equals `id`. A reader that wants the configured level uses `config_id`; `id` stays the MynaCAD level. |

A project floor (`floors`) and a level with the same name (case-insensitive) are one entry. A level with no project floor of that name is appended with `name`, `height_mm` (the level's height) , `id` and `elevation_mm` only. A drawing without explicit levels writes the list exactly as before.

There is no new table and no new `.osp` meta key: the list rides in the existing take-off header JSON.

## Reader rules (MynaOps)

1. A row's level is the entry of `levels` whose `id` equals its `level_id`; if the row has no usable `level_id` (missing, not a whole number, negative, or no such id in `levels`), the entry whose `name` equals `level` (case-insensitive); if neither, a level made from the name alone; a row with no level belongs to the file's only level, else to "no level".
2. Unknown fields are ignored. A file with none of the new fields behaves exactly as before (grouping by name).
3. Level **names** map to AQC levels (`Lvl1`, `Lvl2`...) as before; the id selects the name when they disagree (a level renamed in MynaCAD after the row was written).

Old readers (hcwCAD-KIT's importer, earlier MynaOps) never look at the new fields and keep working: the schema sets `additionalProperties: false` only on the document root, not on rows or levels (and it now lists the new fields).

## Writer rules (MynaCAD)

`bridge.export` and `takeoff.record` stamp `level_id` after all rows are collected, from the drawing's named levels by name, and add `id` and `elevation_mm` to the `levels` list. Nothing is written for a drawing without levels. A plain `takeoff.record` measures the active level only. `takeoff.record` with `levels: "all"` (or a list of level names or ids; not together with `level`) records several levels in one run: see "Recording several levels" below.

## Recording several levels (MynaCAD)

`takeoff.record` takes `levels`: `"all"`, or a list of level names or ids (a name or a whole number; at most 64; an unknown name is an error that lists the levels; `level` and `levels` together are an error). On a drawing without named levels `"all"` records as before (with a warning) and a list is an error.

- The take-off measures the active level, so every requested level other than the active one is measured on a **scratch copy** of the drawing: the copy activates the level, runs again the take-off tools whose saved results the drawing already has (default parameters, no tables), and collects the rows with that level as the stamped level. The open drawing is not changed. The active level keeps the results it has saved.
- Every row carries its level's `level_id`. Electrical and plumbing rows belong to the drawing, not to a level: they come from the active level's run.
- A run replaces everything recorded for the drawing, so the levels recorded are exactly the ones named in the last run.

### Header key `level_status` (optional, additive)

Written when the drawing has named levels, in the take-off header only (not in the bridge file, so the bridge schema is unchanged). Old readers ignore it.

```json
"level_status": { "mode": "all", "recorded_at": "2026-11-15T10:00:00Z",
  "levels": [ { "id": 1, "name": "Ground", "recorded": true,  "rows": 20, "fingerprint": "00ab...", "stale": false },
              { "id": 3, "name": "Roof",   "recorded": false, "rows": 0,  "stale": false } ] }
```

`mode` is `all`, `list` or `active` (a plain recording). `fingerprint` hashes the level's objects (not the take-off's own labels and tables), name, elevation and height when it was recorded. `stale` is written `false`; MynaCAD's `takeoff.levels` compares the fingerprints with the drawing now and, with `update`, writes `stale: true` into the header of the project file for every recorded level that changed since. MynaOps shows the flag and never computes it. Readers must tolerate a missing key, a missing `levels` list, entries without `id`, and wrong types (MynaOps skips an entry it cannot read).

## What MynaOps does with it

- `TakeoffLevels.Summarise` groups rows per level (counts, wall length and gross wall area, room and slab area) and `TakeoffLevels.Filter` keeps the rows of chosen levels. The importer resolves each row's level through `level_id` first.
- **Project > Take-off** lists, for the selected drawing, one line per level: elevation, the AQC level the importer would put it on (`Lvl1`..., "(new)" when the import creates it; the importer's own level mapping), walls, wall length and area, rooms and room area, slab area, and the recording status from `level_status` (recorded, changed in MynaCAD since recorded, not recorded, not in the recording). A take-off without `level_status` says so.
- **Import preview** (File > Import from CAD and Take-off > Read take-off) shows a tick box per level when the take-off has two or more. The preview and the import cover the ticked levels only; all ticked imports the file whole; none ticked disables Import. A chosen level keeps the position it has in the whole file when the importer falls back to matching by position, and is never put on an AQC level named after another floor of the file, so importing floors one at a time does not stack them.
- The BOQ by level needs nothing new: imported rows sit on their AQC level and the BOQ already filters by it, so a CAD level's quantities appear under the AQC level shown in the summary after importing.

### The double-count warning compares floors by elevation

A MynaCAD level id belongs to one drawing: two drawings can both have a level 1, and a copied drawing keeps its ids, so comparing ids across drawings can neither prove nor rule out the same floor. When both levels state `elevation_mm` (same site datum), equal elevations (within 1 mm) are the same floor whatever they are called; otherwise the names (MynaCAD, at record time) or the AQC levels (MynaOps, at import) decide, as before. MynaOps stores `source_elevation_mm` on the rows it adds (older rows have none and fall back to the AQC level). This differs from the task text "compare `level_id` when both sides have one"; whether to keep it is the owner's decision.

## What was run

2026-11-11 (first stage): see the commit messages of `bridge-level-ids` (MynaOps) and `claude/bridge-level-ids-263691` (MynaCAD).

2026-11-15 (this stage; branch `ops-level-ui` in MynaOps, `claude/ops-level-ui-f47be8` in MynaCAD):

- MynaCAD: `cargo test -p aad-engine --test takeoff_levels --test takeoff_record --test quantity_trust --test bridge --test takeoff_save_record` all pass (9 new tests: multi-storey run, list by name and id, stale and update, no-levels drawing, hostile `levels` values, elevation-based overlap); `cargo clippy -p aad-engine --all-targets -- -D warnings` clean; `cargo xtask layers` OK. The full `cargo xtask ci` and the whole engine test suite were **not** run (linking every test binary ran out of memory on this machine).
- MynaOps: `dotnet test BBSDesktop/AQC.CadImport.Tests -c Release` 110 passed; `dotnet build BBSDesktop/BBSApp/BBSApp.csproj -c Release -p:Platform=x64 -p:WindowsPackageType=None` 0 errors.
- By hand, UI Automation against the built `O-CoreOp.exe`, opening a project file that MynaCAD's `takeoff.record` (`levels: "all"`) wrote for a three-level drawing (Ground 2 walls, First 1 wall, Roof empty): the Take-off page showed the three levels with counts, the AQC level (`Lvl1`, `Lvl2 (new)`, `Lvl3 (new)`) and "recorded"; the import preview showed the three tick boxes; unticking First changed the preview from 5 to 3 adds with the Ground walls still on `Lvl1`. **Not exercised in the app:** the "changed in MynaCAD since recorded" display (unit tests only), pressing Import with a partial selection, a take-off linked to a company-file project, and live refresh while MynaCAD records.
