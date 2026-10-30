# SmartSave

**Don't lose your progress when the game crashes.**

Valheim saves your character (inventory, equipment, skills, recipes, map) only every few minutes, or every 30 minutes without a server setting. When your game crashes, everything since that save is gone:

- the armor you just crafted, and the materials it used up,
- items you moved in or out of a chest: **duplicated or lost for good**, because the chest is saved on the server but your bag isn't,
- your whole corpse run, if you had just emptied your tombstone,
- skill levels, new recipes, a forsaken power.

**SmartSave saves your character at exactly those moments**: after crafting, after closing a chest, after taking your tombstone, right before every teleport, and more. A crash costs you seconds, not minutes.

It never moves your respawn point while you are fighting, falling or swimming, so after a crash you don't reconnect into the fight.

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

## How long a save takes

Every character save freezes the game for a moment. That's the game's own save routine, the same as its regular autosave. Two things decide how long it takes: whether the explored map is written, and where your character is stored.

Measured on a normal PC (normal frame 10-20 ms):

| Character storage | Save with map | Save without map |
|---|---|---|
| **Local** | ~200-280 ms | **~20 ms**, barely noticeable |
| **Steam Cloud** | ~300-400 ms | ~100 ms |

**The map** is by far the slowest part (about 85-90% of a local save). By default (`MapSave = OnPortal`), only the save right before a teleport writes it. The other SmartSave saves keep your items, skills and position safe and leave the stored map as it was. The map is also saved by the game's own saves (server save interval, logout) and by F6. After a crash you can lose map exploration and new pins since the last save that wrote the map. Never items.

**Local instead of Steam Cloud** makes every save faster still, SmartSave's and the game's own. With Steam Cloud, replacing the character file alone takes about 70-85 ms. With a local character it's about 1-2 ms. You can move a character between Steam Cloud and local storage in Valheim's **Manage saves** menu (main menu, character selection). A local character isn't synced to other PCs and isn't backed up by Steam, so keep your own backups.

## Settings

All settings are in `BepInEx/config/nfp007.SmartSave.cfg` (also in ConfigurationManager, F1).

| Section | Setting | Default | |
|---|---|---|---|
| General | `Enabled` | true | |
| General | `LogSaves` | true | One line per save in the BepInEx log, with the reason |
| General | `ShowSaveMessage` | false | Small "Saved" message top left |
| General | `MinSaveIntervalSec` | 15 | Minimum gap between normal saves (important saves ignore it) |
| General | `ManualSaveKey` | F6 | `None` = off. Always saves everything, including the map |
| General | `MapSave` | OnPortal | When SmartSave's saves also write the explored map (see below): `OnPortal`, `Always`, `Never` |
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
| Debug | `Profiling` | false | Measures every save (SmartSave's and the game's own) and writes the times to the BepInEx log and `BepInEx/SmartSave_profile.csv`. Needs a restart |

## Good to know

- Each save takes a moment (see above). That's why there's a cooldown and saves are merged.
- Chests, buildings and tombstones live on the server, so a crash of your game doesn't touch them. Your character file is on your PC, and that's what SmartSave protects.
- It doesn't stop the game from crashing. It makes sure a crash costs you as little as possible.
- On a host (non-dedicated) SmartSave never delays the world save.

## Installation

With a mod manager (Gale, r2modman, Thunderstore App): install **SmartSave**.

By hand: install [BepInExPack Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/), then copy `SmartSave.dll` into `BepInEx/plugins/`.
