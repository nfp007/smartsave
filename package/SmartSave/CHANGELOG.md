# Changelog

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
