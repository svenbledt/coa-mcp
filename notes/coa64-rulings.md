# Quirks of the original Ascension client (from the CoA64 port's rulings)

Recovered 2026-10-25 from the execution ledgers of the CoA64 port. The ledgers record each decision of the port with its cost if wrong. Only entries that describe how the original 32-bit Ascension client or its extension library behaves are kept. Decisions about the port's own structure, test plans, task order and deferred minor issues are left out. Addresses are from the 32-bit client.

## Network and packets

- Custom packet handlers in the extension library are void functions cast to int, so the return value is undefined; the client ignores it for custom opcodes. A port may return 1.
- Packets that GM accounts receive: opcode 0x5C0 sets two effects. One is the squared short-range teleport limit used by the movement teleport and the vehicle passenger state change (a constant at 0xA32904). The other skips two chat checks in `Script_SendChatMessage`: the language skill check (jump at 0x50D2B7) and the dead-or-ghost check (jump at 0x50D641).
- A creature query answer (opcode 0x567) for a creature that is already cached replaces the cached record and leaks the old name.
- The client sends asset queries in batches of up to 50 ids per kind (see the module notes).

## Command line and login

- `-character <name>` matches only the first character in the list. The original steps the character records by 0x188 bytes while the client's records are 0x198 bytes, so only record 0 is read correctly.
- `-realm` copies the realm list address into the realm info's address field without a length limit (the field holds 32 bytes). The CoA realm list host fits.
- When a realm's build info applies, the original writes the address of its digits over the "12340" build string at 0x9F5200, corrupting it. The logon build (0x8CA5F0) and the auth session build (0x4648B3) take the number.
- `CVar::SaveCvar` has a skip list that the extension library's `lastCharacterIndex` line bypasses.

## Client data

- A custom DBC layout whose width differs from the file's row size makes the original read past the rows.
- Inserting a DBC row past the table's maximum id grows the index and drops the old maximum row (the original's growth routine, FUN_10133ac0 in the extension library). The CoA64 port keeps this on purpose.
- The `CharacterCreationArchetypes` patch is packet 0x6D6, registered by PatchData; it rebuilds the index after patching.
- 232 client DB tables are marked unloaded and re-initialized on a realm switch; the original leaks the old table and keeps a stale cache. The memory bridge reads that serves creature display ids for the character-create preview use the original's 32-bit rows, where string fields are offsets into a string block. Table commands 0x64 to 0x6F are served; 0x73 is refused; the memory commands 1 to 4 and the projected reads 0x70 to 0x72 are never sent by the client.
- HD tables need the Vulkan renderer; without it HD content stays off and the glue log keeps the line "Couldn't open Interface\SharedXML\HDPatch.xml".

## Lua and the interface

- `RegisterPacket` handlers keep stale registry references after a UI reload or a glue-to-world switch in the original; it calls whatever the new registry holds at that slot. Add-ons re-register after a reload anyway.
- The original runs its modules in static-initialization (link) order. Only duplicate native names make the order visible.
- The extension library detours `FrameScript_HandleError`; the bodies are otherwise identical.
- A one-byte call-target change at 0x68EDD7 (into the middle of a function on a Direct3D path) was left unported; it is probably irrelevant to a renderer replacement.
