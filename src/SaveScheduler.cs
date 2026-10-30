using System;
using UnityEngine;

namespace SmartSave
{
    [Flags]
    internal enum Reason
    {
        None = 0,
        Craft = 1 << 0,
        Inventory = 1 << 1,   // inventory changed while a chest / the inventory window was open
        Tombstone = 1 << 2,
        Teleport = 1 << 3,
        Arrival = 1 << 4,
        SkillUp = 1 << 5,
        Recipe = 1 << 6,
        Boss = 1 << 7,
        CombatEnd = 1 << 8,
        Fallback = 1 << 9,
        Manual = 1 << 10,
    }

    /// <summary>When SmartSave's saves also write the explored map (the slow part of a save).</summary>
    public enum MapSaveMode
    {
        OnPortal,
        Always,
        Never,
    }

    internal enum Prio
    {
        None = 0,
        Low = 1,      // skill up, new recipe, fallback: respects cooldown, waits for no combat
        Normal = 2,   // arrival, boss, combat end: respects cooldown
        High = 3,     // crafting, chests, tombstone: ignores cooldown
    }

    /// <summary>
    /// Collects save requests and runs one save at the right moment.
    /// Every save (ours or the game's own) goes through Game.SavePlayerProfile, whose postfix calls <see cref="OnProfileSaved"/>.
    /// </summary>
    internal static class SaveScheduler
    {
        private static Reason _pending;
        private static Prio _prio;
        private static bool _wantLogout;
        private static float _pendingSince;
        private static float _notBefore;

        // inventory/crafting window tracking
        private static bool _guiDirty;
        private static Reason _guiReason;
        private static float _guiDirtySince;
        private static float _guiClosedAt = -1f;

        private static float _lastSaveTime;
        private static float _lastWorldSaveRequest = -9999f;
        private static bool _wasInCombat;
        private static bool _wasTeleporting;
        private static float _nextTick;
        private static bool _savingNow;

        private static bool _skipMapNow;
        private static bool _inSession;
        private static float _sessionStart;

        internal static bool SavingNow => _savingNow;

        /// <summary>True only during a SmartSave save that leaves the stored map as it is.</summary>
        internal static bool SkipMapNow => _skipMapNow;

        /// <summary>Reason of the SmartSave save that is running right now (for the profiler).</summary>
        internal static Reason CurrentReason { get; private set; }

        // ---------------------------------------------------------------- requests

        // Loading the character sets known recipes, the forsaken power and skills: not a reason to save.
        private const Reason LoginNoise = Reason.Recipe | Reason.Boss | Reason.SkillUp;
        private const float LoginQuietSec = 10f;

        internal static void Request(Reason reason, Prio prio, bool wantLogout, float delaySec = 0f)
        {
            if (!Plugin.Enabled.Value) return;
            if ((reason & LoginNoise) != 0 && (!_inSession || Time.time - _sessionStart < LoginQuietSec))
            {
                reason &= ~LoginNoise;
                if (reason == Reason.None) return;
            }
            if (_pending == Reason.None) _pendingSince = Time.time;
            _pending |= reason;
            if (prio > _prio) _prio = prio;
            _wantLogout |= wantLogout;
            if (delaySec > 0f) _notBefore = Mathf.Max(_notBefore, Time.time + delaySec);
        }

        /// <summary>The local player's inventory changed.</summary>
        internal static void OnInventoryChanged()
        {
            if (!Plugin.Enabled.Value || _savingNow) return;
            if (!InventoryGui.IsVisible()) return; // pickups etc.: covered by the fallback / server push
            if (!Plugin.SaveOnChestClose.Value) return;
            MarkGui(Reason.Inventory);
        }

        internal static void OnCrafted()
        {
            if (!Plugin.Enabled.Value || !Plugin.SaveAfterCrafting.Value) return;
            MarkGui(Reason.Craft);
        }

        private static void MarkGui(Reason r)
        {
            if (!_guiDirty) _guiDirtySince = Time.time;
            _guiDirty = true;
            _guiReason |= r;
        }

        /// <summary>Called right before any teleport of the local player. Saves synchronously.</summary>
        internal static void BeforeTeleport(Player p)
        {
            if (!Plugin.Enabled.Value || !Plugin.SaveBeforeTeleport.Value) return;
            if (p == null || p.IsDead() || p.IsTeleporting() || Game.instance == null) return;
            if (SafetyState.InCombat(p) && !Plugin.AllowProfileSaveInCombat.Value) return;
            FlushGui();
            bool logout = SafetyState.IsLogoutSafe(p);
            SaveNow(_pending | Reason.Teleport, logout);
        }

        // ---------------------------------------------------------------- tick

        internal static void Tick()
        {
            if (!Plugin.Enabled.Value) return;
            var p = Player.m_localPlayer;
            if (p == null || Game.instance == null) { ResetSession(); return; }
            if (!_inSession)
            {
                _inSession = true;
                _sessionStart = Time.time;
            }

            // manual key (checked every frame)
            if (!SafetyState.IsTyping() && IsDown(Plugin.ManualSaveKey.Value))
            {
                if (SafetyState.IsBlocked(p)) Message("SmartSave: can't save right now");
                else
                {
                    FlushGui();
                    SaveNow(_pending | Reason.Manual, SafetyState.IsLogoutSafe(p), forceMessage: true);
                }
            }

            if (Time.time < _nextTick) return;
            _nextTick = Time.time + 0.25f;

            // teleport arrival
            bool tp = p.IsTeleporting();
            if (_wasTeleporting && !tp && Plugin.SaveAfterTeleport.Value)
                Request(Reason.Arrival, Prio.Normal, true, Plugin.ArrivalDelaySec.Value);
            _wasTeleporting = tp;

            // inventory/crafting window: save once it has been closed for a moment
            if (_guiDirty)
            {
                bool open = InventoryGui.IsVisible();
                if (open) _guiClosedAt = -1f;
                else if (_guiClosedAt < 0f) _guiClosedAt = Time.time;

                bool closedLongEnough = !open && Time.time - _guiClosedAt >= Plugin.GuiCloseDelaySec.Value;
                bool waitedTooLong = Time.time - _guiDirtySince >= Plugin.GuiMaxDelaySec.Value;
                if (closedLongEnough || waitedTooLong) FlushGui();
            }

            bool inCombat = SafetyState.InCombat(p);

            // combat just ended
            if (_wasInCombat && !inCombat && Plugin.SaveAfterCombat.Value && _wantLogout)
                Request(Reason.CombatEnd, Prio.Normal, true);
            _wasInCombat = inCombat;

            // fallback
            float fb = Plugin.FallbackIntervalMin.Value;
            if (fb > 0f && Time.time - _lastSaveTime >= fb * 60f && _pending == Reason.None)
                Request(Reason.Fallback, Prio.Low, true);

            TryRunPending(p, inCombat);
        }

        private static void FlushGui()
        {
            if (!_guiDirty) return;
            Request(_guiReason, Prio.High, true);
            _guiDirty = false;
            _guiReason = Reason.None;
            _guiClosedAt = -1f;
        }

        private static void TryRunPending(Player p, bool inCombat)
        {
            if (_pending == Reason.None) return;
            if (Time.time < _notBefore) return;
            if (SafetyState.IsBlocked(p)) return;

            bool high = _prio >= Prio.High;
            if (!high && Time.time - _lastSaveTime < Plugin.MinSaveIntervalSec.Value) return;

            if (inCombat)
            {
                // Only important saves run in combat, and never with the logout point.
                if (!high || !Plugin.AllowProfileSaveInCombat.Value) return;
                SaveNow(_pending, false);
                return;
            }

            // Normal saves wait (up to a minute) until the player stands somewhere safe, so the logout point moves too.
            // Important saves run right away; the logout point only moves if it is safe.
            bool logoutSafe = SafetyState.IsLogoutSafe(p);
            if (!high && _wantLogout && !logoutSafe && Time.time - _pendingSince < 60f) return;
            SaveNow(_pending, _wantLogout && logoutSafe);
        }

        // ---------------------------------------------------------------- saving

        private static void SaveNow(Reason reason, bool setLogout, bool forceMessage = false)
        {
            var game = Game.instance;
            if (game == null) return;

            // Game.SavePlayerProfile resets Game.m_saveTimer. On a host that timer also drives the world save,
            // so keep it as it was – our saves must never postpone the vanilla world save.
            float timer = game.m_saveTimer;
            bool includeMap = IncludeMap(reason);
            CurrentReason = reason;
            _skipMapNow = !includeMap;
            _savingNow = true;
            try
            {
                game.SavePlayerProfile(setLogout, false);
            }
            finally
            {
                _savingNow = false;
                _skipMapNow = false;
                game.m_saveTimer = timer;
            }

            if (Plugin.LogSaves.Value)
                Plugin.Log.LogInfo($"saved (reason={ReasonText(reason)}, logoutPoint={setLogout}, map={(includeMap ? "yes" : "skipped")})");
            if (forceMessage || Plugin.ShowSaveMessage.Value)
                Message(setLogout ? "SmartSave: saved" : "SmartSave: saved (position not updated)");

            MaybeRequestWorldSave(reason);
        }

        /// <summary>Postfix of Game.SavePlayerProfile: any profile save (ours, vanilla timer, server push) clears what it covered.</summary>
        internal static void OnProfileSaved(bool setLogoutPoint)
        {
            _lastSaveTime = Time.time;
            _pending = Reason.None;
            _prio = Prio.None;
            _notBefore = 0f;
            if (setLogoutPoint) _wantLogout = false;
            else if (!_wantLogout) _wantLogout = true; // inventory saved, position still old: update it when safe
        }

        private static void MaybeRequestWorldSave(Reason reason)
        {
            if (!Plugin.RequestWorldSaveAsAdmin.Value) return;
            const Reason important = Reason.Craft | Reason.Inventory | Reason.Tombstone | Reason.Manual | Reason.Boss;
            if ((reason & important) == 0) return;
            if (Time.time - _lastWorldSaveRequest < Plugin.WorldSaveMinIntervalMin.Value * 60f) return;
            var znet = ZNet.instance;
            if (znet == null || znet.IsServer()) return; // a host saves the world itself
            var rpc = znet.GetServerRPC();
            if (rpc == null) return;
            _lastWorldSaveRequest = Time.time;
            rpc.Invoke("Save");
            if (Plugin.LogSaves.Value) Plugin.Log.LogInfo("asked the server to save the world");
        }

        /// <summary>F6 always saves everything; otherwise the MapSave setting decides.</summary>
        private static bool IncludeMap(Reason reason)
        {
            if ((reason & Reason.Manual) != 0) return true;
            switch (Plugin.MapSave.Value)
            {
                case MapSaveMode.Always: return true;
                case MapSaveMode.Never: return false;
                default: return (reason & Reason.Teleport) != 0;
            }
        }

        /// <summary>"Craft+Inventory" instead of "Craft, Inventory" (no commas: the profiler writes CSV).</summary>
        internal static string ReasonText(Reason r) => r.ToString().Replace(", ", "+");

        // ---------------------------------------------------------------- helpers

        private static void ResetSession()
        {
            _inSession = false;
            _pending = Reason.None;
            _prio = Prio.None;
            _notBefore = 0f;
            _wantLogout = false;
            _guiDirty = false;
            _guiReason = Reason.None;
            _guiClosedAt = -1f;
            _wasInCombat = false;
            _wasTeleporting = false;
            _lastSaveTime = Time.time; // no fallback save right after spawning
        }

        private static bool IsDown(BepInEx.Configuration.KeyboardShortcut k)
        {
            if (k.MainKey == KeyCode.None) return false;
            if (!ZInput.GetKeyDown(k.MainKey, false)) return false;
            foreach (var mod in k.Modifiers)
                if (!ZInput.GetKey(mod, false)) return false;
            return true;
        }

        private static void Message(string text)
        {
            if (MessageHud.instance != null)
                MessageHud.instance.ShowMessage(MessageHud.MessageType.TopLeft, text);
        }
    }
}
