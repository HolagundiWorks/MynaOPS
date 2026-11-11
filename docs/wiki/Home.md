# MynaOps

MynaOps is the operations app of the Myna suite: RCC bar bending schedules, civil BOQ, rate books, DSR-style estimates, purchase orders, running bills, scheduling and contracts, for a project manager or a contractor persona. It reads the quantity take-off that MynaCAD records into the shared project file (`.osp`).

MynaOps also hosts the suite's **local AI agent** (Olmo 3 7B, loopback `127.0.0.1:8742`) for itself and MynaCAD, and holds the **project database manager** (integrity check, compact, backup, restore) for the shared `.osp`.

## Pages
- [[Architecture|Architecture]]
- [[Build and test|Build-and-test]]
- [[Conventions|Conventions]]
- [[Take-off bridge with MynaCAD|Take-off-bridge-with-MynaCAD]]
- [[The Myna suite|The-Myna-suite]]

Quantities follow IS 456 and IS 1200 derived conventions; always cross-check against drawings.
