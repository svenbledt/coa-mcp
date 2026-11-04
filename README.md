# CoA Knowledge MCP

A public, login-free MCP server with facts about Ascension for Conquest of AzerothCore development. A pipeline on the owner's machine builds read-only SQLite layers from local data; the server reads them. Every answer shows its trust level, source, date and realm.

Design and plan: `docs/superpowers/specs/` and `docs/superpowers/plans/`. Rules for contributors: `CLAUDE.md`.

## Connect an MCP client

The server speaks MCP over streamable HTTP, with no login for reads:

```
https://coa-mcp.bledt.dev/mcp
```

For Claude Code: `claude mcp add --transport http coa-knowledge https://coa-mcp.bledt.dev/mcp`. Other clients take the same URL as an HTTP server. Start with the `status` tool, which lists the loaded layers, their versions and the known gaps.

## Publish a layer

Layers are built locally, then pushed to the server with `scp` and `ssh` (the server's login shell does not matter: commands run under `sh -s`; the ssh host, such as `BledtIT`, must already work without a password prompt, as a user that may `chown`, normally root).

```
dotnet run --project src/CoaMcp.Pipeline -- dbc --manifest <restore manifest.sqlite> --client-release <CLIENT-RELEASE.json> --winners <private folder outside the repo> --out dbc.sqlite
dotnet run --project src/CoaMcp.Pipeline -- spells --dbc <winners folder> --out spells.sqlite
dotnet run --project src/CoaMcp.Pipeline -- paths --manifest <restore manifest.sqlite> --client-release <CLIENT-RELEASE.json> --out paths.sqlite
dotnet run --project src/CoaMcp.Pipeline -- bisbeard --data <DataCOA\data> --client-dbc <winners folder> --out bisbeard.sqlite
```

`dbc` and `paths` name the client from the launcher's `.coa-launcher/state.json` beside the release file, and compare each archive's size with its entry: an answer adds a mismatch line only for an archive that differs. Without a state file they fall back to `CLIENT-RELEASE.json`'s wording. A damaged state file stops the build.

`config/archives.json` holds each archive's origin. `ascension` means Ascension's by the project's records, not compared with live, so answers say `Ascension's archive per project records; this copy may lag live`. Area 52's archive never counts as a winner: its DBCs are exported as `<table>__area52`, realm `area-52`, and it gets no spell pages.

Each build is versioned with its UTC build time (`yyyyMMddHHmmss`), because a published version never changes and every rebuild differs. Pass `--version <v>` to choose one (letters, digits, `.`, `_`, `-`). Then:

```
dotnet run --project src/CoaMcp.Pipeline -- publish --layer spells.sqlite --dry-run
dotnet run --project src/CoaMcp.Pipeline -- publish --layer spells.sqlite --host BledtIT
```

When a change touches the client layers, deploy the server first, then publish `dbc`, `spells` and `paths` together: older server code can't read a layer built with a launcher client record, and the three layers should describe one client.

`publish` does this, in order, and stops at the first failure:

1. Runs the publish guard: only tables on the layer's allow-list (`src/CoaMcp.Pipeline/Publish/LayerAllowList.cs`), no BLOBs, and no text that starts with MPQ, DBC, BLP or M2 file signatures. A refused layer is never sent.
2. Uploads to `/data/coamcp/layers/<name>/<version>.sqlite.tmp` and checks the SHA-256 on the server.
3. Renames it to `<version>.sqlite` (mode 0644) and rewrites `/data/coamcp/current.json` atomically. Other layers keep their entries.

The server re-reads `current.json` every 30 seconds, verifies the new file (SHA-256, metadata, `PRAGMA quick_check`) and swaps it in. Requests already running finish on the old version. A file that fails verification is logged and the old version stays live.

Older version files stay on the server. To roll back, edit `current.json` to point at the earlier version, using its original `sha256`.

To free disk space, remove versions you no longer need for rollback. First confirm in the `status` tool that the server shows the new version: `current.json` says what should be live, and a version that failed verification is named there while the server keeps serving the older one. Then compare `current.json` and `status` with the folders:

```
ssh BledtIT 'cat /data/coamcp/current.json; ls -l /data/coamcp/layers/*/'
ssh BledtIT 'rm /data/coamcp/layers/<name>/<version>.sqlite'
```

Remove only a `<version>.sqlite` that neither `current.json` nor `status` names. Leave any `.tmp` alone while a publish is running. A removed version can't be rolled back to.

`current.json` looks like this:

```json
{ "layers": { "spells": { "version": "3", "file": "layers/spells/3.sqlite", "sha256": "<hex>" } } }
```

## Run the server

```
dotnet run --project src/CoaMcp.Server      # reads layers from Layers__Root (default /data)
dotnet test CoaMcp.sln
```

Settings (environment variables): `Layers__Root` (data root), `Layers__ReloadSeconds` (default 30), `RateLimit__PerMinute` (default 120, per client), `RateLimit__WritesPerMinute` (default 60), `RateLimit__WritesPerDay` (default 2000), `RateLimit__UndosPerMinute` and `RateLimit__UndosPerDay` (same defaults), `Store__MaxBytes` (writable store size cap, default 1 GiB), `Dbc__QueryBudgetMs` (time limit of one DBC row query, default 2000), `Concurrency__MaxPerClient` (`/mcp` requests one client may have running, default 8; past that a 429 with a JSON-RPC error), `Concurrency__MaxRunning` (`/mcp` requests running at once across all clients, default 64), `Concurrency__MaxQueued` (requests waiting for a turn, default 32; past that a 503 with a JSON-RPC error), `Concurrency__QueueWaitSeconds` (longest wait for a turn, default 5, then the same 503), `Request__BodySeconds` (time a whole `/mcp` request body may take to arrive, default 10, then a 408), none of which limit `/healthz`, `Network__PeerNetworks__0` (CIDR of the networks Traefik connects from; forwarded headers are believed only from peers inside them, default none means trust nothing), `Network__CloudflareRanges__0`, `__1` and so on (optional override of the built-in Cloudflare edge ranges, current as of 2026-11-03). The rate limit keys on the real client: behind Traefik the rightmost `X-Forwarded-For` address is what Traefik saw; if it is a Cloudflare edge address the client is `CF-Connecting-IP`, otherwise it is that address.

Deployment is `docker-compose.yml` on Coolify, with the domain set in Coolify. The host folder `/data/coamcp` is mounted at `/data`. The publisher sets modes explicitly: layer folders 0755 and files 0644 (root-owned, so the server can read but not write them), and `writable/` owned by UID 10001 with mode 0750 for the writable store. The server never opens layer files for writing.

**Token.** Writes need `COA_MCP_BEARER_TOKEN`, set as a secret on the service in Coolify and nowhere else. It must be at least 32 characters, for example from `openssl rand -hex 32`. Unset or shorter, writes are off and reads still work. To replace it, change the variable and redeploy. The `status` tool says whether writes are enabled.

**Restarts.** A restart or redeploy has downtime: the server verifies every layer (SHA-256 and `PRAGMA quick_check`) before it starts listening, so it refuses connections until that's done. The health check allows 300 seconds for it. Publishing a layer has no such gap, because the swap happens in the background.

**Cloudflare ranges.** Compare https://www.cloudflare.com/ips-v4 and https://www.cloudflare.com/ips-v6 with the list in `src/CoaMcp.Server/CloudflareRanges.cs` from time to time. If they differ, update that list and redeploy, or set `Network__CloudflareRanges__N`, which replaces the whole list, so give every range. A stale list makes a request from a new edge address count against that edge address's rate limit.

## Admin and backups

Owner commands run inside the container with `docker exec`. There is no network endpoint for them and they need no token. The container name comes from Coolify (`docker ps`).

```
docker exec <container> dotnet /app/CoaMcp.Server.dll admin list-authors
docker exec <container> dotnet /app/CoaMcp.Server.dll admin undo-author "<name>" --by owner
docker exec <container> dotnet /app/CoaMcp.Server.dll admin undo-entry <id>
```

`list-authors` prints one line per author (name quoted) with live, undone and total counts and the newest date. `undo-author` marks that author's live entries undone in one transaction and prints how many; it matches the name exactly, deletes nothing, and a second run changes nothing. An unknown author, or a missing store, prints a message and exits 1 without creating anything.

Backups: `sh /app/backup-store.sh` (or `admin backup <dir>`) writes `store-YYYYMMDD.sqlite` using `VACUUM INTO`, which is safe while the server runs, and deletes files named exactly like that once they are 14 days old. The default folder is `/data/writable/backups` (host: `/data/coamcp/writable/backups`), because `/data/writable` is the only place the container user can write. Make it a Coolify scheduled task, nightly, running `sh /app/backup-store.sh` in the `server` container. The files are mode 0640, so keep them off public shares.

Restore: stop the container, copy the chosen backup to `/data/coamcp/writable/store.sqlite` (owner UID 10001, mode 0640), delete any `store.sqlite-wal` and `store.sqlite-shm` beside it, and start the container again.

## Merging written entries into the repository

From time to time, copy what agents wrote on production into `knowledge/` and commit it after a review:

```
dotnet run --project src/CoaMcp.Pipeline -- pull-writes --host BledtIT --out knowledge
git diff knowledge
git add knowledge && git commit
```

`pull-writes` checks that the store exists, finds the running container by the app id (`--container <name>` overrides), runs `admin snapshot` inside it (`VACUUM INTO` a new file in `/data/writable`), downloads that file with `scp` to a temp folder, checks its checksum, exports it, and deletes the server copy and the local copy whatever happens. It only reads production, contacts only `--host`, and never commits. With no store on the server yet it says there is nothing to merge and exits 0. `--include-test` also exports `test:` entries, which are left out by default. The files, the scan and the restore are described in `knowledge/README.md`.

To rebuild an empty store from those files, which refuses a store that already has entries:

```
dotnet run --project src/CoaMcp.Pipeline -- restore-writes --from knowledge --store <new store.sqlite>
```

To bring production back: stop the container, restore to `writable/store.sqlite`, `chown 10001:10001` it, start the container.

## Production acceptance suite

`tests/CoaMcp.Acceptance` tests the live server from outside and writes `docs/acceptance-report.md`. It isn't in either solution, so default test runs never touch production.

```
COA_MCP_URL=https://coa-mcp.bledt.dev/mcp dotnet test tests/CoaMcp.Acceptance
```

It takes about 20 minutes, because it stays under the rate limit, and needs the local sources (restored client, `DataCOA`, Spellwork CoA) and the `BledtIT` ssh host. Without a token, the write tests report NOT RUN, never passed; give one in `COA_MCP_BEARER_TOKEN` or `.superpowers/acceptance-token.txt`. Tests that disturb production run only when asked for: `COA_ACCEPT_LIVE_UPDATE=1` publishes a rebuilt notes layer and rolls it back, and `COA_ACCEPT_RESTART=1` restarts the container (a full redeploy isn't performed: the store's file on the host volume is what shows it would survive one). `COA_ACCEPT_SEED` repeats a random sample, `COA_ACCEPT_REPORT` writes the report elsewhere, and `COA_ACCEPT_REAL_CLIENT=0` skips the Claude Code test, which spends model usage. The test that goes straight to the origin past Cloudflare needs the origin's address in `COA_ACCEPT_ORIGIN_IP`, and without it reports NOT RUN.

## Before this repository becomes public

The design names the server's direct address under "Hosting" (`docs/superpowers/specs/`), and earlier commits hold it too. That is fine while the repository is private. It must go before the repository becomes public.
