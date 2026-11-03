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
dotnet run --project src/CoaMcp.Pipeline -- bisbeard --data <DataCOA\data> --out bisbeard.sqlite
dotnet run --project src/CoaMcp.Pipeline -- spells --dbc <dbc folder> --out spells.sqlite
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

Settings (environment variables): `Layers__Root` (data root), `Layers__ReloadSeconds` (default 30), `RateLimit__PerMinute` (default 120), `Network__TrustedProxies__0` (CIDR of the proxy network; forwarded headers are believed only from peers in it, empty means trust nothing). The rate limit keys on the real client: the rightmost untrusted `X-Forwarded-For` address, or `CF-Connecting-IP` when the request came through cloudflared (all addresses trusted).

Deployment is `docker-compose.yml` on Coolify, with the domain set in Coolify. The host folder `/data/coamcp` is mounted at `/data`. The publisher sets modes explicitly: layer folders 0755 and files 0644 (root-owned, so the server can read but not write them), and `writable/` owned by UID 10001 with mode 0750 for the future writable store. The server never opens layer files for writing.
