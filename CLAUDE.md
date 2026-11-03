# CoA Knowledge MCP

A public MCP server that gives agents facts about Ascension for Conquest of AzerothCore development. A pipeline on the owner's machine builds read-only SQLite layers, and the server on BledtIT (`https://coa-mcp.bledt.dev/mcp`) reads them plus one writable store.

- Design: `docs/superpowers/specs/2026-11-03-coa-knowledge-mcp-design.md`
- Plan: `docs/superpowers/plans/2026-11-03-coa-knowledge-mcp.md`

The design decides what gets built. If code and design disagree, fix the code or raise the conflict. Don't quietly pick one.

## Commands

```
dotnet build CoaMcp.sln
dotnet test CoaMcp.sln
dotnet test CoaMcp.Pipeline.sln    # also needs the local SpellWork worktree
```

## Rules that must never break

- **Client files stay on this machine.** Only paths and parsed data reach the server. Never commit, upload or serve MPQ, DBC, BLP, M2 or other client file content. The publish guard must stay on.
- **Never write into `D:\WoW\Coa\Data` or modify an MPQ.**
- **Tokens stay secret.** Store only hashes. Tokens never appear in logs, errors, responses, tests' output or commits.
- **Reads stay login-free, writes stay token-only.** Every new tool must have a test proving which side it's on.
- **No user text in SQL.** Bind values as parameters, and check table and field names against known lists. Search input goes through `FtsQuery.FromUser`.
- **Keep excluded data out:** the emulator's world database, decompiled code, and private player or account data.
- **Don't fetch from BisBeard.** Its data comes only from `D:\WoW\Coa\DataCOA`.

## Accuracy rules

Wrong data is this project's worst bug, because agents copy it straight into the emulator.

- **Every fact shows its source:** trust level, source, date and realm.
- **Never raise a fact's trust level.** Tokens can only claim `observed` or `memory`.
- **All positions are labeled approximate.**
- **Never merge conflicting values silently.** Show both, with higher trust first.
- **Mark what isn't Ascension.** Files from `project` archives and coa-development.org pages never count as evidence about Ascension.
- **Never guess and present it as fact.** Unknown realms stay unproven, inferred DBC types say `guessed`, and listfile names must match both hashes.
- **Report gaps.** Failed extractions, unnamed files and missing sources belong in `status` or a report, not hidden.
- **Check against real data.** When a change affects output, verify counts or values against the real source, not only fixtures.

## Code quality

- **Test first.** Write the failing test, watch it fail, then write the code. Tests check real behavior, using real SQLite rather than mocks.
- **Cover hostile input.** Anything public-facing gets tests for malformed, oversized and malicious input. Bad input gets a clean error, never a server fault.
- **Written notes are untrusted.** Treat text from writes as data, keep it inside its wrapper, and name its author.
- **Keep answers small.** Agents pay for every byte, so results are short by default, capped at 100 rows, and pageable. Keep tool lists and descriptions short too.
- **Keep scope tight.** Build what the design asks for, with no speculative options or extra layers.
- **Keep files focused.** Each file has one job. Split a file before it becomes hard to read.
- **Fail loudly.** Don't swallow errors. A bad layer leaves the previous version live and logs why.
- **No warnings.** Builds and test output stay clean.
- **Claim only what you ran.** Say a thing works only after running it, and report failures as they are.

## Git

- Work on a branch, not `main`, with small commits named `feat:`, `fix:`, `docs:` or `test:`.
- Check `git status` before committing, so no data files or secrets slip in.
