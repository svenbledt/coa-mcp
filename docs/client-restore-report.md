# Client restore report

Restore of `D:\WoW\Coa\Data` to `F:\WorkDir\CoaRestored` and listfile reconstruction to `D:\WoW\Coa\Listfiles`, 2026-11-03. Both outputs stay on the owner's machine; this report holds only counts and file names.

**The restore and the listfile are both complete: every one of the 770,946 entries has a name that matches both hashes, and none failed.** 766,035 entries are restored as files. The other 4,911 are delete markers, which have no file. 83 of the restored files needed a fallback reader because `enUS/backup-enUS.MPQ` has a wrong sector size in its header.

## How to re-run

```
pwsh tools/build-stormlib.ps1      # StormLib v9.40, pinned by commit, into tools/stormlib (gitignored)
dotnet build src/CoaMcp.Pipeline -c Release
coamcp-pipeline restore --data D:\WoW\Coa\Data --out F:\WorkDir\CoaRestored
coamcp-pipeline listfile --manifest F:\WorkDir\CoaRestored\manifest.sqlite --out D:\WoW\Coa\Listfiles
```

A re-run skips files whose size and CRC are already correct, and re-verifies all of them in about 3 to 4 minutes. Expected CRCs always come from the archive as it is now. The manifest records each archive's size and modified time; when either changes (`patch-B.MPQ` does between client revisions), that archive is restored again from scratch, and its files and rows that no longer exist are removed. Re-run `listfile` afterwards to refresh the lists.

What protects a finished restore:
- **Wrong `--data`:** a manifest belongs to one client folder. Another folder is refused, and a folder with no archives is an error. Either way nothing changes.
- **Archive gone from disk:** only reported, with its files and rows kept, and the run exits 1. `--prune-gone` removes them.
- **Damaged or half-written archive:** if the header's size doesn't match the file, or any entry points past the block table or at a freed block, the archive is reported as broken and nothing is changed for it. `coamcp-pipeline archive-check --data <folder>` runs this check read-only; all 78 real archives pass.
- **Failed run:** an archive's new size and date are recorded only after it finishes, so it still counts as changed next time.

## The 4,911 unresolved names are delete markers

`mpq-listfile.md` lists 4,911 names that don't resolve. They are real entries, not stale listfile lines, and locale has nothing to do with it. Each one has `MPQ_FILE_DELETE_MARKER` set and a size of 0.

| Archive | Doc: not resolvable | Manifest: delete markers |
| --- | ---: | ---: |
| `patch.MPQ` | 3,610 | 3,610 |
| `patch-2.MPQ` | 1,234 | 1,234 |
| `patch-3.MPQ` | 39 | 39 |
| `enUS/patch-enUS.MPQ` | 26 | 26 |
| `enUS/patch-enUS-2.MPQ` | 2 | 2 |
| **Total** | **4,911** | **4,911** |

How we know:
- All 770,791 listfile lines match both hashes of an entry, so no archive has a stale line (`stale_lines` is 0 for all 78).
- Every entry in the client has locale 0, so trying other locales can't change anything.
- The per-archive counts match the doc exactly. The likely reason they looked unresolved is that its tool treats a marker as "file not found", but that tool wasn't checked.

**What a marker means to the client (for Task 7 and 8).** A marker doesn't fall through to the next archive. The search stops at it and the open fails as "file not found" (`CoA64/Engine/Source/BlizzardCore/Source/Packages/Mopaq/SFileArchives.cpp:1657-1659` returns 4, the loop at `:1721-1727` stops on any nonzero result, and `SFile.cpp:109-113` turns 4 into error 2). So:
- A marker is never a winning copy, and neither is the older copy under it. The path is deleted as far as the client can see.
- Of the 4,911 markers, 4,906 are the top copy of their path, once realm archives and archives the client never opens are left out.
- In the other 5, a live copy in a higher archive wins: `DBFilesClient/CharVariations.dbc`, `Interface/GlueXML/SoundOptionsFrame.lua` and `.xml` (marker in `enUS/patch-enUS.MPQ`), and `Interface/GlueXML/RaceSelect.lua` and `.xml` (marker in `enUS/patch-enUS-2.MPQ`).

## 83 files recovered from a wrong sector size in one archive's header

`enUS/backup-enUS.MPQ` says its sectors are 16,384 bytes (shift 5; the other 77 archives use shift 3, 4,096 bytes). Its multi-sector compressed files were actually stored with 4,096-byte sectors, and StormLib follows the header and can't be told otherwise:

- 16 files fail with a read error (`ERROR_FILE_CORRUPT`).
- **67 files read with no error but come back wrong**: the first 4,096 bytes are right, then zeros and raw compressed data. Only the CRC in `(attributes)` catches them.

So every read must now agree with `(attributes)`. When a read fails or disagrees, `SectorReader` reads the file's sectors straight from the archive (read-only) and tries the other sector sizes that fit its sector table.

- **What counts:** a result is accepted only if its length is right and its CRC matches `(attributes)`. Length alone is never enough.
- **No CRC:** a file with none stays `failed` if its sector table doesn't fit the header's sector size, because that is the case where StormLib's read can be silently wrong.
- **Scope:** stored and zlib sectors only, the only kinds these 83 files use (14,290 compressed sectors, all zlib). Anything else, including bzip2, stays `failed` with a reason naming the compression byte.

**Result: all 83 recovered at shift 3, and all 83 match their `(attributes)` CRC**, including the 16 that couldn't be read before. Their manifest rows are `ok`, with `reason` set to `recovered: header sector shift 5 wrong, used 3`. They are installer extras, not game data: `Wow.exe`, `Battle.net.dll`, the Mac apps, manuals and help pages. The client only checks this archive's stock size (`CoA64/WoW/Source/Ui/BugReport.cpp:56`).

## Load order

`config/archives.json` holds every archive's priority and origin, plus the evidence for each rule, by file and line. In short, higher priority wins:

| Priority | Archives | Rule |
| ---: | --- | --- |
| 512 | `area-52/patch-D.MPQ` | Realm archive, only while that realm's data path is active |
| 66-128 | `enUS/patch-enUS-2`, `-3`, then `patch-2` ... `patch-ZG` | Sorted by full path; the last name wins |
| 65 | `patch.MPQ` | Appended after the sort, opened second |
| 64 | `enUS/patch-enUS.MPQ` | Appended after the sort, opened first |
| 40-49 | `expansion`, `lichking`, `common`, `common-2`, locale and speech archives | Slot in the client's `s_archives` table |
| below 0 | `enUS/base-enUS.MPQ`, `enUS/backup-enUS.MPQ` | Never opened by the client, so never a winning copy |

Limits of this evidence:
- It comes from reading reconstructed client code. Nothing here has been tested against the running client.
- The table puts `expansion` above `lichking` above `common`, which looks backwards for WotLK. It changes nothing in practice: the base archives share no paths apart from `(listfile)` and `(attributes)`.
- The eight HD archives (`patch-4`, `-5`, `-Q`, `-R`, `-U`, `-V`, `-X`, `-Z`) don't load on a `-hd 0` client. Winning copies below assume HD is on.

Origins: `patch-B.MPQ` and `patch-T.MPQ` are `project`, everything else is `unknown` until the owner confirms it. Outside the realm archive, `patch-B.MPQ` holds the winning copy of 1,590 paths and `patch-T.MPQ` of 2, ignoring markers.

## Checks

- **Against `mpq-listfile.md`:** all 70 archives match on line count. The total is 770,789 lines there and 770,791 here, and the 2 extra lines are `patch-ZG.MPQ`'s, which the doc doesn't cover. Unique names excluding internal files: 639,153 in both.
- **The 8 archives the doc leaves out:** `patch-4`, `-5`, `-C`, `-CZZ`, `-W`, `-WB` and `-WC` hold only `(listfile)` and `(attributes)`. `patch-ZG.MPQ` also holds two real files, and its own listfile names both: `DBFilesClient/ItemDisplayInfo.dbc` and `Interface/Icons/INV_GrootCoin.blp`.
- **On disk:** 766,035 files and 84,137,498,852 bytes, both equal to the manifest's `ok` rows. No partial files. A sample of 3,000 files re-read from disk all matched their recorded CRC, as did all 83 recovered files.
- **CRC against `(attributes)`:** 765,943 `ok` files match, 0 disagree. 92 have no CRC to compare: 86 internal files and 6 other files, 5 of them in `patch-B.MPQ`. All 6 are empty files (size 0), so there are no bytes to check.
- **Listfiles:** 79 files. Per-archive line counts add up to 770,946, and `patch.MPQ.listfile.txt` has 41,162 lines.
- **Archives unchanged:** archives open read-only, and a test proves a fixture's bytes and timestamp don't change. On the real client, all 78 archives still total 45,479,931,701 bytes and nothing under `D:\WoW\Coa\Data` has a modified date of today. No hash was taken beforehand, so this isn't a byte-level proof.

## Names by source

| Source | Entries named |
| --- | ---: |
| Archive's own `(listfile)` | 770,791 |
| Internal file names (`(listfile)`, `(attributes)`) | 155 |
| Other archives' listfiles | 0 |
| Strings in restored files, extension variants, `.skin` patterns, `.toc` and `.xml` relative paths | 0 |

Nothing was left for the last two sources to find, so `listfile` skipped the string scan. That code is tested on fixtures but has not run on the real client. No incremental patch entries exist (`patch_file`: 0).

## Final figures

| | Entries |
| --- | ---: |
| Total | 770,946 |
| Named | 770,946 |
| Unnamed | 0 |
| Restored (`ok`) | 766,035 |
| of which recovered with another sector size | 83 |
| Delete markers (no file, by design) | 4,911 |
| Patch files | 0 |
| Failed | 0 |

Nonzero by archive:

| Archive | Failed | Delete markers | Unnamed |
| --- | ---: | ---: | ---: |
| `enUS/patch-enUS-2.MPQ` | 0 | 2 | 0 |
| `enUS/patch-enUS.MPQ` | 0 | 26 | 0 |
| `patch-2.MPQ` | 0 | 1,234 | 0 |
| `patch-3.MPQ` | 0 | 39 | 0 |
| `patch.MPQ` | 0 | 3,610 | 0 |

All 83 recovered files are in `enUS/backup-enUS.MPQ`, which has no failures, markers or unnamed entries.

**The listfile is complete; nothing remains unnamed or unrestored.** The listed names include internal files and delete markers, because each one is a real entry matching both hashes. `client-listfile.txt` has 639,155 unique names, 639,153 without the two internal names.
