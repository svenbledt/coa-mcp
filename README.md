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
dotnet run --project src/CoaMcp.Pipeline -- bisbeard --data <DataCOA\data> --client-dbc <winners folder> --out bisbeard.sqlite
```

Each build is versioned with its UTC build time (`yyyyMMddHHmmss`), because a published version never changes and every rebuild differs. Pass `--version <v>` to choose one (letters, digits, `.`, `_`, `-`). Then:

```
dotnet run --project src/CoaMcp.Pipeline -- publish --layer spells.sqlite --dry-run
dotnet run --project src/CoaMcp.Pipeline -- publish --layer spells.sqlite --host BledtIT
```

`publish` does this, in order, and stops at the first failure:

1. Runs the publish guard: only tables on the layer's allow-list (`src/CoaMcp.Pipeline/Publish/LayerAllowList.cs`), no BLOBs, and no text that starts with MPQ, DBC, BLP or M2 file signatures. A refused layer is never sent.
2. Uploads to `/data/coamcp/layers/<name>/<version>.sqlite.tmp` and checks the SHA-256 on the server.
3. Renames it to `<version>.sqlite` (mode 0644) and rewrites `/data/coamcp/current.json` atomically. Other layers keep their entries.

The server re-reads `current.json` every 30 seconds, verifies the new file (SHA-256, metadata, `PRAGMA quick_check`) and swaps it in. Requests already running finish on the old version. A file that fails verification is logged and the old version stays live.

Older version files stay on the server. To roll back, edit `current.json` to point at the earlier version, using its original `sha256`.

`current.json` looks like this:

```json
{ "layers": { "spells": { "version": "3", "file": "layers/spells/3.sqlite", "sha256": "<hex>" } } }
```

## Run the server

```
dotnet run --project src/CoaMcp.Server      # reads layers from Layers__Root (default /data)
dotnet test CoaMcp.sln
```

Settings (environment variables): `Layers__Root` (data root), `Layers__ReloadSeconds` (default 30), `RateLimit__PerMinute` (default 120), `Network__PeerNetworks__0` (CIDR of the networks Traefik connects from; forwarded headers are believed only from peers inside them, default none means trust nothing), `Network__CloudflareRanges__0` (optional override of the built-in Cloudflare edge ranges, current as of 2026-11-03). The rate limit keys on the real client: behind Traefik the rightmost `X-Forwarded-For` address is what Traefik saw; if it is a Cloudflare edge address the client is `CF-Connecting-IP`, otherwise it is that address.

Deployment is `docker-compose.yml` on Coolify, with the domain set in Coolify. The host folder `/data/coamcp` is mounted at `/data`. The publisher sets modes explicitly: layer folders 0755 and files 0644 (root-owned, so the server can read but not write them), and `writable/` owned by UID 10001 with mode 0750 for the future writable store. The server never opens layer files for writing.

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

The command checks that the store exists, finds the running container by the app id (`--container <name>` overrides), runs `admin snapshot` inside it (`VACUUM INTO` a new file in `/data/writable`), downloads that file with `scp` to a temp folder, checks its checksum, exports it, and deletes the server copy and the local copy whatever happens. It only reads production, contacts only `--host`, and never commits. With no store on the server yet it says there is nothing to merge and exits 0. The files, the scan and the restore are described in `knowledge/README.md`; `restore-writes --from knowledge --store <file>` rebuilds an empty store (to bring production back: stop the container, restore to `writable/store.sqlite`, `chown 10001:10001` it, start the container).

## Production acceptance suite

`tests/CoaMcp.Acceptance` tests the live server from outside and writes `docs/acceptance-report.md`. It isn't in either solution, so default test runs never touch production.

```
COA_MCP_URL=https://coa-mcp.bledt.dev/mcp dotnet test tests/CoaMcp.Acceptance
```

It takes about 20 minutes, because it stays under the rate limit, and needs the local sources (restored client, `DataCOA`, Spellwork CoA) and the `BledtIT` ssh host. Without a token, the write tests report NOT RUN, never passed; give one in `COA_MCP_BEARER_TOKEN` or `.superpowers/acceptance-token.txt`. Tests that disturb production run only when asked for: `COA_ACCEPT_LIVE_UPDATE=1` publishes a rebuilt notes layer and rolls it back, and `COA_ACCEPT_RESTART=1` restarts the container (a full redeploy isn't performed: the store's file on the host volume is what shows it would survive one). `COA_ACCEPT_SEED` repeats a random sample, `COA_ACCEPT_REPORT` writes the report elsewhere, and `COA_ACCEPT_REAL_CLIENT=0` skips the Claude Code test, which spends model usage.
