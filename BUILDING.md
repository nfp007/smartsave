# Building SmartSave

Needs the free [.NET SDK 8](https://dotnet.microsoft.com/download).

1. Create `lib/core/` and copy `BepInEx.dll` and `0Harmony.dll` into it, from `BepInEx/core` of any BepInEx Valheim install.
2. Tell the build where the game DLLs (`Valheim/valheim_Data/Managed/`) are. Pick one:
   - **`local.props`** (recommended, not committed). Create it next to `SmartSave.csproj`:
     ```xml
     <Project>
       <PropertyGroup>
         <GameLib>D:\SteamLibrary\steamapps\common\Valheim\valheim_Data\Managed\</GameLib>
       </PropertyGroup>
     </Project>
     ```
   - copy the contents of `valheim_Data/Managed/` into `lib/valheim/`,
   - pass it on the command line: `dotnet build -c Release -p:GameLib=<path>\`,
   - do nothing if Valheim is in the default Steam folder (`C:\Program Files (x86)\Steam\steamapps\common\Valheim`).
3. Build:

   ```
   dotnet build -c Release
   ```

   The plugin is written to `bin/Release/SmartSave.dll`.

`lib/` and `local.props` are in `.gitignore`. Game and BepInEx DLLs must never be committed.

## Release package

`package/SmartSave/` holds the Thunderstore layout:

```
manifest.json
README.md
CHANGELOG.md
icon.png
plugins/SmartSave.dll
```

`package.ps1` (Windows) or `package.sh` builds, copies the DLL, README and CHANGELOG in, and zips it to `package/SmartSave-<version>.zip`. Upload that zip to Thunderstore / Hexium.

Before a release, raise the version in all three places: `package/SmartSave/manifest.json`, `src/Plugin.cs` (`Version`) and `SmartSave.csproj` (`<Version>`). Add a section to `CHANGELOG.md`.

Thunderstore doesn't want the `author` field in `manifest.json` (the team name is the author). Gale's "Import local mod" uses it.

## Checking hooks after a game update

```
pip install dnfile dncil
python tools/valheim_il.py members <path>/assembly_valheim.dll "InventoryGui::"
python tools/valheim_il.py il <path>/assembly_valheim.dll "Game::SavePlayerProfile"
```
