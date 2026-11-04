# Ascension client MPQ file list: how it was built and what it can't say

Research note, 2026-10-25. The list was built from the archives of an installed Conquest of Azeroth client by reading each archive's internal `(listfile)` and resolving every listed name against the archive's hash table.

This is only the explanatory part of the note. The per-archive tables, the extension and folder counts and the DBC holder table are left out: the `client_paths` tool serves the same file names with the winning archive per path, and the `dbc_tables` tool serves the DBC tables.

## What this is and its limits

- MPQ archives do not index their contents by name. They carry a hash table, and an optional `(listfile)` entry that names the files. The note is the union of those `(listfile)` entries across every archive in the client's `Data` folder.
- A file whose name is missing from `(listfile)` is **not** in the list even though it is in the archive. Some Ascension patches have incomplete listfiles (for example, custom map terrain is findable by name but not always listed). To check a specific path, probe the name hash directly; to find unlisted content, scan the block table by content.
- Archives with no readable `(listfile)`: none. Archives whose listfile is empty or tiny exist (for example `patch-P.mpq` lists 1 entry and `patch-T.MPQ` lists 2).
- A name can appear in several archives. WoW's patch chain makes later-loaded archives override earlier ones, so a file's effective copy is not necessarily the first one listed; load order decides, not alphabetical order.
- Names are shown as stored. Paths are case-insensitive in MPQ, so entries are grouped case-insensitively.

## Totals at the time of the note

- Archives read: 70
- Listfile entries (with duplicates across archives): 770,789
- Unique file names (case-insensitive): 639,153
- Unique `.dbc` names: 368 (992 entries counting duplicates across archives)

## Archives that stand out

- `patch.MPQ` has 41,160 listed entries, of which 3,610 could not be resolved by name in its hash table.
- `patch-2.MPQ` has 11,811 listed entries, of which 1,234 could not be resolved by name.
- `patch-3.MPQ` (39), `enUS/patch-enUS.MPQ` (26) and `enUS/patch-enUS-2.MPQ` (2) also list names that don't resolve.
- The largest by listed entries are `patch-TA.MPQ` (91,960), `common.MPQ` (83,670), `patch-I.MPQ` (76,062), `patch-CHA.MPQ` (49,120), `patch-WC2.MPQ` (49,779) and `patch-O.MPQ` (46,625).

## File types and folders

By entries, with cross-archive duplicates: `.blp` 402,213; `.skin` 115,056; `.m2` 110,425; `.anim` 37,991; `.ogg` 34,989; `.wmo` 23,454; `.wav` 20,611; `.adt` 17,339. Largest top-level folders: `item` 190,586; `world` 137,007; `interface` 102,718; `character` 99,589; `textures` 71,059; `sound` 57,378; `creature` 56,241; `spells` 33,423.
