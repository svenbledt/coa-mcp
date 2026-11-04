# CoA module notes (2026-10-29)

Findings about how the Ascension client's template, spell charge and asset query code behaves, as seen in the CoA64 port.
Addresses are from the 32-bit Ascension client or its extension library. No code is included.

## Templates

The Lua natives `GetQuestTemplate` (0x1021EA50), `GetCreatureTemplate` (0x1021B200), `GetCreatureModelData` (0x100B0EF0),
`GetCreatureDisplayInfo` (0x100B0D70) and `GetCreatureDisplayInfoExtra` (0x100B0E30) each take one number (the id), look
the record up (the client's quest or creature cache when no callback is set, or the client DBC container), turn it into a
JSON object and push that to Lua as a table. A missing record returns nothing. This was checked against the genuine
Ascension extension library.

**Quest record offsets.** The quest template builder (0x1021ECB0) reads `ObjectiveText` at a 0x200 stride and `RewardTitleId` .. `Unknown`
0x400 past the client's 0x28E4-byte record (up to +0x2CE0), so in the 32-bit client those keys returned other
fields' bytes and could crash the Callboard when a record ended at unmapped memory. The CoA64 port reads the client's real
layout (record copy 0x668290, WDB writer 0x7F6D00): 0x100-byte objective texts from +0x1C94, the four dwords at
+0x2094, `CompletedText` at +0x20A4, the faction arrays at +0x28A4 / +0x28B8 / +0x28CC and `Unknown` at +0x28E0.
Bytes past the record's end still read 0.

## Spell charges

SMSG 0x9C4: u32 n x {category, i32 ms left, u8 used}, kept only for a known category with a max-charges column and
a non-zero time. 0x9C5 {category, ms, used}: used 0 removes, a new category starts now, a known one keeps its
start. `GetSpellCharges(spell)` returns left, max, start (s), duration (s); max, max, 0, recharge without an entry;
0 x 4 otherwise. Every handler fires `SPELL_UPDATE_CHARGES` while in world (0x9C5 only when it passed).

## Asset queries

`TryCache*(id)`: false when cached; true and queued the first time; afterwards false, moving a still-waiting id to
the front. The idle tick sends up to 50 ids per kind per batch (CMSG 0x61B / 0x61C / 0x61A {u32 n, ids}), at most 3
batches a second (outside development realms).
