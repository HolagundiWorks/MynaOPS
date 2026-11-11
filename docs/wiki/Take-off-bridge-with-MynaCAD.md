# Take-off bridge with MynaCAD

MynaCAD writes the take-off as records in the shared `.osp` (`takeoff.record`: a header plus one record per wall, opening, column, lintel, room, slab, electrical board and circuit). MynaOps reads them (Project > Take-off).

- The contract is in `docs/PROJECT-FILE.md` and `docs/bridge/`. Record kinds 0, 2 and 10 to 255 are reserved; adding a kind or an optional field is not a new version.
- Levels are matched by name, then by position. `Lvl0` (plinth) belongs to MynaOps and an import never changes it.
- Level ids on take-off records are not built yet.
