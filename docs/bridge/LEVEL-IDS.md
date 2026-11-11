# Level ids on take-off records (additive, no version bump)

Status: implemented on both sides (MynaCAD writes, MynaOps reads); see "What was run" below.

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

A project floor (`floors`) and a level with the same name (case-insensitive) are one entry. A level with no project floor of that name is appended with `name`, `height_mm` (the level's height) , `id` and `elevation_mm` only. A drawing without explicit levels writes the list exactly as before.

There is no new table and no new `.osp` meta key: the list rides in the existing take-off header JSON.

## Reader rules (MynaOps)

1. A row's level is the entry of `levels` whose `id` equals its `level_id`; if the row has no usable `level_id` (missing, not a whole number, negative, or no such id in `levels`), the entry whose `name` equals `level` (case-insensitive); if neither, a level made from the name alone; a row with no level belongs to the file's only level, else to "no level".
2. Unknown fields are ignored. A file with none of the new fields behaves exactly as before (grouping by name).
3. Level **names** map to AQC levels (`Lvl1`, `Lvl2`...) as before; the id selects the name when they disagree (a level renamed in MynaCAD after the row was written).

Old readers (hcwCAD-KIT's importer, earlier MynaOps) never look at the new fields and keep working: the schema sets `additionalProperties: false` only on the document root, not on rows or levels (and it now lists the new fields).

## Writer rules (MynaCAD)

`bridge.export` and `takeoff.record` stamp `level_id` after all rows are collected, from the drawing's named levels by name, and add `id` and `elevation_mm` to the `levels` list. Nothing is written for a drawing without levels. The take-off still measures the active level only; other levels need their own `takeoff.record` run with `level` (see `docs/levels-and-sheet-sets.md`).

## What MynaOps does with it

`TakeoffLevels.Summarise` groups rows per level (counts, wall length and gross wall area, room and slab area) and `TakeoffLevels.Filter` keeps the rows of chosen levels; the importer resolves each row's level through `level_id` first. The existing BOQ already filters by AQC level, so importing a filtered file gives a per-level BOQ.

## What was run (2026-11-11)

See the commit messages of branch `bridge-level-ids` (MynaOps) and `claude/bridge-level-ids-263691` (MynaCAD) for the exact commands and results. No WinUI UI for choosing levels at import was built.
