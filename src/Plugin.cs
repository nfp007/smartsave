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
        public const string Version = "0.2.0";

        internal static Plugin Instance;
        internal static ManualLogSource Log;

        // [General]
        internal static ConfigEntry<bool> Enabled, LogSaves, ShowSaveMessage;
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
            ShowSaveMessage = Config.Bind("General", "ShowSaveMessage", false, "Show a small \"Saved\" message top left after each SmartSave save.");
            MinSaveIntervalSec = Config.Bind("General", "MinSaveIntervalSec", 15f,
                "Minimum seconds between two normal saves. Important saves (crafting, chests, tombstone, teleport, manual) ignore it.");
            ManualSaveKey = Config.Bind("General", "ManualSaveKey", new KeyboardShortcut(KeyCode.F6),
                "Key to save right now. Set to None to turn it off.");

            MapSave = Config.Bind("General", "MapSave", MapSaveMode.OnPortal,
                "When SmartSave's saves also write your explored map. The map is by far the slowest part of a save.\n" +
                "Measured on a normal PC: a save with the map freezes the game for about 200-280 ms with a local character\n" +
                "and 300-400 ms with a Steam Cloud character. Without the map it takes about 20 ms locally\n" +
                "(about 100 ms with Steam Cloud).\n" +
                "  OnPortal = only the save right before a teleport writes the map (the screen fades out anyway).\n" +
                "  Always   = every SmartSave save writes the map: nothing of the map is ever lost, but every save freezes.\n" +
                "  Never    = SmartSave never writes the map.\n" +
                "The game's own saves (server save interval, logout) and the manual save key always write the map.\n" +
                "Without the map, a crash can lose map exploration and new pins since the last save that wrote it. Never items.");

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

            Profiling = Config.Bind("Debug", "Profiling", false,
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
