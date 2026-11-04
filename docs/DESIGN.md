# SmartSave – Design

SmartSave is a client-side BepInEx mod for Valheim. It saves the character at the moments that matter instead of only on a fixed timer, and it never moves the logout point while the player is in danger. It also makes every character save faster: the map data is built in one go and reused when unchanged, and the game's periodic save is skipped right after a full save.

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

### Faster map build (FastMapSave)

`Minimap.GetMapData` is replaced by `FastMapData.TryGet` (prefix on `GetMapData`), which returns the same data:

- outer `ZPackage`: `int 8` (version), then the compressed inner package,
- inner: `int m_textureSize`, `m_explored` as one byte (0/1) per bit, `m_exploredOthers` the same (vanilla loops to `m_explored.Length` for both), `int` count of pins with `m_save`, per pin `string m_name`, `Vector3 m_pos`, `int m_type`, `bool m_checked`, `long m_ownerID`, `string m_author.ToString()`, then `bool ZNet.IsReferencePositionPublic()`.

The difference: the bits are expanded with `BitArray.CopyTo(int[])` into one reused byte buffer and written with a single `BinaryWriter.Write(byte[], 0, n)` into a pre-sized `MemoryStream`. Vanilla calls `ZPackage.Write(bool)` once per bit and lets the stream grow.

Measured (0.3.0, Steam Cloud character): `GetMapData` 48–49 ms, of which compression ~39 ms. The bulk build itself is ~10 ms, compared with ~140–230 ms for vanilla's bool-by-bool loop. The first map save per start runs both versions for the check (~290 ms). Whole save with map on Steam Cloud: 170–315 ms (was 290–400). Local with map, estimated from the parts: ~70 ms.

Self-check: once per game start, in a postfix of `Minimap.LoadMapData` (joining a world, usually behind the loading screen), the game's own `GetMapData` runs (our prefix is bypassed) and its decompressed content is compared byte by byte with our payload. Identical → the fast version is used from then on, and the game's result seeds the cache. Different → switched off until restart, with a log warning. If the join check fails for another reason (too early), the first save runs the game's version and checks it instead. This catches game updates and mods that change the map data.

Cache: every `GetMapData` call takes a snapshot (both grids as 32-bit words via `BitArray.CopyTo(int[])`, plus the pins part as written by vanilla), ~1-2 ms. If it equals the snapshot of the cached result exactly (no hashing), the cached bytes are returned. Otherwise the payload is built and compressed (`Utils.Compress`, the game's own) and cached.

Prewarm (`MapPrewarmSec`, default 60 s): if the map changed, the snapshot is taken on the main thread and the payload is built and compressed on a background thread. Only plain arrays cross threads. The disk / Steam Cloud write always stays on the main thread.

Skipping repeated saves (`SkipRepeatSaveSec`, default 30 s): a prefix on `ZNet.RPC_SavePlayerProfile` (server request) and on `Game.SavePlayerProfile` while `Game.UpdateSaving` runs (the game's timer) skips the save if a save with the map **and** the logout point finished within the window. A save without the map never counts, so the game's regular save still keeps the map up to date. Quit, logout and sleep saves are never skipped. The postfix of `SavePlayerProfile` uses `__runOriginal`, so a skipped save doesn't count as a save.

The ideas for the cache, the prewarm, the join check and skipping repeated saves came from the save module of HitchFix (OrdinaryJoe, MIT, github.com/brutaliccus/valheim-performance-fixes). SmartSave's code is its own.

### Background Steam Cloud write (BackgroundWrite)

A Steam Cloud character save spends most of its time in `FileHelpers.ReplaceOldFile`: Splatform's `SteamCloud.MoveFile` is `CopyFile` + `DeleteFile` (Steam Cloud has no rename), so `.fch → .old` and `.new → .fch` each read and write the whole file. `SteamCloud.WriteFile` also allocates a 100 MB chunk buffer per write (~200 MB of garbage per save, measured).

`CloudWriter` (only when the profile is in Steam Cloud, the game is not shutting down and Steam is initialized):
- `PlayerProfile.SavePlayerToDisk` prefix: wait for a running job (pumping `SteamAPI.RunCallbacks`), decide whether this save is taken over.
- `FileHelpers.CloudFileWriteInChunks` prefix for `*.fch.new`: keep the finished bytes, report success.
- `FileHelpers.ReplaceOldFile` prefix: start the job instead of the three copies.
- `ZNet.ConsiderAutoBackup` prefix: deferred until the job is done (it would read the old file).
- Job, each step after the previous one's Steam callback: `FileWriteAsync(.new)` → `FileReadAsync(.fch)` + `FileWriteAsync(.old)` → `FileWriteAsync(.fch)` → `FileReadAsync(.fch)` and byte compare → `FileDelete(.new)` → `SaveSystem.InvalidateCache`, deferred auto-backup check.
- Steam does the disk work; callbacks arrive on the main thread via `SteamAPI.RunCallbacks` (called every frame by the game's `SteamManager`). No own thread. Data arrays stay referenced until confirmed (Unity's GC does not move objects).
- Shutdown / logout: `Game.Shutdown` sets `m_shuttingDown` before its final `SavePlayerProfile`, so that save is never taken over and waits for the running job first.
- Any Steam error or no answer in 5 s: the data is written as a local `_backup_cloud-<date>.fch` (as vanilla does on a failed cloud save), the feature is off until restart.
- The job starts one frame after the save (`CloudWriter.Tick`), outside the Steam write batch the game opens around every save (Splatform's `Mount` = `BeginFileWriteBatch`, `Unmount` = `EndFileWriteBatch`).
- Every request gets a fresh `CallResult`: Steamworks.NET unregisters a `CallResult` right after its callback returns, so re-using one for the next step from inside that callback loses the answer (found in test 1.2.10). As a fallback, the request is polled with `SteamUtils.IsAPICallCompleted` / `GetAPICallResult` if no callback came within 30 frames.
- A given-up request (timeout: 1 s, 3 s on shutdown) is remembered; if it still finishes later, a `Repair` save makes the game write the newest data last.
- Measured (1.2.11, Steam Cloud, 132 KB character): 9 of 9 jobs verified, 86-146 ms in the background (.new ~30, read ~1, .old ~28, .fch ~27, check ~1 ms). In the frame: 17-21 ms with the map reused (13 ms of it `SaveSystem.PreSaveCloudChecksAndOperations`), 72-88 ms right after exploring; before 100-300 ms.

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
  Plugin.cs        BepInEx entry point: config, Harmony.PatchAll, Update() → SaveProfiler / FastMapData / SaveScheduler.Tick()
  SaveScheduler.cs pending reasons, priorities, cooldown, inventory-window debounce, the actual save
  SafetyState.cs   blocked / in combat / logout-safe / typing
  FastMapData.cs   fast map data build, exact-snapshot cache, background prewarm, self-check on joining
  SaveProfiler.cs  [Debug] Profiling: timing of every save and its parts (log + CSV)
  Patches.cs       all Harmony patches (one nested class per patch)
tools/
  valheim_il.py    lists members and disassembles methods of assembly_valheim.dll (hook verification)
```

---

## 8. Known limits and ideas

- A save causes a short hitch (more with Steam Cloud). That's why there's a cooldown, the window debounce, and no save per item.
- `Character.OnDeath` only runs on the client that owns the boss. The forsaken power trigger and the fallback cover the rest.
- Saving before a teleport doesn't prevent a crash during loading. It makes sure nothing is lost if one happens.
- 1.1.0: the map cache and the join check are confirmed in a game log (map part 0.4-4 ms on reuse). The background prewarm and `SkipRepeatSaveSec` were not yet triggered in a logged session.
- With Steam Cloud, the remaining cost of a save is the Steam Cloud file handling (`FileHelpers.ReplaceOldFile` 70-210 ms). It stays on the main thread on purpose (Steam Cloud file access is not thread-safe).
- Ideas: run the save at the end of a teleport / sleep black screen so it isn't seen (HitchFix does this); optional admin-only world save after important saves (`RequestWorldSaveAsAdmin`, off by default; it makes every player stutter); save when a portal destination chooser opens (the player stands still in a portal).
