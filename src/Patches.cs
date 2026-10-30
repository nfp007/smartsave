using HarmonyLib;
using UnityEngine;

namespace SmartSave
{
    /// <summary>All Harmony patches in one place. Signatures checked against assembly_valheim.dll (2026-10-30).</summary>
    internal static class Patches
    {
        private static bool IsLocal(Character c) => c != null && c == Player.m_localPlayer;

        // ------------------------------------------------------------ bookkeeping of every profile save

        /// <summary>Any character save (ours, the vanilla timer, the server push, logout) resets our state.</summary>
        [HarmonyPatch(typeof(Game), nameof(Game.SavePlayerProfile))]
        private static class Game_SavePlayerProfile
        {
            private static void Postfix(bool setLogoutPoint) => SaveScheduler.OnProfileSaved(setLogoutPoint);
        }

        /// <summary>SmartSave's saves may leave the stored map as it is (MapSave setting, see SaveScheduler.IncludeMap).</summary>
        [HarmonyPatch(typeof(Minimap), nameof(Minimap.SaveMapData))]
        private static class Minimap_SaveMapData
        {
            private static bool Prefix() => !SaveScheduler.SkipMapNow;
        }

        /// <summary>
        /// The server tells every client to save (ZNet.SaveOtherPlayerProfiles). Vanilla always moves the logout point.
        /// In combat (or mid-air, swimming...) we save the inventory but keep the old logout point.
        /// </summary>
        [HarmonyPatch(typeof(ZNet), "RPC_SavePlayerProfile")]
        private static class ZNet_RPC_SavePlayerProfile
        {
            private static bool Prefix()
            {
                if (!Plugin.Enabled.Value || !Plugin.SafeServerPushSave.Value) return true;
                var p = Player.m_localPlayer;
                if (p == null || Game.instance == null || SafetyState.IsLogoutSafe(p)) return true;
                if (Plugin.LogSaves.Value) Plugin.Log.LogInfo("server save request while not safe: saving without moving the logout point");
                Game.instance.SavePlayerProfile(false, true);
                return false;
            }
        }

        // ------------------------------------------------------------ crafting & inventory window

        /// <summary>Crafting, upgrading (vanilla and mods that go through DoCrafting, e.g. craft-from-chests).</summary>
        [HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
        private static class InventoryGui_DoCrafting
        {
            private static void Postfix(Player player)
            {
                if (IsLocal(player)) SaveScheduler.OnCrafted();
            }
        }

        [HarmonyPatch(typeof(InventoryGui), "RepairOneItem")]
        private static class InventoryGui_RepairOneItem
        {
            private static void Postfix() => SaveScheduler.OnCrafted();
        }

        /// <summary>Any change to the local player's inventory while the window is open (chests, crafting, enchanting...).</summary>
        [HarmonyPatch(typeof(Inventory), "Changed")]
        private static class Inventory_Changed
        {
            private static void Postfix(Inventory __instance)
            {
                var p = Player.m_localPlayer;
                if (p != null && __instance == p.GetInventory()) SaveScheduler.OnInventoryChanged();
            }
        }

        // ------------------------------------------------------------ tombstone

        [HarmonyPatch(typeof(TombStone), "OnTakeAllSuccess")]
        private static class TombStone_OnTakeAllSuccess
        {
            private static void Postfix()
            {
                if (Plugin.Enabled.Value && Plugin.SaveOnTombstone.Value)
                    SaveScheduler.Request(Reason.Tombstone, Prio.High, true);
            }
        }

        // ------------------------------------------------------------ teleport (portals, dungeon entrances, teleport mods)

        [HarmonyPatch(typeof(Player), nameof(Player.TeleportTo))]
        private static class Player_TeleportTo
        {
            private static void Prefix(Player __instance)
            {
                if (IsLocal(__instance)) SaveScheduler.BeforeTeleport(__instance);
            }
        }

        // ------------------------------------------------------------ progress

        [HarmonyPatch(typeof(Player), nameof(Player.OnSkillLevelup))]
        private static class Player_OnSkillLevelup
        {
            private static void Postfix(Player __instance)
            {
                if (IsLocal(__instance) && Plugin.SaveOnSkillLevelUp.Value)
                    SaveScheduler.Request(Reason.SkillUp, Prio.Low, true);
            }
        }

        [HarmonyPatch(typeof(Player), "AddKnownRecipe")]
        private static class Player_AddKnownRecipe
        {
            private static void Postfix(Player __instance)
            {
                if (IsLocal(__instance) && Plugin.SaveOnNewRecipe.Value)
                    SaveScheduler.Request(Reason.Recipe, Prio.Low, true);
            }
        }

        [HarmonyPatch(typeof(Player), "AddKnownPiece")]
        private static class Player_AddKnownPiece
        {
            private static void Postfix(Player __instance)
            {
                if (IsLocal(__instance) && Plugin.SaveOnNewRecipe.Value)
                    SaveScheduler.Request(Reason.Recipe, Prio.Low, true);
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.SetGuardianPower))]
        private static class Player_SetGuardianPower
        {
            private static void Postfix(Player __instance)
            {
                if (IsLocal(__instance) && Plugin.SaveOnBossKill.Value)
                    SaveScheduler.Request(Reason.Boss, Prio.Normal, true);
            }
        }

        /// <summary>A boss died near us (runs where the boss is owned; best effort).</summary>
        [HarmonyPatch(typeof(Character), "OnDeath")]
        private static class Character_OnDeath
        {
            private static void Postfix(Character __instance)
            {
                if (!Plugin.SaveOnBossKill.Value || __instance == null || !__instance.IsBoss()) return;
                var p = Player.m_localPlayer;
                if (p == null) return;
                if (Vector3.Distance(p.transform.position, __instance.transform.position) < 100f)
                    SaveScheduler.Request(Reason.Boss, Prio.Normal, true, 5f);
            }
        }

        // ------------------------------------------------------------ combat signals

        [HarmonyPatch(typeof(Player), "OnDamaged")]
        private static class Player_OnDamaged
        {
            private static void Postfix(Player __instance)
            {
                if (IsLocal(__instance)) SafetyState.MarkCombatAction();
            }
        }
    }
}
