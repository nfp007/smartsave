# SmartSave – Design

SmartSave is a client-side BepInEx mod for Valheim. It saves the character at the moments that matter instead of only on a fixed timer, and it never moves the logout point while the player is in danger.

Everything below about vanilla behaviour was read from the IL of `assembly_valheim.dll` (game version from October 2026). Re-check it after big game updates (see `tools/valheim_il.py`).

---

## 1. The problem

### What a client crash loses

In multiplayer, the data lives in two places:

| Data | Lives where | Saved by | Lost when the game client crashes? |
|---|---|---|---|
| Character: inventory, equipment, skills, known recipes, explored map, forsaken power, food | `.fch` file on the player's computer (local or Steam Cloud) | the client (`Game.SavePlayerProfile`) | **Yes**, everything since the last character save |
| World: chests, buildings, smelters, tombstones, portals, tames | world `.db` on the server | the server | No. Changes reach the server within milliseconds |

A client crash is a **character save** problem. The server's world save doesn't help the character.

### Why chests, crafting and tombstones matter most

The chest is on the server, the inventory is on the client. A crash between "chest changed" and "character saved" splits the two:

| Player did | Then crashed before a save | Result |
|---|---|---|
| Moved items inventory → chest | | items in the chest **and** back in the inventory → **duplication** |
| Took items chest → inventory | | gone from the chest **and** from the inventory → **loss** |
| Crafted with materials from the inventory | | item lost, materials back → annoying |
| Crafted with a craft-from-chests mod | | materials gone from the chest **and** item lost → **loss** |
| Took everything from the tombstone | | tombstone gone **and** items not in the inventory → **the whole corpse run is lost** |

---

## 2. How vanilla saves the character

- `Game.SavePlayerProfile(bool setLogoutPoint, bool isFromRpc)`
  - writes the player data (`PlayerProfile.SavePlayerData`) and the minimap (`Minimap.SaveMapData`),
  - if `setLogoutPoint`: `PlayerProfile.SaveLogoutPoint()`, the position used after reconnecting,
  - then `PlayerProfile.Save()`. Steam Cloud storage is mounted and unmounted around the save. If the file would exceed the cloud quota, the game moves the character to local storage by itself,
  - resets `Game.m_saveTimer` to 0,
  - wraps everything in `SystemResourceManager.Push/PopIsInLoadingScreen`. **A save is not free**: there's a short hitch, longer with Steam Cloud.
- **Client timer**: `Game.UpdateSaving` saves when `m_saveTimer > Game.m_saveInterval`. `m_saveInterval` is static, default **1800 s**, changed by the `-saveinterval` launch argument.
- **Server push**: when the server saves the world (`ZNet.Save(sync, saveOtherPlayerProfiles: true, …)`), `ZNet.SaveOtherPlayerProfiles()` sends the RPC `SavePlayerProfile` to every peer. The client runs `ZNet.RPC_SavePlayerProfile` → `SavePlayerProfile(true, true)`.
  - On a server with `-saveinterval N`, every client is saved every N seconds.
  - This push **always moves the logout point, even in the middle of a fight**.
- `ZNet.HardSaveBlock()` only blocks RPC-triggered saves on Nintendo. It's not relevant on PC.
- **On a host (non-dedicated)**, `Game.m_saveTimer` also drives the **world** save. Any extra `SavePlayerProfile` call resets it, so it would postpone the world save.

---

## 3. Two kinds of save

1. **Character save without logout point**: `SavePlayerProfile(false, false)`. Inventory, skills and map are safe. This is fine almost any time.
2. **Character save with logout point**: `SavePlayerProfile(true, false)`. Also moves where the player appears after a crash. Only done when the player is **safe** (section 5).

Rules:
- Important events save right away, even in combat (configurable). The logout point only moves when safe.
- When a save could not move the logout point, SmartSave remembers that and moves it at the next safe save (for example, when combat ends).

---

## 4. Triggers

Priorities: **High** ignores the cooldown and may run in combat. **Normal** and **Low** respect `MinSaveIntervalSec` and never run in combat.

| Trigger | Hook | When the save runs | Prio |
|---|---|---|---|
| Crafting, upgrading | `InventoryGui.DoCrafting(Player)` postfix | when the inventory/crafting window has been closed for `GuiCloseDelaySec` (1.5 s), or after `GuiMaxDelaySec` (20 s) if it stays open | High |
| Repairing | `InventoryGui.RepairOneItem()` postfix | same | High |
| Inventory changed while the window is open (chests, mods that enchant, recycle, craft from chests…) | `Inventory.Changed(bool, bool)` postfix, only for the local player's inventory and only while `InventoryGui.IsVisible()` | same | High |
| Tombstone recovered | `TombStone.OnTakeAllSuccess()` postfix | right away | High |
| Before a teleport | `Player.TeleportTo(Vector3, Quaternion, bool)` prefix, local player only | **synchronously**, before the loading screen. Covers portals, dungeon entrances and every mod that teleports through `TeleportTo` (including mods that replace the portal flow, such as Portaler) | direct |
| After arriving | `Player.IsTeleporting()` goes true → false | after `ArrivalDelaySec` (5 s) | Normal |
| Boss killed nearby | `Character.OnDeath()` postfix, `IsBoss()`, within 100 m. Best effort: runs where the boss is owned | after 5 s | Normal |
| New forsaken power | `Player.SetGuardianPower(string)` postfix | next allowed moment | Normal |
| Combat ended | in-combat state goes true → false, and a logout point is still owed | next allowed moment | Normal |
| Skill level up | `Player.OnSkillLevelup(SkillType, float)` postfix | next allowed moment | Low |
| New recipe / building piece | `Player.AddKnownRecipe(Recipe)`, `Player.AddKnownPiece(Piece)` postfix | next allowed moment | Low |
| Fallback | no character save (any source) for `FallbackIntervalMin` (30 min) | next allowed moment | Low |
| Manual | `ManualSaveKey` (F6), not while typing | right away, with an on-screen message | direct |
| Server push | `ZNet.RPC_SavePlayerProfile` prefix | when not safe: replaced by `SavePlayerProfile(false, true)`, so the inventory is saved and the logout point stays | vanilla |

Requests are merged: reasons are OR-ed into one pending save, and the highest priority wins. A postfix on `Game.SavePlayerProfile` clears everything that was pending. This happens on **any** character save (SmartSave's own, the vanilla timer, the server push, logout).

Normal and Low saves that want to move the logout point wait up to 60 s for a safe moment, then save anyway without moving it.

### The map (measured)

Profiling (0.1.1, `[Debug] Profiling`) on a real game, explored map 2048×2048:

| Part | Steam Cloud | Local |
|---|---|---|
| Whole `SavePlayerProfile` | 290–400 ms | 190–230 ms |
| `Minimap.SaveMapData` (map build) | 180–290 ms | 170–205 ms |
| of which `ZPackage.WriteCompressed` | 44–70 ms | 39–69 ms |
| `PlayerProfile.SavePlayerToDisk` | 100–115 ms (`FileHelpers.ReplaceOldFile` 70–86) | 10–36 ms (replace ~1.5) |
| `PlayerProfile.SavePlayerData` | 2–5 ms | 1–2 ms |
| Temporary memory | ~190–200 MB, 2–3 GCs | ~42 MB |
| Whole save **without** the map (0.1.2) | ~100–115 ms (estimated from the parts) | 19–21 ms (measured), ~1 MB memory |

`Minimap.GetMapData` writes `m_explored` and `m_exploredOthers` one bool at a time into a `ZPackage` (~8.5 MB), adds the pins, then compresses it (to ~71 KB). `PlayerProfile` keeps the last map bytes per world, so a save that skips `SaveMapData` still writes the previously stored map.

`MapSave` (default `OnPortal`) decides when SmartSave's saves include the map: only the save before a teleport, always, or never. The manual key always includes it, and so do all vanilla saves. A prefix on `Minimap.SaveMapData` returns false while `SaveScheduler.SkipMapNow` is set.

Trade-off: after a crash, map exploration and pins since the last save that wrote the map can be lost. Items, skills and recipes cannot.

### Saving must not delay the world save

SmartSave stores `Game.m_saveTimer` before its own `SavePlayerProfile` call and restores it afterwards. This keeps the host's world save on schedule. The server-push path keeps the vanilla behaviour.

---

## 5. Player state

**Blocked** (no save at all; the request stays pending): no local player, no `Game`/`ZNet`, `IsDead()`, `IsTeleporting()`, `InCutscene()`.

**In combat** (any of these):
- the player took damage (`Player.OnDamaged` postfix) within `CombatCooldownSec` (10 s),
- `InAttack()` or `IsBlocking()`,
- vanilla combat-music state `MusicMan.InCombat()` (private, called via a Harmony delegate),
- an alerted enemy within `CombatScanRadiusM` (30 m) targets the player: `BaseAI.IsAlerted()`, `BaseAI.IsEnemy(c, player)`, `GetTargetCreature() == player`. Scanned every 0.5 s.

**Logout-safe**: not blocked, not in combat, `IsOnGround()`, not `IsSwimming()`, `IsAttached()`, `IsAttachedToShip()` or `IsFlying()`.

**Typing** (manual key ignored): chat focus, console, text input dialog, minimap text input.

---

## 6. Configuration

`BepInEx/config/nfp007.SmartSave.cfg`. All settings are in the config file, with no custom in-game UI. ConfigurationManager shows them if it's installed. The full list with defaults is in the README.

---

## 7. Code layout

```
src/
  Plugin.cs        BepInEx entry point: config, Harmony.PatchAll, Update() → SaveScheduler.Tick()
  SaveScheduler.cs pending reasons, priorities, cooldown, inventory-window debounce, the actual save
  SafetyState.cs   blocked / in combat / logout-safe / typing
  Patches.cs       all Harmony patches (one nested class per patch)
tools/
  valheim_il.py    lists members and disassembles methods of assembly_valheim.dll (hook verification)
```

---

## 8. Known limits and ideas

- A save causes a short hitch (more with Steam Cloud). That's why there's a cooldown, the window debounce, and no save per item.
- `Character.OnDeath` only runs on the client that owns the boss. The forsaken power trigger and the fallback cover the rest.
- Saving before a teleport doesn't prevent a crash during loading. It makes sure nothing is lost if one happens.
- Ideas: optional admin-only world save after important saves (`RequestWorldSaveAsAdmin`, off by default; it makes every player stutter); save when a portal destination chooser opens (the player stands still in a portal).
