# SmartSave

**Saves your character when it matters, not just on a timer.**

A game crash rolls your character back to the last save: crafted items, chest moves, a recovered tombstone, skill levels, explored map. SmartSave saves at exactly those moments, and it never moves your respawn point while you are fighting, falling or swimming.

Client-side only. The server doesn't need it, and it works on any server and in single player, with local or Steam Cloud characters.

Source code, issues and ideas: **[github.com/nfp007/smartsave](https://github.com/nfp007/smartsave)**

## When it saves

| Moment | What happens |
|---|---|
| **Crafting, upgrading, repairing** | Saves when you close the crafting window (one save, however many items you made) |
| **Closing a chest** | Saves if your inventory changed while it was open. Chest to chest gives one save, not ten |
| **Tombstone** | Saves right after you take everything back |
| **Before a teleport** | Portals, [Portaler](https://thunderstore.io/c/valheim/p/nfp007/Portaler/), dungeon entrances, any mod that teleports. Loading screens are where games like to crash |
| **After arriving** | Saves 5 seconds after a teleport |
| **Boss / forsaken power** | Saves after a boss dies near you or when you take a new power |
| **Skill level up, new recipe** | Saves at the next calm moment |
| **After combat** | Anything that waited is saved when the fight is over |
| **F6** | Save right now |
| **Fallback** | If nothing saved for 30 minutes, it saves at the next safe moment |

## Two kinds of save

- **Inventory save** (items, skills, map): fine almost any time.
- **Logout point** (where you appear after a crash or reconnect): only moved when you stand on solid ground and are **not in combat**, not swimming, not riding and not mid-air.

So a crash in the middle of a fight brings you back with your items, but not into the fight.

SmartSave also fixes this for the game's own server save: with `-saveinterval`, the server tells every client to save, and vanilla always moves your logout point, even mid-fight. SmartSave keeps the save but leaves the logout point where it was.

Combat is detected from the game's own combat-music state, damage you take, your own attacks and blocks, and alerted enemies within 30 m that target you.

## Settings

All settings are in `BepInEx/config/nfp007.SmartSave.cfg` (also in ConfigurationManager, F1).

| Section | Setting | Default | |
|---|---|---|---|
| General | `Enabled` | true | |
| General | `LogSaves` | true | One line per save in the BepInEx log, with the reason |
| General | `ShowSaveMessage` | false | Small "Saved" message top left |
| General | `MinSaveIntervalSec` | 15 | Minimum gap between normal saves (important saves ignore it) |
| General | `ManualSaveKey` | F6 | `None` = off |
| Triggers | `SaveAfterCrafting` / `SaveOnChestClose` / `SaveOnTombstone` | true | |
| Triggers | `GuiCloseDelaySec` | 1.5 | The window must stay closed this long before the save |
| Triggers | `GuiMaxDelaySec` | 20 | Window open longer than this after a change: save anyway |
| Triggers | `SaveBeforeTeleport` / `SaveAfterTeleport` | true | |
| Triggers | `ArrivalDelaySec` | 5 | |
| Triggers | `SaveOnSkillLevelUp` / `SaveOnNewRecipe` / `SaveOnBossKill` / `SaveAfterCombat` | true | |
| Triggers | `FallbackIntervalMin` | 30 | 0 = off |
| Combat | `CombatCooldownSec` | 10 | "In combat" for this long after damage or an attack |
| Combat | `CombatScanRadiusM` | 30 | |
| Combat | `AllowProfileSaveInCombat` | true | Important saves keep your items in combat (logout point unchanged) |
| Combat | `SafeServerPushSave` | true | The server's regular save doesn't move your logout point in combat |
| Server | `RequestWorldSaveAsAdmin` | false | Admins only: also ask the server to save the world after important saves. Makes everybody stutter |
| Server | `WorldSaveMinIntervalMin` | 5 | |

## Good to know

- Each save takes a moment (longer with Steam Cloud). That's why there's a cooldown and saves are merged.
- Chests, buildings and tombstones live on the server, and a crash of your game doesn't lose them. Your character file is on your PC, and that's what SmartSave protects. Without it, a crash after moving items can **duplicate** them (in the chest and back in your bag) or **lose** them (out of the chest, but not in your bag).
- On a host (non-dedicated) SmartSave never delays the world save.

## Installation

With a mod manager (Gale, r2modman, Thunderstore App): install **SmartSave**.

By hand: install [BepInExPack Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/), then copy `SmartSave.dll` into `BepInEx/plugins/`.
