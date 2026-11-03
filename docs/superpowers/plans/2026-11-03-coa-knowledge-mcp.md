# CoA Knowledge MCP Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A public, no-login MCP on BledtIT that answers questions about Ascension for Conquest of AzerothCore development, with bearer-token writes.

**Architecture:** A .NET pipeline on the owner's machine turns client data, Spellwork output, `DataCOA` and notes into versioned read-only SQLite layers, and publishes them to BledtIT. An ASP.NET Core MCP server reads those layers plus one writable SQLite store, and renders Markdown on request. The server never loads Spellwork, raw DBCs or client files.

**Tech Stack:** .NET 10 (`net10.0`), `ModelContextProtocol.AspNetCore` 2.x, `Microsoft.Data.Sqlite` with FTS5, xUnit, StormLib (native, pipeline only), Docker, Coolify.

**Spec:** `docs/superpowers/specs/2026-11-03-coa-knowledge-mcp-design.md`

## Global Constraints

- Reads need no login. Writes need a valid bearer token.
- Client files never reach the server: only paths and parsed data. The publish guard enforces this.
- Trust levels, in order: `client`, `derived`, `cache`, `observed`, `memory`. Tokens may claim only `observed` and `memory`.
- All positions are labeled approximate.
- Project site pages and `project`-origin archives never count as evidence about Ascension.
- No Coa-AzerothCore world database, no decompiled code, no private player data.
- Nothing contacts BisBeard. BisBeard data comes only from `D:\WoW\Coa\DataCOA`, and its pages credit BisBeard.
- Tokens never appear in logs or responses.
- Hard caps: 100 rows per read, 32 KB per write.

## Review Focus

1. A query with SQL metacharacters or FTS5 syntax (`"`, `*`, `NEAR(`, `'; DROP`) returns results or a clean error, never a server fault. Tested in Task 4 and Task 7.
2. Written text that contains a fake closing marker can't escape its "untrusted" wrapper. Tested in Task 10.
3. Behind Coolify's proxy, rate limits apply per real client IP, and a spoofed `X-Forwarded-For` from outside can't bypass them. Tested in Task 4.
4. A layer swap during live reads doesn't fail in-flight requests, and a corrupt or partial upload leaves the old version live. Tested in Task 5.
5. A malformed, wrong or missing token still allows reads and refuses writes, with no token text in the error. Tested in Task 10.

---

## Repository layout

```
CoaMcp.sln                  (server + core + tests; used by Docker)
CoaMcp.Pipeline.sln         (adds pipeline; needs local SpellWork)
src/CoaMcp.Core/            Layer format, ids, trust, realms, Markdown helpers
src/CoaMcp.Server/          MCP server
src/CoaMcp.Pipeline/        CLI: restore, paths, dbc, spells, bisbeard, notes, wdb, publish
config/archives.json        Archive origin and order overrides
config/bisbeard-guides.json Realm or exclusion per guide
config/notes.json           Published notes
config/wdb-sources.json     Approved cache folders
tests/CoaMcp.Core.Tests/, tests/CoaMcp.Server.Tests/, tests/CoaMcp.Pipeline.Tests/
Dockerfile, docker-compose.yml, .gitignore, README.md
```

Ids are `type:key`: `item:10531`, `spell:133`, `class:barbarian`, `marker:<id>`, `note:<slug>`, `dbc:Spell:133`, `entry:<n>`.

## Milestone 1: items and spells in real use (Tasks 1-5)

### Task 1: Layer format

**Files:**
- Create: `CoaMcp.sln`, `.gitignore`, `src/CoaMcp.Core/{Trust.cs,Realm.cs,Entity.cs,LayerWriter.cs,LayerReader.cs,FtsQuery.cs}`
- Test: `tests/CoaMcp.Core.Tests/LayerTests.cs`

**Interfaces:**
- Produces:
  - `enum Trust { Client=1, Derived=2, Cache=3, Observed=4, Memory=5 }`
  - `record RealmSet(IReadOnlyList<string> Realms, bool Proven)`, with `string Label()`. `["coa","classic","s10"]` unproven renders `CoA, Classic or S10`.
  - `record Entity(string Id, string Type, string Title, RealmSet Realm, Trust Trust, string Source, DateOnly SourceDate, string BodyMd, string? DataJson, IReadOnlyList<Link> Links)`
  - `record Link(string ToId, string Kind)`
  - `LayerWriter(path, name, version)`: `Add(Entity)`, `Execute(string ddl)`, `Finish()`. `Finish()` builds FTS, runs `VACUUM`, and writes the `layer_meta` table (`name, version, built_at, source_date`).
  - `LayerReader(path)`: opens read-only and immutable; `Get(id)`, `Search(FtsQuery, type?, realm?, limit, offset) -> IReadOnlyList<SearchHit>`, `Meta`.
  - `FtsQuery.FromUser(string)`: always quotes user terms into safe FTS5 phrases.

- [ ] **Step 1: Write the failing tests.** `Roundtrip_ReturnsEntity`, `Search_FindsByTitleAndBody`, `Search_FiltersByRealmAndType`, `FromUser_NeutralizesFtsSyntax` (inputs `"`, `a*`, `NEAR(a b)`, `'; DROP TABLE x`, empty string do not throw and return zero or more hits), `Label_RendersUnprovenAsOr` (`CoA, Classic or S10`).
- [ ] **Step 2: Run `dotnet test tests/CoaMcp.Core.Tests`.** Expected: FAIL, types not defined.
- [ ] **Step 3: Implement the types above.** Schema: `entities(id PK, type, title, realm_json, realm_proven, trust, source, source_date, body_md, data_json)`, `links(from_id, to_id, kind)`, `entities_fts` (external content over `title, body_md`), `layer_meta`.
- [ ] **Step 4: Run the tests.** Expected: PASS.
- [ ] **Step 5: Commit** `feat: add layer format`, including the spec and plan.

### Task 2: BisBeard layer

**Files:**
- Create: `src/CoaMcp.Pipeline/{Program.cs,Bisbeard/ItemImporter.cs,Bisbeard/RealmImporter.cs,Bisbeard/MarkerImporter.cs,Bisbeard/GuideImporter.cs}`, `config/bisbeard-guides.json`
- Test: `tests/CoaMcp.Pipeline.Tests/BisbeardTests.cs`, with small JSON fixtures in `tests/CoaMcp.Pipeline.Tests/fixtures/`

**Interfaces:**
- Consumes: `LayerWriter`, `Entity`, `RealmSet`.
- Produces: CLI `coamcp-pipeline bisbeard --data D:\WoW\Coa\DataCOA\data --out <file>`, writing layer `bisbeard`. Entity types `item`, `class`, `spec`, `marker`, `guide`.

- [ ] **Step 1: Write the failing tests.**
  - `Item_MainList_RealmIsUnprovenCoaClassicS10`, using item 10531 from a fixture: title `Stoneclad Libram`, `Trust.Derived`, body includes stats, source `Molten Core - Baron Geddon`, and credits BisBeard.
  - `Item_WotlkList_RealmIsProvenWotlk`.
  - `Marker_BodyLabelsApproximate`, asserting the word `approximate` and that the location is given as map percentages.
  - `Guide_ExcludedInConfig_IsNotImported` (`best-vpn-online-games-world-of-warcraft`).
  - `Guide_NotInConfig_FailsTheBuild`, so new guides get a review.
  - `RealmData_DropsFunctionPlaceholders`, so no `[function` text reaches a page.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement the importers.** Inputs: `api/items_all.json`, `realms/{wotlk,octo}/api/items_all.json`, `realm/{coa,classic,s10,wotlk,octo}RealmData.json`, `atlas/markers_*.json`, `guides_text/*.txt`. Source date for items: 2026-08-27 for the main list, 2026-07-16 for WotLK, 2026-08-10 for Octo. Write `config/bisbeard-guides.json` covering all 48 files, each with a realm or `excluded`. Exclude general hardware, VPN, site-policy and contact pages.
- [ ] **Step 4: Run the tests, then the real import.** Expected: PASS. `item` count equals 70,838 + 3,885 + 5,416; `marker` count equals 5,582.
- [ ] **Step 5: Commit** `feat: import BisBeard data`.

### Task 3: Spell layer

**Files:**
- Create: `src/CoaMcp.Pipeline/Spells/SpellExporter.cs`, `src/CoaMcp.Pipeline/Spells/EnumExporter.cs`, `src/CoaMcp.Pipeline/Spells/ScalingExporter.cs`, `CoaMcp.Pipeline.sln`
- Modify: `src/CoaMcp.Pipeline/CoaMcp.Pipeline.csproj`
- Test: `tests/CoaMcp.Pipeline.Tests/SpellExportTests.cs`

**Interfaces:**
- Consumes: `SpellTools`, `VisualTools`, `DecoderTools`, `SpellDataService` and `McpServerConfig` from `SpellWork.Mcp`, by project reference to `$(SpellWorkRoot)\SpellWork.Mcp\SpellWork.Mcp.csproj`. `SpellWorkRoot` defaults to `F:\WorkDir\SpellWork\.claude\worktrees\sw-coa`.
- Produces: CLI `coamcp-pipeline spells --dbc <dir> --out <file>`, writing layer `spells`. One `spell:<id>` entity per spell, with Markdown body and sections `summary`, `effects`, `triggers`, `visuals`, and `data_json` holding the fields used by comparison. Tables `enums(name, value, label)` and `flag_kinds(kind, enum_name)`.

The pipeline never sets `SPELLWORK_MYSQL_*`, so emulator database overrides can't enter the data.

- [ ] **Step 1: Write the failing tests** using the `FakeDbc` fixtures from SpellWork's tests: `Spell_HasSections`, `Spell_TrustIsClient`, `Spell_RealmIsClientLabeled`, `Export_DoesNotContainDbOverrides`, `Enums_ExportFlagKinds`.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement the exporters.** Call the existing tools per spell id with `format="json"`, and render the result to Markdown. Export scaling inputs per spell to `spell_scaling_inputs`, plus the `ScalingStatValues` and `ScalingStatDistribution` tables needed by Task 4's port of the formula. Write `golden_scaling` from `calculate_spell_scaling` for 20 representative spells at levels 1, 40 and 80, for Task 4 to check against.
- [ ] **Step 4: Run the tests, then the real export with `--dbc F:\WorkDir\azerothcore-wotlk-coa\env\dist\data\dbc`.** Expected: PASS, 209,510 spell entities. Task 8 changes this source to the client's winning copies.
- [ ] **Step 5: Commit** `feat: export spells from Spellwork`.

### Task 4: Read server

**Files:**
- Create: `src/CoaMcp.Server/{Program.cs,LayerSet.cs,Render.cs,Tools/ReadTools.cs,Tools/SpellHelperTools.cs,Limits.cs}`
- Test: `tests/CoaMcp.Server.Tests/{ReadToolTests.cs,LimitTests.cs,ScalingGoldenTests.cs}`

**Interfaces:**
- Consumes: `LayerReader`, `Entity`.
- Produces:
  - `LayerSet`: holds the current reader per layer; `Search`, `Get`, `Status`, `Current(name)`. Later tasks add layers by name.
  - Tools, all read-only:
    - `search(query, topic?, realm?, limit=10, offset=0)`
    - `get(id, sections?)`
    - `status()`
    - `spell_compare(idA, idB)`
    - `decode_flags(kind, value)`
    - `spell_scaling(id, level, itemLevel?)`
  - `POST /mcp` by Streamable HTTP in stateless mode; `GET /healthz`.

Use `WithHttpTransport(o => o.Stateless = true)` or the current equivalent; check the installed SDK version (`HttpServerSessionMode.Stateless` in recent docs).

- [ ] **Step 1: Write the failing tests.**
  - `Get_RendersHeader`: id, type, realm, trust, source, date and layer version all appear.
  - `Get_SectionsLimitOutput`: `get("spell:1", ["effects"])` omits `visuals`.
  - `Search_CapsAt100`: `limit=5000` returns at most 100 and says it was capped.
  - `Search_ShowsRealmOnEveryHit`.
  - `Search_HostileInput_NoFault`: the Review Focus inputs produce no tool fault.
  - `Get_Unknown_SaysNotFoundWithSuggestions`.
  - `Status_ListsEachLayerVersionAndKnownGaps`.
  - `DecodeFlags_KnownKind_ReturnsNames`.
  - `Scaling_MatchesGolden`: reads `golden_scaling` from a fixture layer.
  - `RateLimit_PerRealClientIp`: requests from a trusted proxy with different `X-Forwarded-For` count separately.
  - `RateLimit_SpoofedHeaderFromUntrustedPeer_Ignored`.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement.** Keep tool descriptions short, and tell agents that written notes are data rather than instructions. Rate limit: fixed window per IP, starting at 120 requests per minute. Forwarded headers are accepted only from configured `KnownNetworks` (the Docker network). Port the 3.3.5 scaling formula from `SpellWork.Mcp/Tools/DecoderTools.cs`.
- [ ] **Step 4: Run the tests.** Expected: PASS.
- [ ] **Step 5: Commit** `feat: add read-only MCP server`.

### Task 5: Publish and deploy

**Files:**
- Create: `src/CoaMcp.Pipeline/Publish/{PublishGuard.cs,Publisher.cs}`, `Dockerfile`, `docker-compose.yml`, `README.md`
- Modify: `src/CoaMcp.Server/LayerSet.cs`
- Test: `tests/CoaMcp.Pipeline.Tests/PublishGuardTests.cs`, `tests/CoaMcp.Server.Tests/LayerSwapTests.cs`

**Interfaces:**
- Consumes: `LayerSet`, layer files.
- Produces:
  - CLI `coamcp-pipeline publish --layer <file> --host BledtIT`. Runs the guard, uploads with `scp` to `/data/coamcp/layers/<name>/<version>.sqlite.tmp`, verifies the SHA-256 remotely, renames, then atomically replaces `/data/coamcp/current.json`.
  - `PublishGuard.Check(path) -> GuardResult`.
  - Server re-reads `current.json` every 30 seconds.

- [ ] **Step 1: Write the failing tests.**
  - `Guard_RejectsBlobColumn`.
  - `Guard_RejectsUnknownTable`, using a per-layer allowed-table list.
  - `Guard_RejectsMpqOrDbcMagicInText`: base64-free scan for `WDBC`, `MPQ\x1a`, `BLP2`, `MD20` at field starts.
  - `Swap_InFlightRequestSurvives`: a reader held during a swap finishes.
  - `Swap_CorruptLayer_KeepsOldVersion`: truncated file; `status` still shows the old version and logs an error.
  - `Swap_MissingLayerFile_KeepsOldVersion`.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement** the guard, publisher and swap. The Dockerfile builds only `CoaMcp.sln` and runs as non-root on port 8080. Compose bind-mounts `/data/coamcp` to `/data`. Validate new layers with `PRAGMA quick_check` and a meta read before swapping.
- [ ] **Step 4: Run the tests.** Expected: PASS.
- [ ] **Step 5: Deploy.** Confirm the DNS record for `coa-mcp.bledt.dev` points at BledtIT and the git remote exists. In Coolify, create a Docker Compose app from the repo with domain `coa-mcp.bledt.dev`. Publish `bisbeard` and `spells`.
- [ ] **Step 6: Verify from outside.** With an MCP client against `https://coa-mcp.bledt.dev/mcp`: `get("item:10531")` returns Stoneclad Libram, `get("spell:133")` returns a page, and `status()` shows both layers.
- [ ] **Step 7: Commit** `feat: publish layers and deploy`.

## Milestone 2: complete client knowledge (Tasks 6-9)

### Task 6: MPQ restore, load order and origin

**Files:**
- Create: `src/CoaMcp.Pipeline/Mpq/{StormLib.cs,ArchiveOrder.cs,MpqRestorer.cs}`, `config/archives.json`
- Test: `tests/CoaMcp.Pipeline.Tests/ArchiveOrderTests.cs`, `tests/CoaMcp.Pipeline.Tests/RestorerTests.cs`

**Interfaces:**
- Produces:
  - `ArchiveOrder.Resolve(dataDir, config) -> IReadOnlyList<ArchiveInfo>`, highest priority first. `record ArchiveInfo(string RelPath, int Priority, string Origin, string? Realm)`; `Origin` is `ascension`, `project` or `unknown`.
  - CLI `coamcp-pipeline restore --data D:\WoW\Coa\Data --out <dir>`, writing `<dir>/<archive>/<path>` and `<dir>/manifest.sqlite`: `files(archive, path, path_lc, size, crc, status)`, where `status` is `ok`, `failed` or `unnamed`.

`--out` must be outside the repo. `.gitignore` also blocks `*.MPQ`, `*.dbc`, `*.blp`, `*.m2`.

- [ ] **Step 1: Derive the real order, and record it in `config/archives.json` with file and line evidence.** Sources: `F:\WorkDir\CoA64\WoW\Source\Client.cpp:167-196` and `:1400-1470`, plus `F:\WorkDir\ascension-extensions-reconstruction\src\Ascension\AscRealmData.cpp` (realm archives such as `area-52/patch-D.MPQ`, and HD archives such as `patch-Q.MPQ`). Set `patch-B.MPQ` and `patch-T.MPQ` to `project`, and everything else to `unknown` until the owner confirms.
- [ ] **Step 2: Write the failing tests.** `Order_LaterPatchBeatsBase`, `Order_RealmArchiveHandled` (`area-52/patch-D.MPQ`), `Order_EveryArchiveOnDiskIsClassified`: an unlisted archive fails with its name. `Restore_FixtureArchive_ExtractsAndManifests`, using a small MPQ built with StormLib. `Restore_FailedFile_RecordedNotFatal`. `Restore_UnnamedEntry_RecordedAsUnnamed`.
- [ ] **Step 3: Run them.** Expected: FAIL.
- [ ] **Step 4: Implement.** Build StormLib from source with CMake into `tools/stormlib` (gitignored), and P/Invoke it. For the 4,911 unresolved names, retry across all locales before calling them unresolved.
- [ ] **Step 5: Run the tests, then the real restore of `D:\WoW\Coa\Data`.** Expected: PASS; the report lists 78 archives, total entries near 770,946, and every failed or unnamed file. Check `manifest.sqlite` against the listfile counts per archive, and record any gap in `docs/client-restore-report.md`.
- [ ] **Step 6: Reconstruct the listfile for this client.** Add test `Candidates_ResolveUnnamedEntry`: a name found elsewhere that matches an entry's two name hashes fills in that entry. Implement `coamcp-pipeline listfile --manifest <file> --out <dir>`, trying candidate names from every other archive's listfile, then path strings found in restored `.dbc`, `.m2`, `.wmo`, `.adt`, `.wdt`, `.lua`, `.xml` and `.toc` files. It writes one `<archive>.listfile.txt` per archive plus `client-listfile.txt` (the union) to `--out`, marks each name's source in `manifest.sqlite`, and reports entries still unnamed. Never guess a name that doesn't match both hashes. Run it with `--out D:\WoW\Coa\Listfiles`, as the owner wants these lists in the client's folder. **Never write into `D:\WoW\Coa\Data` or modify an MPQ.**
- [ ] **Step 7: Commit** `feat: restore MPQ archives and reconstruct the listfile`, with `config/archives.json` and the report. The listfile output stays outside the repo; Task 7 publishes the paths.

### Task 7: Client paths

**Files:**
- Create: `src/CoaMcp.Pipeline/Mpq/PathExporter.cs`, `src/CoaMcp.Server/Tools/PathTools.cs`
- Test: `tests/CoaMcp.Pipeline.Tests/PathExportTests.cs`, `tests/CoaMcp.Server.Tests/PathToolTests.cs`

**Interfaces:**
- Consumes: `manifest.sqlite`, `ArchiveOrder.Resolve`.
- Produces: layer `paths` with `paths(path, ext, size, winner_archive, winner_origin, ascension_winner?, holders_json)` and a trigram FTS. CLI `coamcp-pipeline paths`. Tool `client_paths(query?, prefix?, ext?, limit=50, offset=0)`.

- [ ] **Step 1: Write the failing tests.** `Winner_IsHighestPriority`, `Winner_ProjectArchive_ShowsAscensionCopy`, `Export_HasNoFileContent`, `Paths_CaseInsensitive`, `Prefix_Browses_DBFilesClient`, `Paths_HostileInput_NoFault`, `Status_ReportsUnnamedAndFailedCounts`.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement.** Add `paths` to the guard's allowed tables.
- [ ] **Step 4: Run the tests and export.** Expected: PASS, about 639,153 unique paths plus any restored names.
- [ ] **Step 5: Commit** `feat: serve client paths`.

### Task 8: DBC layer

**Files:**
- Create: `src/CoaMcp.Pipeline/Dbc/{DbcReader.cs,FieldGuess.cs,DbcExporter.cs}`, `config/dbc-fields/` (one JSON per named table), `src/CoaMcp.Server/Tools/DbcTools.cs`
- Modify: `src/CoaMcp.Pipeline/Spells/SpellExporter.cs` (source)
- Test: `tests/CoaMcp.Pipeline.Tests/DbcTests.cs`, `tests/CoaMcp.Server.Tests/DbcToolTests.cs`

**Interfaces:**
- Consumes: restored winning `.dbc` files, chosen by `ArchiveOrder`.
- Produces:
  - Layer `dbc`: one table `t_<name>` per DBC with columns from `config/dbc-fields` or `f0..fn`, plus `dbc_tables(name, rows, fields, named, winner_archive, origin)` and `dbc_fields(table, idx, name, type, guessed)`. Where the winner isn't `ascension`, the `ascension` copy is exported as `t_<name>__ascension`.
  - Tools: `dbc_tables(filter?)` and `dbc_rows(table, where?, columns?, limit=20, offset=0)`, with `where` as a list of `{field, op, value}`, ops `= != < > <= >= contains`.
  - `get("dbc:Spell:133")` and search on a table's name.

Row Markdown says `guessed` for inferred types. Filters never accept raw SQL: table and field names are checked against `dbc_tables` and `dbc_fields`, and values are bound as parameters.

- [ ] **Step 1: Write the failing tests.** `Reader_ParsesHeaderAndStrings`, `Reader_CountMatchesHeader`, `Guess_StringVsIntVsFloat`, `Rows_FilterAndPage`, `Rows_UnknownField_CleanError`, `Rows_InjectionInFieldName_Refused`, `Rows_CapsAt100`, `Row_FromProjectArchive_ShowsAscensionRow`, `Tables_All368Listed`.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement.** Seed `config/dbc-fields` from the 30 tables Spellwork models, and from `F:\WorkDir\ascension-extensions-reconstruction` where it proves names. Export spells from the same winning DBC folder. Add indexes on id columns and `Name` fields.
- [ ] **Step 4: Run tests and export.** Expected: PASS. Row counts match: `ItemStat` 1,513,931, `Creature` 127,343, `Spell` 209,510, `Item` 563,769. If they differ from those measured in the azerothcore folder, list the differences in `docs/client-restore-report.md`, since that shows where the server's DBCs drifted from the client.
- [ ] **Step 5: Narrow item realms.** Add test `Item_NotInClient_DropsCoa`, then in the BisBeard importer drop `coa` from an item's realm when its id isn't in client `Item`. Rerun Task 2's tests.
- [ ] **Step 6: Commit** `feat: serve all client DBC tables`.

### Task 9: Notes, project site and player caches

**Files:**
- Create: `src/CoaMcp.Pipeline/Notes/NoteImporter.cs`, `src/CoaMcp.Pipeline/Notes/SecretScan.cs`, `src/CoaMcp.Pipeline/Wdb/WdbImporter.cs`, `config/notes.json`, `config/wdb-sources.json`
- Test: `tests/CoaMcp.Pipeline.Tests/{NotesTests.cs,WdbTests.cs}`

**Interfaces:**
- Produces: layers `notes` (types `note`, `project`) and `cache` (types `cache-creature`, `cache-item`, `cache-quest`). CLI `coamcp-pipeline notes`, `coamcp-pipeline wdb`.

- [ ] **Step 1: Write the failing tests.** `Note_NotInManifest_Skipped`, `Note_WithLocalPath_FailsBuild` (`F:\WorkDir\...`), `Note_WithSecretPattern_FailsBuild`, `Note_TrustIsDerived`, `ProjectPage_LabeledNotEvidence`, `Wdb_FolderNotListed_Skipped`, `Wdb_ParsesCreatureFixture`, `Wdb_TrustIsCache_NamesServer`, `Wdb_UnknownVersion_FailsWithName`.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement.** Seed `config/notes.json` with the five notes named in the spec, reviewing each by hand and sanitizing paths in a copy under `notes/`. Project pages: public pages of https://coa-development.org/ only, never account pages. `config/wdb-sources.json` starts empty; the owner adds folders and their server.
- [ ] **Step 4: Run tests and publish all layers.** Expected: PASS. `status` shows all layers, and the guard passes.
- [ ] **Step 5: Commit** `feat: add notes, project pages and cache import`.

## Milestone 3: agent writes (Tasks 10-11)

### Task 10: Tokens and writes

**Files:**
- Create: `src/CoaMcp.Server/Writes/{WriteStore.cs,TokenAuth.cs,WriteEntry.cs}`, `src/CoaMcp.Server/Tools/WriteTools.cs`
- Modify: `src/CoaMcp.Server/{Program.cs,Render.cs,Tools/ReadTools.cs}`
- Test: `tests/CoaMcp.Server.Tests/{TokenAuthTests.cs,WriteToolTests.cs,MergeRenderTests.cs}`

**Interfaces:**
- Consumes: `LayerSet`, `Render`, `Trust`.
- Produces:
  - `/data/writable/store.sqlite`, with append-only `entries(id, subject_id, kind, realm_json, trust, content_md, evidence_json, field, value, position_json, author, created_at, supersedes_id, undone_at, undone_by)`.
  - Tools marked `[Authorize]`: `write_entry(author, subject, kind, realm, trust, content, evidence, field?, value?, position?)` with `kind` as `observation|note|correction|page`, and `undo_entry(id)`.
  - The token comes from the environment variable `COA_MCP_BEARER_TOKEN`, is compared in constant time (hash both sides, then `CryptographicOperations.FixedTimeEquals`), and is never stored in the database.
  - `get` and `search` include entries through `WriteStore`.

`position` is `{map, x?, y?, z?, leftPct?, topPct?}`.

- [ ] **Step 1: Write the failing tests.**
  - Auth: `Read_NoToken_Works`, `Read_InvalidToken_Works`, `Write_NoToken_Refused`, `Write_WrongToken_Refused`, `Write_MalformedToken_Refused_NoTokenInError`, `Logs_NeverContainToken`, `EnvUnset_WritesDisabled_ReadsWork`, `EnvUnderThirtyTwoChars_WritesDisabled_LogsWarning`.
  - Rules: `Write_MissingAuthor_Refused`, `Entry_ShowsAuthorAsSelfDeclared`, `Write_TrustClientOrDerivedOrCache_Refused`, `Write_ObservedWithoutEvidence_Refused`, `Write_VideoWithoutTimestamp_Refused`, `Write_Over32KB_Refused`, `Write_RateLimitedPerToken`, `Write_ToDbcSubject_StoredAsNote_RowUnchanged`.
  - Merge: `Get_ShowsHigherTrustFirst`, `Get_StructuredConflict_ShowsNote`, `Get_FreeTextEntries_NoAutoConflict`, `Get_NewerDoesNotEraseOlder`, `Position_AlwaysLabeledApproximate`.
  - Untrusted text: `Entry_WrappedAsUntrusted_NamesAuthor`, `Entry_FakeClosingMarker_CannotEscape`.
  - History: `Undo_HidesEntry_KeepsHistory`, `Page_WriteSupersedes_HistoryKept`, `LayerSwap_LeavesEntries`.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement.** Register an ASP.NET auth scheme that gives `NoResult` for missing or bad tokens, so reads proceed. Call `AddAuthorizationFilters()`. Wrap written text with a per-response random marker, so content can't predict it. Open the store in WAL mode with a busy timeout. Never log the `Authorization` header. A page is its entries with kind `page`, latest not undone.
- [ ] **Step 4: Run the tests.** Expected: PASS.
- [ ] **Step 5: Commit** `feat: add bearer-token writes`.

### Task 11: Admin, backup and final verification

**Files:**
- Create: `src/CoaMcp.Server/Admin/AdminCli.cs`, `scripts/backup-store.sh`
- Modify: `src/CoaMcp.Server/Program.cs`, `docker-compose.yml`, `README.md`
- Test: `tests/CoaMcp.Server.Tests/AdminCliTests.cs`

**Interfaces:**
- Consumes: `WriteStore`.
- Produces: `coamcp admin undo-author <name>` and `admin list-authors`. Run through `docker exec`, with no network endpoint.

- [ ] **Step 1: Write the failing tests.** `UndoAuthor_HidesAllItsEntries_KeepsHistory`, `ListAuthors_ShowsCounts`, `UndoAuthor_Unknown_CleanError`.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement.** The backup script runs `VACUUM INTO` nightly via Coolify's scheduled task to `/data/coamcp/backups`, keeping 14 days.
- [ ] **Step 4: Run all tests with `dotnet test CoaMcp.Pipeline.sln`.** Expected: PASS.
- [ ] **Step 5: Deploy and verify end to end from outside.** Generate a random token and set it as the Coolify secret `COA_MCP_BEARER_TOKEN`, without printing it to any log or commit. Add `COA_MCP_BEARER_TOKEN=${COA_MCP_BEARER_TOKEN}` to compose. Without a token, `write_entry` is refused and reads work. With it, write an `observed` NPC position with a video link, read it back labeled approximate and untrusted, and undo it. Change the variable, redeploy, and confirm the old token is refused. Restore a backup to a scratch file and read it. Tell the owner where the final token is set; don't paste it into chat.
- [ ] **Step 6: Commit** `feat: add author admin and backups`.

## Milestone 4: merging and production proof (Tasks 12-13)

### Task 12: Merge written data into the project

**Files:**
- Create: `src/CoaMcp.Pipeline/Knowledge/{SnapshotPuller.cs,KnowledgeExporter.cs,KnowledgeRestorer.cs}`, `knowledge/README.md`
- Test: `tests/CoaMcp.Pipeline.Tests/KnowledgeTests.cs`

**Interfaces:**
- Consumes: the `entries` schema from Task 10, `IRemote` from Task 5.
- Produces:
  - CLI `coamcp-pipeline pull-writes --host BledtIT --out knowledge`: takes a consistent snapshot on the server with `VACUUM INTO`, downloads it to a temp file outside the repo, exports it, deletes both copies, and prints a summary of added and changed entries and any scan warnings.
  - `knowledge/entries/<id>.md`: YAML front matter with all entry fields, and the content as the body.
  - CLI `coamcp-pipeline restore-writes --from knowledge --store <file>`: refuses a store that already has entries.

- [ ] **Step 1: Write the failing tests.** `Export_WritesOneFilePerEntry`, `Export_Rerun_NoChanges` (byte-identical files, stable field order and line endings), `Export_KeepsUndoneAndSuperseded_Marked`, `Export_PreservesContentExactly`, including front-matter-like text and `---` in content, `Export_HostileId_CannotEscapeFolder`, `Export_ScanWarnsOnSecretOrLocalPath`, `Export_TokenNeverInOutput`, `Restore_RoundTrip_MatchesStore`, `Restore_NonEmptyStore_Refused`, `Pull_RemoteFailure_LeavesKnowledgeUntouched`, `Pull_RemovesTempSnapshot`.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement.** Export to a temp folder and replace `knowledge/entries` only when the whole export succeeded. Don't commit automatically.
- [ ] **Step 4: Run the tests.** Expected: PASS.
- [ ] **Step 5: Commit** `feat: merge written data into the project`.

### Task 13: Production acceptance testing

**Files:**
- Create: `tests/CoaMcp.Acceptance/` (xUnit, excluded from default solution test runs), `docs/acceptance-report.md`
- Modify: code in whichever task's area a failure points to

**Interfaces:**
- Consumes: the live server at `https://coa-mcp.bledt.dev/mcp`; `COA_MCP_BEARER_TOKEN` from the environment for write tests only; local sources for accuracy checks.
- Produces: `dotnet test tests/CoaMcp.Acceptance` with `COA_MCP_URL`; a saved report with date, layer versions, pass and fail per area, timings, and a **Tool responses** section. For each tool, it shows several real requests with their exact arguments and full responses in code blocks, including normal use, a not-found or error case, and a refused write. The suite captures these from the live server, so they're never written by hand. Very long responses are cut with a marker that states the full size, and the token is scrubbed from everything captured.

Write tests use author `acceptance-test`, subjects under `test:`, and undo everything afterward. Load tests stay modest, since this is a shared server. The suite never prints the token.

- [ ] **Step 1: Write the suite.** Through a real MCP client, covering:
  - **Protocol:** initialize, tool list, HTTPS, stateless behavior, `/healthz`. Anonymous tool list contents match the design.
  - **Every read tool:** normal use, paging, caps, sections, realm and topic filters, not-found.
  - **Accuracy:** at least 100 random items against `items_all.json`, 100 random spells against the `spellwork-coa` tool output, 100 random DBC rows against the restored DBCs across at least 10 tables, 100 random paths and winners against the rebuilt listfile, 20 golden scaling spells, all row counts in `status`. Spot checks: `fireball` finds Fireball first, client and BisBeard realms label correctly, positions say approximate, a `project` archive file says so.
  - **Hostile input:** SQL and FTS injection, wrong types, huge values, null bytes, path traversal in ids, oversized bodies. No faults, no stack traces, no paths leaked.
  - **Limits:** rate limit triggers per client through the real proxy, and a spoofed `X-Forwarded-For` doesn't escape it.
  - **Writes:** no token, wrong token and malformed token refused with reads still working. Valid token writes; evidence rules, trust limits, size limit; approximate label, untrusted wrapper and fake closing marker; conflict display; undo.
  - **Merge:** `pull-writes` picks up a test entry, re-run is identical, and undo shows up on the next run.
  - **Live update:** publish a new small layer version during concurrent reads with no failed requests, then roll back with the previous entry.
  - **Restart:** written entries survive a redeploy.
  - **Load:** 20 concurrent clients for 2 minutes of mixed reads, recording p50 and p95 and errors.
  - **Real client:** connect Claude Code with `claude mcp add --transport http`, and answer one question each about a spell, an item and a client path, using only this server.
- [ ] **Step 2: Run the suite against production.** Save results in `docs/acceptance-report.md`, including every failure.
- [ ] **Step 3: For each failure,** write a failing test in the owning project, fix it, deploy, and re-run the affected area.
- [ ] **Step 4: Run the full suite once more.** Expected: all pass, or each remaining failure is listed in the report with the reason. Confirm no `acceptance-test` entries remain visible, then commit a `pull-writes` result only if it contains real data.
- [ ] **Step 5: Commit** `test: add production acceptance suite and report`.
