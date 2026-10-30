using System;
using HarmonyLib;
using UnityEngine;

namespace SmartSave
{
    /// <summary>
    /// Answers three questions about the local player:
    ///   IsBlocked     – no save at all right now (dead, teleporting, no player).
    ///   InCombat      – fighting: inventory may be saved, logout point must not move.
    ///   IsLogoutSafe  – a good place to come back to after a crash.
    /// </summary>
    internal static class SafetyState
    {
        /// <summary>Vanilla combat-music state (MusicMan.InCombat is private).</summary>
        private static readonly Func<MusicMan, bool> MusicInCombat = MakeMusicInCombat();

        private static float _lastCombatActionTime = -999f;
        private static float _nextScanTime;
        private static bool _enemyTargetsMe;

        private static Func<MusicMan, bool> MakeMusicInCombat()
        {
            try
            {
                var m = AccessTools.Method(typeof(MusicMan), "InCombat");
                return m == null ? null : AccessTools.MethodDelegate<Func<MusicMan, bool>>(m);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"MusicMan.InCombat not found, using own combat detection only: {e.Message}");
                return null;
            }
        }

        /// <summary>Called when the local player takes damage or attacks.</summary>
        internal static void MarkCombatAction() => _lastCombatActionTime = Time.time;

        internal static bool IsBlocked(Player p)
        {
            if (p == null || Game.instance == null || ZNet.instance == null) return true;
            if (p.IsDead() || p.IsTeleporting()) return true;
            if (p.InCutscene()) return true;
            return false;
        }

        internal static bool InCombat(Player p)
        {
            if (p == null) return false;
            if (Time.time - _lastCombatActionTime < Plugin.CombatCooldownSec.Value) return true;
            if (p.InAttack() || p.IsBlocking()) return true;

            var music = MusicMan.instance;
            if (music != null && MusicInCombat != null)
            {
                try { if (MusicInCombat(music)) return true; }
                catch { /* ignore, fall back to the scan */ }
            }

            if (Time.time >= _nextScanTime)
            {
                _nextScanTime = Time.time + 0.5f;
                _enemyTargetsMe = ScanEnemies(p);
            }
            return _enemyTargetsMe;
        }

        private static bool ScanEnemies(Player p)
        {
            float r = Plugin.CombatScanRadiusM.Value;
            float r2 = r * r;
            Vector3 pos = p.transform.position;
            foreach (var c in Character.GetAllCharacters())
            {
                if (c == null || c == p || c.IsPlayer() || c.IsDead()) continue;
                if ((c.transform.position - pos).sqrMagnitude > r2) continue;
                var ai = c.GetBaseAI();
                if (ai == null || !ai.IsAlerted()) continue;
                if (!BaseAI.IsEnemy(c, p)) continue;
                if (ai.GetTargetCreature() == p) return true;
            }
            return false;
        }

        /// <summary>Would it be fine to respawn here after a crash?</summary>
        internal static bool IsLogoutSafe(Player p)
        {
            if (IsBlocked(p) || InCombat(p)) return false;
            if (!p.IsOnGround()) return false;
            if (p.IsSwimming() || p.IsAttached() || p.IsAttachedToShip()) return false;
            if (p.IsFlying()) return false;
            return true;
        }

        /// <summary>The player is typing somewhere (chat, console, sign, search box).</summary>
        internal static bool IsTyping()
        {
            if (Chat.instance != null && Chat.instance.HasFocus()) return true;
            if (Console.IsVisible() || TextInput.IsVisible() || Minimap.InTextInput()) return true;
            return false;
        }
    }
}
