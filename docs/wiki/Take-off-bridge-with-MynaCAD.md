# Take-off bridge with MynaCAD

MynaCAD writes the take-off as records in the shared `.osp` (`takeoff.record`: a header plus one record per wall, opening, column, lintel, room, slab, electrical board and circuit). MynaOps reads them (Project > Take-off).

- The contract is in `docs/PROJECT-FILE.md`, `docs/bridge/` and `docs/bridge/LEVEL-IDS.md`. Record kinds 0, 2 and 10 to 255 are reserved; adding a kind or an optional field is not a new version.
- **Level ids:** rows optionally carry `level_id` next to the level name, and each header `levels` entry optionally carries `id` and `elevation_mm`. Old readers ignore the extra fields; a drawing without named levels writes what it always wrote. The importer resolves a row's level by id first, so a renamed level still lands on the right floor. `TakeoffLevels` groups and filters quantities per level.
- Levels are matched by id, then by name, then by position. `Lvl0` (plinth) belongs to MynaOps and an import never changes it.
- **Not built:** a level picker in the MynaOps import window, a take-off run that records all levels at once, and live refresh of a running MynaOps when MynaCAD records.
