# Conventions

- **Take-off happens in MynaCAD,** not in MynaOps. MynaCAD writes it into the `.osp`; MynaOps reads it. The file contract is in `docs/bridge/`.
- **Imports are idempotent.** Every imported row carries `source = "hcwcad:<drawing_id>"` and a `source_ref`; keep that prefix, it is stored in user projects.
- Never import plaster, painting, skirting, shuttering or rebar: MynaOps derives them. Preview first, one undo step, never delete a row that has disappeared from the file.
- **Say only what you ran.** Documentation separates implemented from planned.
- **Git.** Add files by name; never `git add -A`; the default branch is `main`.
