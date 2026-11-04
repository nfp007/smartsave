using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SmartSave
{
    /// <summary>
    /// SmartSave – saves your character when it matters, by nfp007.
    /// After crafting, after closing a chest, after taking your tombstone, right before a teleport.
    /// Never moves your logout point while you are fighting, falling or swimming.
    /// Client-side only: the server does not need it.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "nfp007.SmartSave";
        public const string Name = "SmartSave";
        public const string Version = "1.2.11";

        internal static Plugin Instance;
        internal static ManualLogSource Log;

        // [General]
        internal static ConfigEntry<bool> Enabled, LogSaves, FastMapSave;
        internal static ConfigEntry<float> MapPrewarmSec, SkipRepeatSaveSec;
        internal static ConfigEntry<BackgroundWriteMode> BackgroundWrite;
        internal static ConfigEntry<SaveMessageMode> SaveMessage;
        internal static ConfigEntry<float> MinSaveIntervalSec;
        internal static ConfigEntry<KeyboardShortcut> ManualSaveKey;
        internal static ConfigEntry<MapSaveMode> MapSave;

        // [Triggers]
        internal static ConfigEntry<bool> SaveAfterCrafting, SaveOnChestClose, SaveOnTombstone,
            SaveBeforeTeleport, SaveAfterTeleport, SaveOnSkillLevelUp, SaveOnNewRecipe,
            SaveOnBossKill, SaveAfterCombat;
        internal static ConfigEntry<float> GuiCloseDelaySec, GuiMaxDelaySec, ArrivalDelaySec, FallbackIntervalMin;

        // [Combat]
        internal static ConfigEntry<float> CombatCooldownSec, CombatScanRadiusM;
        internal static ConfigEntry<bool> AllowProfileSaveInCombat, SafeServerPushSave;

        // [Server]
        internal static ConfigEntry<bool> RequestWorldSaveAsAdmin;
        internal static ConfigEntry<float> WorldSaveMinIntervalMin;

        // [Debug]
        internal static ConfigEntry<bool> Profiling;

        private Harmony _harmony;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Turn SmartSave on or off.");
            LogSaves = Config.Bind("General", "LogSaves", true, "Write a line to the BepInEx log for every save (with the reason).");
            SaveMessage = Config.Bind("General", "SaveMessage", SaveMessageMode.Off,
                "Show a small \"Saved\" message top left after SmartSave saves.\n" +
                "  Off       = no message (the manual save key always shows one).\n" +
                "  Important = only after crafting, chest changes, the tombstone and a boss / forsaken power.\n" +
                "  All       = after every SmartSave save.");
            MinSaveIntervalSec = Config.Bind("General", "MinSaveIntervalSec", 15f,
                "Cooldown: the shortest gap in seconds between two less important saves (arrival, boss, end of combat, skill up,\n" +
                "new recipe, fallback), so a burst of events gives one save. It never triggers a save by itself.\n" +
                "Important saves (crafting, chests, tombstone, teleport, manual) ignore it.");
            ManualSaveKey = Config.Bind("General", "ManualSaveKey", new KeyboardShortcut(KeyCode.F6),
                "Key to save right now. Set to None to turn it off.");

            MapSave = Config.Bind("General", "MapSave", MapSaveMode.OnPortal,
                "When SmartSave's saves also write your explored map. The map is by far the slowest part of a save.\n" +
                "Measured on a normal PC (with FastMapSave on): the map part of a save takes under 1 ms when the map is reused\n" +
                "and ~50 ms right after exploring. Without FastMapSave it takes ~200 ms. A whole save takes ~20 ms with a local\n" +
                "character and ~100-300 ms with a Steam Cloud character (the Steam Cloud file handling).\n" +
                "  OnPortal = only the save right before a teleport writes the map (the screen fades out anyway).\n" +
                "  Always   = every SmartSave save writes the map: nothing of the map is ever lost, but every save freezes.\n" +
                "  Never    = SmartSave never writes the map.\n" +
                "The game's own saves (server save interval, logout) and the manual save key always write the map.\n" +
                "Without the map, a crash can lose map exploration and new pins since the last save that wrote it. Never items.");

            FastMapSave = Config.Bind("General", "FastMapSave", true,
                "Build the explored map data much faster whenever it is saved (by SmartSave and by the game's own saves),\n" +
                "and reuse it when your explored map and pins haven't changed since the last save (under 1 ms instead of ~50 ms).\n" +
                "Safeguard: once per game start, when you join a world (usually behind the loading screen), the map data is built\n" +
                "both ways (SmartSave's and the game's) and compared byte by byte. Only if they are identical is the faster version\n" +
                "used for the rest of the session. If they differ (after a game update, or because another mod changes the map data),\n" +
                "SmartSave switches back to the game's own method and writes a warning to the log.\n" +
                "If another map mod misbehaves (pins or map not saved as expected), set this to false.");

            MapPrewarmSec = Config.Bind("General", "MapPrewarmSec", 60f,
                "With FastMapSave: every this many seconds, if your explored map or pins changed, SmartSave prepares the map data\n" +
                "in the background (a ~1-2 ms copy in the game, the rest on another CPU core). The next save that writes the map then\n" +
                "takes ~1-2 ms for the map instead of ~50 ms. If nothing changed since the last save, the map is reused anyway.\n" +
                "0 = off (the map is then prepared during the save itself).");
            BackgroundWrite = Config.Bind("General", "BackgroundWrite", BackgroundWriteMode.CloudOnly,
                "Steam Cloud characters: the save is made in the frame as always (all data captured at once), but writing it to\n" +
                "Steam Cloud happens in the background through Steam's own background write. A Steam Cloud save then freezes the\n" +
                "game much shorter. The game's safe order is kept (.new, .old, .fch). Quitting and logging out always wait for it.\n" +
                "If Steam reports an error, the save is kept as a local backup (like the game does) and this switches itself off.\n" +
                "  CloudOnly = Steam Cloud characters only (local characters already save fast).\n" +
                "  Off       = the game's own cloud save.");
            SkipRepeatSaveSec = Config.Bind("General", "SkipRepeatSaveSec", 30f,
                "Skip the game's own regular save (the server's save interval, or the game's 30-minute timer) if a full save\n" +
                "(with the map and your position) already ran within this many seconds. It would write exactly the same again.\n" +
                "Saves when quitting, logging out or sleeping are never skipped. 0 = off.");

            SaveAfterCrafting = Config.Bind("Triggers", "SaveAfterCrafting", true,
                "Save after crafting, upgrading or repairing, as soon as the crafting window is closed.");
            SaveOnChestClose = Config.Bind("Triggers", "SaveOnChestClose", true,
                "Save after closing a chest (or the inventory) - only if your inventory changed while it was open.");
            GuiCloseDelaySec = Config.Bind("Triggers", "GuiCloseDelaySec", 1.5f,
                "Seconds the inventory/crafting window must stay closed before the save runs. Going from chest to chest gives one save, not ten.");
            GuiMaxDelaySec = Config.Bind("Triggers", "GuiMaxDelaySec", 20f,
                "If the window stays open longer than this after a change, save anyway.");
            SaveOnTombstone = Config.Bind("Triggers", "SaveOnTombstone", true, "Save right after you take everything from your tombstone.");
            SaveBeforeTeleport = Config.Bind("Triggers", "SaveBeforeTeleport", true,
                "Save just before any teleport (portals, dungeon entrances, mods that teleport).");
            SaveAfterTeleport = Config.Bind("Triggers", "SaveAfterTeleport", true, "Save a few seconds after arriving from a teleport.");
            ArrivalDelaySec = Config.Bind("Triggers", "ArrivalDelaySec", 5f, "Seconds to wait after arriving before saving.");
            SaveOnSkillLevelUp = Config.Bind("Triggers", "SaveOnSkillLevelUp", true, "Save after a skill level up (when out of combat).");
            SaveOnNewRecipe = Config.Bind("Triggers", "SaveOnNewRecipe", true, "Save after learning a new recipe or building piece.");
            SaveOnBossKill = Config.Bind("Triggers", "SaveOnBossKill", true, "Save after a boss dies near you, or when you take a new forsaken power.");
            SaveAfterCombat = Config.Bind("Triggers", "SaveAfterCombat", true,
                "If something waits to be saved, save as soon as the fight is over.");
            FallbackIntervalMin = Config.Bind("Triggers", "FallbackIntervalMin", 30f,
                "Save at the next safe moment if nothing saved the character for this many minutes. 0 = off.\n" +
                "(On servers with -saveinterval the server already asks every client to save at that interval.)");

            CombatCooldownSec = Config.Bind("Combat", "CombatCooldownSec", 10f,
                "You count as 'in combat' for this many seconds after taking damage or attacking.");
            CombatScanRadiusM = Config.Bind("Combat", "CombatScanRadiusM", 30f,
                "An alerted enemy that targets you within this distance (metres) means 'in combat'.");
            AllowProfileSaveInCombat = Config.Bind("Combat", "AllowProfileSaveInCombat", true,
                "true = important saves (crafting, chests, tombstone, teleport) still save your inventory during combat, " +
                "but your logout point is not moved.\nfalse = wait until the fight is over.");
            SafeServerPushSave = Config.Bind("Combat", "SafeServerPushSave", true,
                "The server regularly tells every client to save (with -saveinterval). Vanilla then also moves your logout point, " +
                "even mid-fight. true = during combat that save keeps your inventory but does not move the logout point.");

            RequestWorldSaveAsAdmin = Config.Bind("Server", "RequestWorldSaveAsAdmin", false,
                "Only for server admins. After important saves, also ask the server to save the world.\n" +
                "This makes the game stutter for everybody. Keep it off unless your server crashes too.");
            WorldSaveMinIntervalMin = Config.Bind("Server", "WorldSaveMinIntervalMin", 5f, "Minimum minutes between two world save requests.");

            Profiling = Config.Bind("Debug", "Profiling", true,
                "Measure every character save (SmartSave's and the game's own) and write the results to the BepInEx log\n" +
                "and to BepInEx/SmartSave_profile.csv: total time, the slow parts (map, compression, file write, Steam Cloud),\n" +
                "memory and the frame times right after the save. Needs a game restart to change.");

            _harmony = new Harmony(Guid);
            _harmony.PatchAll(typeof(Plugin).Assembly);
            SaveProfiler.Init(_harmony);
            Log.LogInfo($"{Name} {Version} loaded");
        }

        private void Update()
        {
            try
            {
                SaveProfiler.Tick();
                FastMapData.Tick();
                CloudWriter.Tick();
                SaveScheduler.Tick();
            }
            catch (System.Exception e)
            {
                Log.LogError(e);
            }
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }
}
