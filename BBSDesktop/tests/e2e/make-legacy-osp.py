# Makes a small project file the way MynaCAD leaves one before the Ops-first workflow: a drawing, take-off rows, a take-off header with levels,
# a config.proposal (MynaCAD's local key) and no config.* keys. Used by uia-ops-first.ps1 to drive the Adopt page. Usage: python -I make-legacy-osp.py <out.osp>
import hashlib
import json
import sqlite3
import sys

path = sys.argv[1]
db = sqlite3.connect(path)
db.executescript("""
PRAGMA journal_mode=WAL;
CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID;
CREATE TABLE drawing (name TEXT PRIMARY KEY, ext TEXT NOT NULL, sha256 TEXT NOT NULL, bytes BLOB NOT NULL, revision INTEGER NOT NULL, created TEXT NOT NULL) WITHOUT ROWID;
CREATE TABLE drawing_state (drawing TEXT PRIMARY KEY, json TEXT NOT NULL) WITHOUT ROWID;
CREATE TABLE takeoff_header (slot INTEGER PRIMARY KEY, state TEXT NOT NULL, json TEXT NOT NULL);
CREATE TABLE takeoff_row (slot INTEGER NOT NULL, kind INTEGER NOT NULL, n INTEGER NOT NULL, json TEXT NOT NULL, PRIMARY KEY (slot, kind, n)) WITHOUT ROWID;
CREATE TABLE project_doc (id INTEGER PRIMARY KEY CHECK (id = 1), json TEXT NOT NULL);
CREATE TABLE asset (id TEXT PRIMARY KEY, slot INTEGER, name TEXT NOT NULL, mime TEXT NOT NULL, sha256 TEXT NOT NULL, bytes BLOB NOT NULL) WITHOUT ROWID;
CREATE TABLE markup (id TEXT PRIMARY KEY, drawing TEXT NOT NULL, author TEXT NOT NULL, created TEXT NOT NULL, kind TEXT NOT NULL, json TEXT NOT NULL, text TEXT NOT NULL DEFAULT '', resolved INTEGER NOT NULL DEFAULT 0) WITHOUT ROWID;
CREATE TABLE detail (id TEXT PRIMARY KEY, drawing TEXT NOT NULL, handle TEXT NOT NULL, kind TEXT NOT NULL, block TEXT NOT NULL, params TEXT NOT NULL, layer TEXT NOT NULL, label TEXT NOT NULL) WITHOUT ROWID;
CREATE TABLE history (id INTEGER PRIMARY KEY, created TEXT NOT NULL, label TEXT NOT NULL, bytes BLOB NOT NULL);
CREATE TABLE change (revision INTEGER NOT NULL, tbl TEXT NOT NULL, key TEXT NOT NULL, op TEXT NOT NULL);
CREATE INDEX change_revision ON change (revision);
""")
for k, v in [("format", "osp"), ("version", "1"), ("project_id", "legacy0000000000000000000000000001"), ("name", "Legacy Villa"),
             ("created", "2026-11-01T09:00:00Z"), ("revision", "4")]:
    db.execute("INSERT INTO meta VALUES (?, ?)", (k, v))
drawing = b"AC1027 legacy drawing bytes " + bytes(range(256)) * 8
db.execute("INSERT INTO drawing VALUES (?,?,?,?,?,?)", ("villa.dxf", "dxf", hashlib.sha256(drawing).hexdigest(), drawing, 3, "2026-11-01T09:00:00Z"))
for i in range(3):
    db.execute("INSERT INTO takeoff_row VALUES (0, 1, ?, ?)", (i, json.dumps({"n": i})))
header = {"format": "hcw-takeoff-shilpi", "version": 1, "levels": [
    {"name": "Ground", "id": 3, "elevation_mm": 0, "height_mm": 3200},
    {"name": "First", "id": 4, "elevation_mm": 3200, "height_mm": 3000}]}
db.execute("INSERT INTO takeoff_header VALUES (0, 'recorded', ?)", (json.dumps(header),))
proposal = {"v": 1, "time": "2026-11-15T09:00:00Z", "has_content": True, "drawing_unit": "m",
            "levels": [{"cid": 3, "name": "Ground", "elevation_mm": 0, "height_mm": 3200}, {"cid": 4, "name": "First", "elevation_mm": 3200, "height_mm": 3000}],
            "tabs": [{"tid": 7, "name": "Working drawing", "layer_prefix": "W-", "layers": ["A-*"], "blocks": ["DOOR*"]}],
            "layer_sets": ["standard"], "project": {"title": "Legacy Villa", "client": "S. Hegde", "architect": "A. Kulkarni"}}
db.execute("INSERT INTO meta VALUES ('config.proposal', ?)", (json.dumps(proposal),))
db.commit()
db.close()
print("wrote", path)
