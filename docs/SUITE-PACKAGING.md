# O-CoreOp in the unified suite installer

**Status:** Verified in tree 2026-11-04 · **Canonical suite doc:** O-Studio `installer/suite/README.md`

O-CoreOp is installed through **`O-Suite-Setup-<version>.exe`**, built in the O-Studio repository (`installer/suite/build-single.ps1`). That single package:

- **Install mode:** checkbox **O-CoreOp** (or **Everything**); optional shared **Olmo 3 7B** model task (same file O-Draft uses).
- **Remove mode:** run the same Setup again, choose **Remove applications**, select O-CoreOp only or with other apps; company files and the model stay unless the user removes them elsewhere.

Build this app’s payload first:

```powershell
# From O-STUDIO repo: cargo build --release -p office-ffi  (or office-ffi folder)
powershell -ExecutionPolicy Bypass -File BBSDesktop\installer\build-installer.ps1 `
  -OfficeDllDir D:\path\to\O-STUDIO\office-ffi\target\release
```

Output: `BBSDesktop/artifacts/installer/O-CoreOp-Setup-<ver>.exe` (Inno Setup, per user). The suite builder copies it to `installer/suite/stage/` and runs it silently during O-Suite install.

Per-app packaging, signing, and MSIX: [PACKAGING.md](PACKAGING.md).
