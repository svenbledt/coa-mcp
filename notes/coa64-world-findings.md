# What the Ascension client does on world entry (CoA64 port findings)

Measured 2026-10-25 and 2026-10-26 while porting the Ascension client's extension library to 64 bits (the CoA64 port), by logging a 32-bit Ascension client and the port into the world on a CoA realm and comparing them. Only findings about client behavior are kept here. The port's plans, per-run error counts, screenshots, account and character details and realm host names are left out. Addresses are from the 32-bit Ascension client.

## Custom server packets the client handles at world entry

The client handles these server-to-client opcodes beyond the stock 3.3.5 set. The counts are packets in a 3-minute session; "unhandled" means a client without the matching extension module logs them as unknown.

| Extension module | Opcodes (first seen count) |
|---|---|
| AuraAmounts | 0x673 SMSG_AURA_UPDATE_ADDON (202) |
| ObjectAddon | 0x578 SMSG_UPDATE_OBJECT_ADDON (63) |
| Tutorial | 0x6A5 SMSG_PATCH_TUTORIAL (9), 0x6B0 SMSG_TUTORIAL_REWARDED_LIST (1) |
| CAMgr (character advancement) | 0x926 SMSG_CA_AVAILABLE_CREDITS_UPDATE (4), 0x725 and 0x726 |
| Appearance | 0x6A2, 0x69D, 0x69A, 0x699 |
| MysticEnchant | 0x5FD, 0x5FC, 0x5F9 |
| MysticEnchantPreset | 0x5FF, 0x600 |
| Challenge | 0x597, 0x599, 0x596, 0x5A2 |
| GameMode | 0x90B SMSG_GAME_MODE_CHANGED |
| BuildCreator | 0x633, 0x634, 0x635 |
| Token | 0x670 SMSG_TOKEN_UPDATE |
| HighRisk | 0x73D SMSG_FEL_COMMUTATION_STATUS_UPDATE |
| SpellCharges | 0x9C2 SMSG_CLEAR_ALL_SPELL_CHARGES |
| CallboardCache | 0x730 SMSG_CALLBOARD_CACHE_CONFIG_UPDATE |

All client DBC patch packets (opcodes 0x92A to 0x9CC), PatchData and the event packets are handled by the extension library. A client without the DBC patch handler logged SMSG_PATCH_SPELL alone 28,422 times in one run. The rows CharacterAdvancement.dbc holds after patching: 10,255.

## Size of the extension library's surface

The generated reference of the Ascension extension library lists: 1,346 Lua natives (including 70 widget and metatable methods), 533 server packets, 346 hook sites, 93 CVars, 259 events, 126 byte patches, and 167 glue-screen natives. Counting registrar entries instead (1,277 natives, 530 packets, 400 hooks) gives different totals, because it folds byte patches into hooks.

## Client behaviors worth knowing

- **Add-on signatures.** The Ascension client always accepts add-on signatures (code at 0x816625, 0x81663E, 0x816694 and 0x81675F). A port that keeps the check shows the error "Couldn't load Blizzard_CombatLog: Corrupt" for the Blizzard add-ons.
- **Secure add-ons.** The extension library hooks `issecure` (0x816DA0) so it answers 1 while an add-on that the server lists as secure runs (for example Ascension_WildCard). Without the hook, `C_Hook.lua` recurses SendEvent, SecureSetValues, SetAttribute, OnAttributeChanged and SendEvent until Lua reports "C stack overflow". A hook at 0x5F7E90 (not confirmed) probably refuses add-ons the server does not list on realms without add-ons.
- **Lua number conversion.** The 32-bit client converts Lua numbers to integers through `__ftol2` (a 64-bit store, keeping the low dword), so values past 2^31 wrap. This decides the CoA class check: `C_Player:IsCustomClass()` tests `bit.contains(Enum.ClassMask.COA, UnitClassMask(unit))` with `COA = 0xFFFFF800`. A direct C cast to a 32-bit int gives 0x80000000 instead and the test fails, and the add-on LootCollector then reports "WR mode" instead of "COA mode". Its fallback realm-mode check uses `C_GameMode:IsGameModeActive`, `C_CharacterCreate.CanCreateArchetype`, `C_Player.IsCustomClass` and `C_Player.IsHero`, because the realm name is not in its list.
- **Sound Kit Player frame.** `patch-B.MPQ` has a `FrameXML.toc` (line 349) that includes `Ascension_DebugTools`, whose `SoundKitBrowser` frame is not hidden by itself. The client closes it when UIParent's PLAYER_ENTERING_WORLD handler reaches `CloseAllWindows(1)` (UIParent.lua line 1163). That handler first calls `C_LootLockout.QueryInstanceBinds()` (line 1150); if that native is missing and errors, the frame stays open.
- **HD archives.** The eight HD archives are refused by a 32-bit client started with `-hd 0`. `Interface\SharedXML\HDPatch.xml` is HD-only, and a client without HD content logs one missing-file line for it.
- **CVars the interface expects.** The client's options panels look up the CVars `useUiScale`, `autoAcceptTrades`, `ObjectSelectionCircleTexture` and `SpellQueueWindow` (saved value 401 in one run); a missing one raises a Lua error, for example "Couldn't find CVar named 'SpellQueueWindow'" in OptionsPanelTemplates.lua line 258.
- **Discord.** Ascension's `discord_game_sdk.dll` is the official 2.5.6 build (x86). It creates a core with current Discord apps but never completes its session (no current user, no visible activity). The CoA server sends no 0x90C or 0x90D packets (the Discord status packets).

## Errors a client shows when an extension module is absent

Each missing native appears as `attempt to call field/global '...'` at a named FrameXML line. Examples seen at world entry: `GetCustomGameMode` (C_GameMode.lua line 12), `GetMapIDByZoneID` (MapPOI.lua line 85), `GetUnitPrimaryStat` (UnitUtil.lua line 74), `C_SpellActivationOverlay` (ActionButton.lua line 637), `C_SuperTrack` (SuperTrackerUtil.lua line 9), `SuggestInvite` (GlobalFunctions.lua line 230), `GetActiveSpecID` (SpecializationUtil.lua line 65), `C_MysticEnchant` (GameTooltip.lua line 606), `C_Appearance` (GameTooltip.lua line 633), `C_Tutorial` (TutorialUtil.lua line 61), `C_Challenge`, `C_Manastorm`, `C_LootLockout`, `C_SkillCardCollection`, `C_PlayerTicket`, `C_CustomStore`, `C_AssetQueryService` and `C_BuildCreator`.
