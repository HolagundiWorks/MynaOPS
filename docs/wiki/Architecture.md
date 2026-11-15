# Architecture

- **App:** WinUI 3 in `BBSDesktop/BBSApp`, using the shared `O.Ui` library (sibling folder `MynaUI`). Identifiers keep old spellings: `O-CoreOp.exe`, `bbs_engine.dll`, `.bbsproj`.
- **Libraries:** `Office.Shared` (project store and shared types), `Office.Ai` (AI host and client), `AQC.CadImport` (the importer and the `.osp` take-off reader).
- **Modules:** Project (dashboard, levels, take-off from MynaCAD), RCC steel, Civil BOQ, Schedule, Contracts, Stores, Outputs.
- **Linked-item derivation:** one trade's quantity is derived from another (plaster from masonry, painting from plaster, skirting from flooring).
- **AI host:** a MynaOps window hosts the agent; without a window MynaOps runs it headless with `O-CoreOp.exe --ai-host`, started by MynaCAD when no host answers. It ends when neither app is open.
- **Independence:** MynaOps needs no launcher, no sign-in and no MynaHub. Project data reaches the company database through the CSV export that MynaHub imports.
- **Ops-first (package A, 2026-11-15, branch not yet merged):** MynaOps opens or creates the project (wizard writes `meta.config.*`), holds it with a lease, starts MynaCAD with `--managed --project --ops-pid`, starts and stops the AI agent, and takes the project database online or offline. See `docs/ops-first-workflow.md`.
