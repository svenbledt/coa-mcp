# CoA Knowledge MCP: design

Date: 2026-11-03. Status: awaiting approval.

Labels: **[Owner]** means the project owner decided it. **[Design call]** means the author of this document chose it and the owner can overrule it. Unlabeled measurements were taken from the local data on 2026-11-03.

## Intent

Conquest of AzerothCore (https://coa-development.org/) is an emulator project that rebuilds how the Ascension server, specifically its Conquest of Azeroth realm, behaves. Agents doing that work need facts: how class spells work, what items do, how dungeons behave, where NPCs stand, and what the client contains. Those facts are scattered today across client files, BisBeard, videos, memories and player caches, and some of the tooling (Spellwork CoA) only exists on one developer's machine.

This server gives everyone the same fast, accurate reference. Any agent can read it without logging in. **[Owner]**

The test of success: an agent working on CoA asks one question and gets back a short, clean answer that says where the fact came from, how far to trust it, and how old it is. It should rarely need to search anywhere else first.

## Goals

- Answer questions about class spells, items, dungeons and encounters, NPC positions, DBC data, client file structure, and new topics that come up later. **[Owner]**
- Return agent-optimized Markdown, organized so lookups are fast. **[Owner]**
- Order sources properly, so the most reliable answer comes first and conflicts stay visible. **[Owner]**
- Include what Spellwork CoA offers for spells, since this server is the one everyone can reach. **[Owner]**
- Update often, so users always get the latest available information. **[Owner]**
- Let agents holding a bearer token write data directly through the MCP. Agents without one get read access only. **[Owner]**
- Cover more than CoA. **[Owner]** At launch that means the realms already in `DataCOA`: CoA, Classic, S10, WotLK and Octo. **[Design call]**

## Realms

**[Design call]** for all of this section:

- Every row, page and observation has a realm field, and it can hold more than one realm or "unknown". New realms can be added without re-tagging.
- All read tools accept a realm filter. A search with no filter returns all realms, with the realm shown on every result, so an agent always sees which realm a fact belongs to.
- WotLK and Octo items have their own item lists, so they're tagged with confidence. The shared 70,838-item list is tagged "CoA, Classic or S10" until something proves a narrower realm.
- The `D:\WoW\Coa` client's data is labeled as coming from that client. Whether a row applies only to CoA isn't assumed.
- WotLK, Octo, Classic and S10 only have BisBeard data for now, since the only client on hand is `D:\WoW\Coa`. The status tool lists this as a known gap.

## Non-goals

- Serving client files. The full MPQ structure is restored only on the owner's machine, and only path information goes to the server. **[Owner]**
- Serving decompiled code from `wowdecomp`, which has no license. Written notes are fine; the code is not. **[Design call]**
- Serving Coa-AzerothCore's own world database as fact about Ascension. It describes the emulator, not the target. This means Spellwork's `query_world_db` and `get_spell_db_overrides` tools are not carried over. **[Design call]**
- A website or dashboard. The audience is agents.
- Moderation workflow or approval queues for writes. Writes go live directly. **[Owner]**
- Serving private player or account data from coa-development.org.

## Anti-goals

These would make it a failure even if it technically works:

- An agent copies an approximate NPC position or a remembered mechanic into the emulator believing it is exact data.
- An answer for CoA is silently really about another realm.
- A bad write corrupts extracted data, or cannot be undone.
- Text written by one agent takes control of another agent reading it.
- Client files leave the owner's machine.

## Constraints and facts

### Hosting

- Runs on `BledtIT` (ORIGIN-ADDRESS-REMOVED), deployed through Coolify. **[Owner]** Confirmed reachable over SSH as root with Coolify and its proxy running; 183 GB disk free, 30 GB RAM of which about 10 GB is available.
- Public HTTPS with no login for reads, at `https://coa-mcp.bledt.dev`. **[Owner]**
- `D:\WoW\Coa` is the client to use, and the reconstructed file list is for this client. **[Owner]** No separate unmodified client is used, so where a project archive wins and no `ascension` copy exists underneath, only the project's copy is available.

### Sources

| Source | Location | Contents | Measured |
|---|---|---|---|
| Client archives | `D:\WoW\Coa\Data` | Full client data | 78 MPQs, 45.5 GB, 770,946 file entries |
| Client file list | `F:\WorkDir\CoA64\docs\mpq-listfile.md` | Names from each archive's `(listfile)` | 770,789 entries, 639,153 unique, 70 archives |
| Extracted DBCs | `F:\WorkDir\azerothcore-wotlk-coa\env\dist\data\dbc` | The client's 368 DBC tables, already merged | See below |
| Spellwork CoA | `F:\WorkDir\SpellWork` (.NET, `SpellWork.Mcp`) | Spell lookups, flag decoding, triggers, visual chains, scaling | 30 of 368 DBCs read by field name |
| BisBeard capture | `D:\WoW\Coa\DataCOA` | 70,838 items, CoA classes and talents, 5,582 map markers, 48 guide texts, plus WotLK and Octo items and realm data | Captured 2026-11-01; item data published 2026-08-27. The only BisBeard source; the server and pipeline never contact BisBeard. **[Owner]** BisBeard is run by others, structured data originating from the owner's project, and consented to this use. **[Owner]** Credit BisBeard on its pages. **[Design call]** |
| Client notes | `F:\WorkDir\CoA64\docs`, `F:\WorkDir\ascension-extensions-reconstruction` | Written findings about the client | Select notes only, see below |
| Project site | https://coa-development.org/ | Realm, armory, bug reports | Public project pages only |
| Player caches | Local `Cache/WDB`, plus files collected from players | Creature, item and quest details | Parsed by the pipeline |
| Observations | Written by token holders | NPC positions, mechanics, from video and memory | Starts empty |

Row counts for DBCs Spellwork cannot read by name, and which fill gaps once their fields are worked out:

| DBC | Rows | | DBC | Rows |
|---|---:|---|---|---:|
| `ItemStat` | 1,513,931 | | `MysticEnchant` | 7,841 |
| `ItemSpells` | 131,722 | | `Talent` | 2,384 |
| `Creature` | 127,343 | | `DungeonEncounter` | 2,080 |
| `Quest` | 18,561 | | `DungeonEncounterExtra` | 2,069 |
| `MythicAffixes` | 13,409 | | `LFGDungeons` | 430 |
| `CharacterAdvancement` | 10,255 | | `Map` | 374 |

Also: `Spell` has 209,510 rows and `Item` has 563,769.

### Known problems in the data

- **Realms are mixed.** BisBeard's 70,838 items are shared with its Classic and S10 realms and aren't tagged per realm. Its guide texts also mix realms and include off-topic articles (VPNs, mice, monitors). The owner doesn't run BisBeard, so a tagged export would have to be requested from them. At launch these items are marked "CoA, Classic or S10" rather than guessed, and the builder checks them against client item data to narrow down what can be proven. **[Design call]**
- **The local client contains the project's own edits.** `D:\WoW\Coa` is a CoA client patch at revision 8 (`CLIENT-RELEASE.json`, `README.txt`): at least `patch-B.MPQ` and `patch-T.MPQ` are project-built, with Descension branding and other changes, and there may be others. A value from such an archive says what the project shipped, not what Ascension does. Every archive therefore has an origin (`ascension`, `project` or `unknown`) in a reviewed config file. Files and DBC rows whose winning copy isn't from an `ascension` archive say so, and the next `ascension` copy is shown alongside when one exists. **[Design call]**
- **Local caches may not be from Ascension.** `D:\WoW\Coa\Cache\WDB\enUS\Pale Reach` may come from a non-Ascension server. Cache folders are imported only when listed in a config file that names their server. **[Design call]**
- **Patch order is not documented.** `mpq-listfile.md` lists holders alphabetically. Showing which copy of a file wins requires the real load order, which must be derived from the client (`CoA64` holds the code that loads archives).
- **Unresolved names.** The list marks 4,911 names that didn't resolve to an archive entry (3,610 in `patch.MPQ`, 1,234 in `patch-2.MPQ`). It may be a locale or checking artifact, or the real names may differ. `patch-ZG.MPQ` holds 2 files with no known names and isn't in the list. Seven other archives hold only internal entries.
- **Extraction is unproven.** Only archive tables were read. Whether every file decompresses is unknown until restoration runs.
- **Spawn positions and boss scripts are normally server-side.** Client data gives encounter lists, creature details and world geometry, not NPC spawns or AI. These come from observations. **[Owner]**
- **DBC field meanings are mostly unknown.** 338 tables have no field names yet.

## The design

### Two machines

```
Owner's machine                        BledtIT (Coolify)
---------------                        -----------------
MPQs -> restored file tree (private)
     -> path list + effective copy  -> generated layers (versioned, read-only)
DBCs -> parsed tables               ->            |
BisBeard, notes, site -> pages      ->            v
                                          MCP server  <- anyone reads
                                              ^
                                       writable store <- token holders write
```

Nothing from the restored file tree reaches the server except path information and data parsed from DBCs. **[Owner]** A check in the publish step must fail if a layer contains anything else from client files, such as textures, models, audio or raw `.dbc` files. **[Design call]**

### Build pipeline (runs on the owner's machine)

1. **Restore** every archive to a private folder, keeping the archive layout, resolving the patch order, and reporting files that failed or have no name. Goal: complete structure, with any gap listed rather than hidden. **[Owner]**
2. **Parse** all 368 DBCs into a database. Tables without known fields stay readable by position, with field names added as they get worked out.
3. **Import** BisBeard data, Spellwork's spell logic, and selected notes.
4. **Generate** search indexes as new versioned layers.
5. **Publish** changed layers to BledtIT and switch over in one step, so readers never see a half-finished update.

Update timing: **[Design call]**

- **Client layer:** rebuilt on the owner's machine when Ascension patches, started by hand, since it needs the local client.
- **BisBeard layer:** built from `D:\WoW\Coa\DataCOA` and rebuilt only when that folder changes. Nothing fetches from BisBeard automatically. Its age is shown in answers, since it won't track live changes on its own.
- **Observations:** live immediately.
- The status tool reports the version and date of each layer rather than one release number.

### How information is stored

Markdown is what agents receive, not what everything is stored as. **[Owner asked for optimized MD; storage split is a Design call.]** With up to 1.5 million rows in one table, a file per row is impractical and can't answer questions like "every item with this stat".

- **Generated data:** read-only SQLite databases with full-text search, one per source layer (client, BisBeard, notes). Each layer has its own version and is replaced whole, so BisBeard can refresh without rebuilding millions of client rows. **[Design call]**
- **Rendered Markdown:** rows are rendered to Markdown when requested, always in the same layout per type.
- **Writable store:** a separate database holding observations and curated pages (mechanics, class overviews, dungeon guides), as Markdown with full history. Layer updates never touch it. Must be backed up. **[Design call]**
- **Pages that start from generated content:** the pipeline may create a curated page once. After that the writable store owns it, and later builds never overwrite it. Generated facts stay in their layers and appear beside the page. **[Design call]**

Every page has the same small header: id, type, realm, trust level, source, date, layer version. Pages link to related pages by id, such as boss to spells to loot.

### Source order

Each fact keeps its own trust level. Answers show the highest first and never merge conflicting values silently. **[Design call on order; Owner asked for proper ordering.]**

| Level | Name | Meaning | Examples |
|---|---:|---|---|
| 1 | `client` | Extracted from client files | DBC rows, file paths |
| 2 | `derived` | Curated data or written analysis based on real data | BisBeard item data, client notes |
| 3 | `cache` | Exact values seen in a player's cache | Creature and quest details |
| 4 | `observed` | Seen in a video or in play, with evidence | Positions, mechanics |
| 5 | `memory` | Someone remembers it | Always marked unconfirmed |

Rules:

- Every answer carries its trust level, source and age.
- If two sources disagree, both appear, with the higher level first. Writes may include an optional structured field and value, and automatic conflict notes only appear where those exist. Free-text observations are shown side by side without an automatic conflict note. **[Design call]**
- All positions are labeled approximate, because no current source provides exact spawn coordinates. This includes BisBeard's map markers, which are percentages on a map image and mark Worldforge and Mystic Enchant locations, not NPCs. The schema stores both map percentages and world coordinates (map, x, y, z), so an exact source can be added later. **[Design call]**
- Project site pages describe the emulator and are labeled as such. They never count as evidence about Ascension.
- Newer observations don't erase older ones.

### Topics

| Topic | Content | Main sources |
|---|---|---|
| Spells | Full spell detail, flags, triggers, visuals, scaling | DBCs, Spellwork logic, observations |
| Classes | Classes, specs, talents, essences, Character Advancement | DBCs, BisBeard realm data |
| Items | Stats, sets, sources, Mystic Enchants, Worldforge locations | DBCs, BisBeard |
| Dungeons and encounters | Maps, bosses, mechanics, loot | DBCs, BisBeard, observations |
| NPCs | Creature details, positions | DBCs, cache, observations |
| DBC | Any table, any row | DBCs |
| Client files | Path lookup and browsing | MPQ path list |
| Client notes | What's known about client behavior | Selected notes |

### Client notes

Include written findings that explain client behavior, starting with `mpq-listfile.md`, `2026-11-03-client-dbc-patch-notes.md`, `2026-10-29-coa-module-notes.md`, `2026-10-25-coa64-world-findings.md` and `2026-10-25-coa64-rulings.md`. Leave out plans, ledgers, decompiled code, and anything with credentials or private paths. The builder reviews each note before it publishes. **[Design call]**

### Reading

Keep the tool list small, because agents pay for every tool they load. **[Design call]** The builder decides final names. Needed abilities:

- Search across everything, filterable by topic and realm, returning short results with ids.
- Get one thing by id or exact name, as a Markdown page, with optional sections to keep it short.
- Query DBC tables: list tables, get a row, filter rows, with paging and a hard result cap.
- Search and browse client paths, showing size, holders and the winning copy.
- Spell helpers matching Spellwork CoA: compare spells, decode flags, triggers, visual chain, scaling.
- Server status: version and date of each layer, known gaps.

Responses should be small by default with a way to ask for more.

### Writing

Agents holding a bearer token can write through the MCP, and writes go live immediately. **[Owner]** Agents without a valid token can read but can't write.

**[Design call]** for everything below:

- **Tokens:** one per person or agent, stored only as hashes, individually revocable. The server needs a simple admin way to issue and revoke them. No web UI required.
- **What can be written:** observations, notes, corrections, and curated pages. Extracted data (DBC rows, file paths) can't be changed, but can get a note saying a row looks wrong.
- **Required on each write:** subject, content, trust level, and evidence. `observed` requires a link or description, with a timestamp for videos.
- **Levels a token can claim:** only `observed` and `memory`. `client`, `derived` and `cache` come only from the pipeline, so a remembered fact can't outrank real evidence by being mislabeled.
- **Player caches:** WDB files are collected by the owner and parsed by a pipeline step on the owner's machine, which is what produces level `cache`. Uploading WDB files through the MCP is out of scope for now.
- **History:** nothing is truly deleted. Every change records author and time. A single write, or all writes by one token, can be undone.
- **Limits:** size and rate limits per token.
- **Untrusted text:** answers clearly separate written text from extracted data, name the author, and mark the text as data rather than instructions. Tool descriptions tell reading agents the same.
- **Secrets:** tokens must never appear in logs or in responses.

Reads never need a token, and sending one doesn't change what a reader sees, apart from unlocking writes.

### Public reads

The endpoint is open to the internet with no login, so it needs per-IP rate limits and result caps, to keep anyone from overloading the server or pulling the entire database through one query. **[Design call]**

## Technology

- **Language: .NET for the server and the pipeline**, because `SpellWork.Mcp` is already .NET and its spell logic can be reused rather than rewritten. **[Design call]**
- **Spellwork runs only in the pipeline.** It reads raw `.dbc` files, which may not reach the server, so its output is generated into the spell data and the server never loads Spellwork. The CoA variant lives in the `F:\WorkDir\SpellWork\.claude\worktrees\sw-coa` worktree. **[Design call]**
- **Protocol:** MCP over Streamable HTTP. **[Design call]**
- **Storage:** SQLite, as described above. **[Design call]**
- **Deployment:** Docker image through Coolify, with a persistent volume for generated layers and the writable store. **[Design call]**

## Left to the builder

- Exact tool names, schemas and Markdown layouts per type.
- The MPQ library, and how releases upload to BledtIT.
- Search ranking, indexes and rate limit values.
- Order of work, though a read-only server with items and spells should reach real use before everything else is done. **[Design call]**
- Field naming approach for the 338 unnamed DBC tables, and how much of it comes first.
