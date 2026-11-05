"""Builds a structured items + item_stats SQLite from the archived BisBeard item APIs.

Reads only D:\\WoW\\Coa\\DataCOA (never the network) and, read-only, the client Item table in the dbc layer.
Realm honesty: the main list is shared by CoA, Classic and S10 and is not tagged per realm, so its rows are
realm_proven=0. Only the wotlk and octo lists are proven. `in_coa_client` is a hint, not proof of CoA.

usage: python scripts/build-items-db.py [out.sqlite]
"""
import json, os, sqlite3, sys

DATA = r"D:\WoW\Coa\DataCOA\data"
DBC = r"F:\WorkDir\CoAMCP\.superpowers\layers\dbc.sqlite"
OUT = sys.argv[1] if len(sys.argv) > 1 else r"F:\WorkDir\CoAMCP\.superpowers\items\items.sqlite"

LISTS = [  # list name, file, realms, proven, published
    ("main", os.path.join(DATA, "api", "items_all.json"), "coa,classic,s10", 0, "2026-08-27"),
    ("wotlk", os.path.join(DATA, "realms", "wotlk", "api", "items_all.json"), "wotlk", 1, "2026-07-16"),
    ("octo", os.path.join(DATA, "realms", "octo", "api", "items_all.json"), "octo", 1, "2026-08-10"),
]

SCHEMA = """
CREATE TABLE items(
  list TEXT NOT NULL, id INTEGER NOT NULL, name TEXT NOT NULL, realms TEXT NOT NULL, realm_proven INTEGER NOT NULL,
  in_coa_client INTEGER, slot TEXT, type TEXT, equip_loc TEXT, quality TEXT, item_level INTEGER, req_level INTEGER,
  source_category TEXT, source TEXT, version TEXT, phase INTEGER, drop_rate TEXT, set_name TEXT,
  unique_equip INTEGER, required_faction TEXT, base_item_id INTEGER, affix_id INTEGER, bloodforged_type TEXT,
  dmg_min REAL, dmg_max REAL, dmg_speed REAL, icon TEXT, description TEXT, extra_json TEXT,
  PRIMARY KEY(list, id)) WITHOUT ROWID;
CREATE TABLE item_stats(list TEXT NOT NULL, item_id INTEGER NOT NULL, stat TEXT NOT NULL, value REAL NOT NULL,
  PRIMARY KEY(list, item_id, stat)) WITHOUT ROWID;
CREATE TABLE item_classes(list TEXT NOT NULL, item_id INTEGER NOT NULL, class TEXT NOT NULL,
  PRIMARY KEY(list, item_id, class)) WITHOUT ROWID;
CREATE TABLE item_set_bonuses(list TEXT NOT NULL, item_id INTEGER NOT NULL, pieces INTEGER NOT NULL, bonus TEXT NOT NULL,
  PRIMARY KEY(list, item_id, pieces)) WITHOUT ROWID;
CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE INDEX ix_items_realm ON items(realms, slot, quality);
CREATE INDEX ix_items_name ON items(name COLLATE NOCASE);
CREATE INDEX ix_items_cat ON items(list, source_category, version);
CREATE INDEX ix_stats_stat ON item_stats(stat, value);
CREATE INDEX ix_classes ON item_classes(class);
-- CoA is the priority. Both views are the shared list (CoA, Classic or S10), NOT proven CoA.
CREATE VIEW coa_candidate_items AS SELECT * FROM items WHERE list='main';
CREATE VIEW coa_client_items AS SELECT * FROM items WHERE list='main' AND in_coa_client=1;
"""

KNOWN = {"id", "name", "slot", "type", "equipLoc", "quality", "itemLevel", "reqLevel", "sourceCategory", "source",
         "version", "phase", "dropRate", "setName", "uniqueEquip", "requiredFaction", "baseItemId", "affixId",
         "bloodforgedType", "damage", "icon", "description", "stats", "classes", "setBonuses"}


def client_ids():
    if not os.path.exists(DBC):
        return None
    c = sqlite3.connect("file:" + DBC.replace("\\", "/") + "?mode=ro", uri=True)
    try:
        return {r[0] for r in c.execute("SELECT ID FROM t_Item")}
    finally:
        c.close()


def to_int(v):
    try:
        return int(v)
    except (TypeError, ValueError):
        return None


def main():
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    tmp = OUT + ".tmp"
    if os.path.exists(tmp):
        os.remove(tmp)
    db = sqlite3.connect(tmp)
    db.executescript(SCHEMA)
    cids = client_ids()
    counts = {}
    for name, path, realms, proven, published in LISTS:
        with open(path, encoding="utf8") as f:
            items = json.load(f)
        ids = set()
        for it in items:
            iid = to_int(it["id"])
            if iid is None or iid in ids:
                raise SystemExit(f"bad or duplicate id {it['id']!r} in {name}")
            ids.add(iid)
            dmg = it.get("damage") or {}
            in_client = None if (cids is None or name != "main") else int(iid in cids)
            extra = {k: v for k, v in it.items() if k not in KNOWN}
            db.execute("INSERT INTO items VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)", (
                name, iid, it["name"], realms, proven, in_client, it.get("slot"), it.get("type"), it.get("equipLoc"),
                it.get("quality"), it.get("itemLevel"), it.get("reqLevel"), it.get("sourceCategory"), it.get("source"),
                it.get("version"), it.get("phase"), it.get("dropRate"), it.get("setName"),
                None if it.get("uniqueEquip") is None else int(bool(it["uniqueEquip"])),
                it.get("requiredFaction"), to_int(it.get("baseItemId")), to_int(it.get("affixId")),
                it.get("bloodforgedType"), dmg.get("min"), dmg.get("max"), dmg.get("speed"), it.get("icon"),
                it.get("description"), json.dumps(extra, separators=(",", ":")) if extra else None))
            db.executemany("INSERT INTO item_stats VALUES(?,?,?,?)",
                           [(name, iid, k, v) for k, v in (it.get("stats") or {}).items()])
            db.executemany("INSERT OR IGNORE INTO item_classes VALUES(?,?,?)",
                           [(name, iid, c) for c in it.get("classes") or []])
            db.executemany("INSERT INTO item_set_bonuses VALUES(?,?,?,?)",
                           [(name, iid, int(p), t) for p, t in (it.get("setBonuses") or {}).items()])
        counts[name] = len(items)
        db.execute("INSERT INTO meta VALUES(?,?)", (f"list.{name}", json.dumps(
            {"items": len(items), "realms": realms, "realm_proven": proven, "published": published})))
    db.execute("INSERT INTO meta VALUES('source','BisBeard archive D:\\WoW\\Coa\\DataCOA (captured 2026-11-01)')")
    db.execute("INSERT INTO meta VALUES('note','main list is shared by coa, classic and s10 and not tagged per realm: realm_proven=0. in_coa_client=1 means the id is in the client Item table; that is a hint, not proof.')")
    db.commit()
    db.execute("ANALYZE")
    db.close()
    os.replace(tmp, OUT)
    print(counts, "->", OUT)


main()
