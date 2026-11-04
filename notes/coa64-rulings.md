# Quirks of the original Ascension client (from the CoA64 port's rulings)

Recovered 2026-10-25 from the execution ledgers of the CoA64 port. The ledgers record each decision of the port with its cost if wrong. Only entries that state how the original 32-bit Ascension client or its extension library behaves are kept, and they are the port team's claims from reading it, not independent checks. Decisions about the port's own structure, what it serves or refuses, test plans, task order and deferred minor issues are left out. Addresses are from the 32-bit client or its extension library.

## Network and packets

- A creature query answer (opcode 0x567) for a creature that is already cached replaces the cached record, and the original leaks the old name (port ledger).
- The port ties the effects of the GM packet 0x5C0 to two sites in the original: a constant at 0xA32904 (squared short-range teleport limit, used by the movement teleport and the vehicle passenger state change) and two jumps in `Script_SendChatMessage` that skip the language skill check (0x50D2B7) and the dead-or-ghost check (0x50D641).

## Command line and login

- `-character <name>` matches only the first character in the list. The original steps the character records by 0x188 bytes while the client's records are 0x198 bytes, so only record 0 is read correctly.
- `-realm` copies the realm list address into the realm info's address field without a length limit (the field holds 32 bytes).
- When a realm's build info applies, the original writes the address of its digits over the "12340" build string at 0x9F5200, corrupting it. The logon build (0x8CA5F0) and the auth session build (0x4648B3) take the number.

## Client data

- A custom DBC layout whose width differs from the file's row size makes the original read past the rows.
- Inserting a DBC row past the table's maximum id grows the index and drops the old maximum row (the original's growth routine, FUN_10133ac0 in the extension library).
- On a realm switch the original leaks the old bridged table and keeps a stale cache.

## Lua and the interface

- `RegisterPacket` handlers keep stale registry references after a UI reload or a glue-to-world switch in the original; it calls whatever the new registry holds at that slot. Add-ons re-register after a reload anyway.
- The original runs its modules in static-initialization (link) order. Only duplicate native names make the order visible.
- The extension library patches a call target at 0x68EDD7 (one byte, into the middle of a function on a Direct3D path). Its effect is undetermined.
