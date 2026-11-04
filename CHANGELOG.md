# Changelog

## 1.3.0 (not released yet; test build 1.2.11 with profiling on)
- New `BackgroundWrite` (`CloudOnly`): for Steam Cloud characters the save is still made in the frame, but writing it to Steam Cloud happens in the background through Steam's own background write. The game's safe order is kept (.new, .old, .fch), every save is read back and compared, quitting and logging out wait for it, and on any Steam error the save is kept as a local backup and the feature switches itself off.

## 1.2.0
- The map data is reused when your explored map and pins haven't changed: the map part of a save now takes under 1 ms instead of ~50 ms (measured).
- New `MapPrewarmSec` (60 s): when the map has changed, its data is prepared in the background, so the next save can reuse it.
- The fast map save's safeguard check now runs when you join a world (usually behind the loading screen), not at the first save.
- New `SkipRepeatSaveSec` (30 s): the game's own regular save is skipped right after a full SmartSave save (with the map and your position).
- New icon: an iron-rimmed wooden shield with a glowing save symbol.

## 1.1.0
- README: the save time table now compares the game's own map save, SmartSave's fast map save and a save without the map side by side.
- Corrected the Steam Cloud time with the fast map save: ~170-310 ms (was ~170-270 ms).

## 1.0.0
- Faster map save (`FastMapSave`, on by default): the explored map data is built in one go instead of one point at a time (~10 ms instead of ~140-230 ms, plus compression). Every save that writes the map gets faster, including the game's own saves. The result is the same as the game's; the first map save after each game start builds it both ways and compares them, and the faster version is switched off with a log warning if they ever differ.
- `SaveMessage` setting (`Off` by default, `Important` or `All`) replaces `ShowSaveMessage`.

## 0.2.0
- Much shorter freeze: SmartSave's saves no longer write the explored map every time. The map is about 85-90% of a save (with a local character a save takes ~20 ms instead of ~200-280 ms).
  New setting `MapSave`: `OnPortal` (default, only the save right before a teleport writes the map), `Always` or `Never`.
  The manual save key (F6) and the game's own saves always write the map.
- New `[Debug] Profiling` setting (off by default): measures every character save, SmartSave's and the game's own, and writes the results to the BepInEx log and `BepInEx/SmartSave_profile.csv`.
- Fixed: logging in triggered "new recipe" and "forsaken power" saves.
- README: how long a save takes, and why a local character saves faster than a Steam Cloud one.

## 0.1.0
- First version.
- Saves after crafting, chest changes, tombstone recovery, before and after teleports, boss kills, skill level ups, new recipes, after combat, F6, and a 30-minute fallback.
- The logout point only moves when you are safe (not in combat, on the ground, not swimming or riding).
- The server's regular save (`-saveinterval`) doesn't move the logout point in combat.
- Never delays the world save on a host.
