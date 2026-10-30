# CLAUDE.md

Guidance for Claude (and other contributors) working in this repository.

## What this is

**SmartSave** is a client-side BepInEx 5 / Harmony mod for Valheim. It saves the player's character at the moments that matter (after crafting, after closing a chest, after taking the tombstone, before teleports, after combat…) and never moves the logout point while the player is in danger. The server doesn't need it.

- Author / Thunderstore team: `nfp007`. Plugin GUID: `nfp007.SmartSave`.
- Published on Thunderstore and Hexium. License: MIT.
- Design and the vanilla behaviour it relies on: `docs/DESIGN.md`. Read it before changing save logic.
- User-facing docs: `README.md` (also the Thunderstore page). History: `CHANGELOG.md`.

## Build

```
dotnet build -c Release        # → bin/Release/SmartSave.dll
./package.sh   or   ./package.ps1   # → package/SmartSave-<version>.zip (Thunderstore layout)
```

- Target `net48`, `LangVersion 9.0`, offline build: no NuGet packages. All references come from `lib/core/` (BepInEx.dll, 0Harmony.dll) and the game's `valheim_Data/Managed/`.
- Game DLL path (`GameLib`): `-p:GameLib=…` → `local.props` → `lib/valheim/` → default Steam path. Details in `BUILDING.md`.
- **Never commit** `lib/`, `local.props`, game or BepInEx DLLs, or built zips/DLLs (all in `.gitignore`).
- There are no automated tests. A change is only verified by building it and checking it in the game (see "Testing").

## Code layout

```
src/Plugin.cs        entry point: all config entries (Config.Bind), Harmony.PatchAll, Update() → SaveScheduler.Tick()
src/SaveScheduler.cs pending save reasons (flags), priorities, cooldown, inventory-window debounce, SaveNow()
src/SafetyState.cs   IsBlocked / InCombat / IsLogoutSafe / IsTyping
src/Patches.cs       every Harmony patch, one nested private static class per patch
tools/valheim_il.py  list members / disassemble methods of assembly_valheim.dll
```

## Rules for the save logic

- Every character save goes through `Game.SavePlayerProfile(bool setLogoutPoint, bool isFromRpc)`. Its postfix (`SaveScheduler.OnProfileSaved`) is the single place that clears pending state. Keep it that way.
- SmartSave's own saves call `SaveScheduler.SaveNow`, which **restores `Game.m_saveTimer`** afterwards. On a host that timer drives the world save. Never call `SavePlayerProfile` directly from new code.
- The logout point (`setLogoutPoint: true`) is only set when `SafetyState.IsLogoutSafe` is true.
- Saves are not free (loading-screen push, Steam Cloud mount). Don't add triggers that fire per item or per frame. Merge them through `SaveScheduler.Request(reason, prio, wantLogout, delay)`.
- New trigger checklist: add a `Reason` flag → add a config entry in `Plugin.cs` (section `Triggers`, with a clear description) → add the patch in `Patches.cs` → document it in the README table, `docs/DESIGN.md` section 4 and `CHANGELOG.md`.
- Only react to the **local** player (`Player.m_localPlayer`). Patched methods also run for other characters.

## Harmony conventions

- Style: `[HarmonyPatch(typeof(X), nameof(X.Method))]` for public methods, `"Method"` string for non-public ones. Nested `private static class X_Method` with `Prefix`/`Postfix`.
- Private members: use `AccessTools` (e.g. `AccessTools.MethodDelegate` for `MusicMan.InCombat`). Fail softly with a log warning if a member is missing.
- **Verify every target against the real DLL before using it.** Valheim renames and changes signatures between patches. Use `python tools/valheim_il.py members <dll> "Type::"` and `… il <dll> Type::Method`. Don't rely on memory or old modding wikis.
- Catch exceptions in `Update()`. Never let SmartSave break the game loop.

## Releasing

1. Raise the version in **three** places: `src/Plugin.cs` (`Version`), `SmartSave.csproj` (`<Version>`), `package/SmartSave/manifest.json` (`version_number`).
2. Add a section at the top of `CHANGELOG.md`: short, user-facing bullets.
3. Update `README.md` if behaviour or settings changed. The package scripts copy README and CHANGELOG into the package.
4. `./package.ps1` → upload `package/SmartSave-<version>.zip` to Thunderstore and Hexium.
5. Manifest `description` must stay ≤ 250 characters. The dependency is `denikson-BepInExPack_Valheim`.

## Testing (in game)

Enable `LogSaves` (default on) and watch `BepInEx/LogOutput.log` for `[Info:SmartSave] saved (reason=…, logoutPoint=…)`:
- craft and close the station → `Craft`; move items in a chest and close it → `Inventory`; chest to chest quickly → one save
- take a portal → `Teleport` before, `Arrival` ~5 s after
- F6 → on-screen message
- get hit, then wait for the server's `-saveinterval` save → `server save request while not safe…`
- tombstone take-all → `Tombstone`

## Writing style

- Docs and config descriptions: plain, short sentences in English. User-facing text describes what the player sees, not internals.
- Keep code comments about *why*, not *what*.
