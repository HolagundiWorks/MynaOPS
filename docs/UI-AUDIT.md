# UI and feature audit — Tren and Falcon (2026-10-31)

Cross-check of stale chrome, placeholder behaviour, and intentional stubs after the take-off toolbar move and `.osp` take-off path.

## Fixed in this pass

| Item | App | Was | Now |
|---|---|---|---|
| Retired left tool column | Tren | Empty `ToolColumn` + hidden `LeftToolHost` still in the grid | Column removed; canvas uses full width beside sheet rail |
| Window menu checkmarks | Tren | Literal `?` prefix on toggles (broken placeholder) | `ToggleMenuFlyoutItem` with real checked state |
| Help → updates | Tren | Missing while Falcon had it | **Check for updates…** via shared `OSAS_UPDATE_CHANNEL_URL` (`o-draft` package id) |
| Graph window hint | Tren | "Connect Companion" (removed product) | Describes engine `companion.evaluate` + optional O-Dynamo build |
| Take-off status text | Falcon | Empty branch when project not Studio-linked | Explains Studio link for header matching |
| CAD import comment | Falcon | hcwCAD-KIT-only wording | Bridge v1 from Tren take-off or legacy export |
| `docs/SUITE.md` Tren layout | Both repos | Left vertical tools + take-off on one strip | Two-row top layout documented |

## Intentional stubs (not bugs)

| Item | App | Notes |
|---|---|---|
| Help → Check for updates | Both | Check-only; no download/install until org publishes channel JSON (`UpdateCheck`, `docs/PACKAGING.md`) |
| Code signing / auto-update | Suite | Roadmap; `sign.ps1` exists |
| Rate book seed rows | Falcon | Placeholder **rates** in data, not UI — edit in Rate book |
| AORMS Connect / Bridge | Falcon | Removed 2026-10-28; docs only |
| Graph in-process evaluate | Tren | Optional `ODRAFT_GRAPH` + O-Dynamo; default uses engine pipe |
| AI agent tools | Tren | Unregistered tools return "not implemented yet" by design |
| Electrical / xref extras | Tren engine | Documented "not implemented" in command modules |

## Stale naming (code, not user-facing)

| Item | Notes |
|---|---|
| `BBSApp.Services.Shilpi` | Historical name; reads `.osp` take-off via `OdpTakeoffAdapter` — rename is a large refactor, behaviour is correct |
| `BridgeFile` / `.aqcbridge` | hcw-aqc-bridge v1 contract; still valid offline path |
| `hcw*` theme keys | Shared `O.Ui` bridge resources, not AORMS |

## Verified wired (sample)

- Tren take-off bar: draw tools, `takeoff.record`, `takeoff.maplayers`, **Sync take-off** (`takeoff.record` without annotate), phase pickers ← `takeoffPhases` in chrome JSON
- Falcon take-off page: poll on file mtime, **Sync take-off**, **Reload list**, import preview, annotated PDF store
- Falcon ribbon pages: Schedule, Contracts, Stores, Data model, Report — implemented (not empty shells)
- Parameters Manager, Smart blocks, layer import map, Pomodoro — host commands wired

## Follow-ups (owner / later)

- Rename `Shilpi*` types to `Odp*` when a breaking refactor is acceptable
- Publish `OSAS_UPDATE_CHANNEL_URL` JSON with `o-draft` and `o-coreop` package entries
- Sync `docs/SUITE.md` from Falcon into Kivo and O-DRAFT when editing suite docs (four-repo rule)
- Kivo cloud connector row in SUITE.md remains **planned**
