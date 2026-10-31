using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace SmartSave
{
    /// <summary>
    /// Measures where the time goes during every character save (SmartSave's, the game's timer, the server push).
    /// Only active with [Debug] Profiling = true; the patches are applied at startup, so changing it needs a restart.
    /// Output: one line per save in the BepInEx log and one row in BepInEx/SmartSave_profile.csv.
    /// </summary>
    internal static class SaveProfiler
    {
        /// <summary>Methods measured inside a save, in the order they run: label, type, method (all overloads).</summary>
        private static readonly (string label, string type, string method)[] Targets =
        {
            ("loadHintPush", "SystemResourceManager", "PushIsInLoadingScreen"),
            ("cloudMount", "FileHelpers", "Mount"),
            ("playerData", "PlayerProfile", "SavePlayerData"),
            ("mapTotal", "Minimap", "SaveMapData"),
            ("mapBuild", "Minimap", "GetMapData"),
            ("compress", "ZPackage", "WriteCompressed"),
            ("toDiskTotal", "PlayerProfile", "SavePlayerToDisk"),
            ("cloudChecks", "SaveSystem", "PreSaveCloudChecksAndOperations"),
            ("hash", "ZPackage", "GenerateHash"),
            ("writeFile", "FileWriter", "Finish"),
            ("replaceFile", "FileHelpers", "ReplaceOldFile"),
            ("backupCheck", "ZNet", "ConsiderAutoBackup"),
            ("cacheInvalidate", "SaveSystem", "InvalidateCache"),
            ("cloudUnmount", "FileHelpers", "Unmount"),
            ("loadHintPop", "SystemResourceManager", "PopIsInLoadingScreen"),
        };

        private static readonly string[] Columns = Array.ConvertAll(Targets, t => t.label);

        private static readonly Dictionary<MethodBase, string> Labels = new Dictionary<MethodBase, string>();
        private static readonly Dictionary<string, double> Parts = new Dictionary<string, double>();
        private static readonly double TickMs = 1000.0 / Stopwatch.Frequency;

        private static int _depth;
        private static long _start;
        private static string _source;
        private static bool _logoutPoint;
        private static int _gc0, _gc1, _gc2;
        private static long _memBefore;
        private static int _saveNo;

        // frame times around the save
        private static float _avgFrameMs = 16f;
        private static string _pendingLine, _pendingRow;
        private static readonly List<float> _framesAfter = new List<float>();
        private const int FramesToWatch = 3;

        internal static bool Active { get; private set; }

        internal static void Init(Harmony harmony)
        {
            if (!Plugin.Profiling.Value) return;
            // First/last, so the timing wraps other mods' patches and still runs when a prefix skips the original.
            var prefix = new HarmonyMethod(AccessTools.Method(typeof(SaveProfiler), nameof(PartPrefix))) { priority = Priority.First };
            var postfix = new HarmonyMethod(AccessTools.Method(typeof(SaveProfiler), nameof(PartPostfix))) { priority = Priority.Last };
            int ok = 0;
            foreach (var (label, typeName, methodName) in Targets)
            {
                var type = AccessTools.TypeByName(typeName);
                if (type == null) { Plugin.Log.LogWarning($"profiler: type {typeName} not found"); continue; }
                foreach (var m in AccessTools.GetDeclaredMethods(type))
                {
                    if (m.Name != methodName || m.IsAbstract || m.IsGenericMethodDefinition) continue;
                    try
                    {
                        harmony.Patch(m, prefix: prefix, postfix: postfix);
                        Labels[m] = label;
                        ok++;
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning($"profiler: could not patch {typeName}.{methodName}: {e.Message}");
                    }
                }
            }

            var save = AccessTools.Method(typeof(Game), nameof(Game.SavePlayerProfile));
            harmony.Patch(save,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(SaveProfiler), nameof(SavePrefix))) { priority = Priority.First },
                finalizer: new HarmonyMethod(AccessTools.Method(typeof(SaveProfiler), nameof(SaveFinalizer))) { priority = Priority.Last });

            Active = true;
            Plugin.Log.LogInfo($"profiler on: {ok} methods measured, CSV: {CsvPath}");
        }

        private static string CsvPath => Path.Combine(Paths.BepInExRootPath, "SmartSave_profile.csv");

        // ------------------------------------------------------------ the whole save

        private static void SavePrefix(bool setLogoutPoint, bool isFromRpc)
        {
            if (_depth++ > 0) return;
            FlushPending(); // a previous save that is still waiting for its frame times
            Parts.Clear();
            Running.Clear();
            _logoutPoint = setLogoutPoint;
            _source = SaveScheduler.SavingNow ? "SmartSave:" + SaveScheduler.ReasonText(SaveScheduler.CurrentReason)
                : isFromRpc ? "game:serverPush"
                : "game";
            _gc0 = GC.CollectionCount(0);
            _gc1 = GC.CollectionCount(1);
            _gc2 = GC.CollectionCount(2);
            _memBefore = GC.GetTotalMemory(false);
            _start = Stopwatch.GetTimestamp();
        }

        private static Exception SaveFinalizer(Exception __exception)
        {
            if (--_depth > 0) return __exception;
            double total = (Stopwatch.GetTimestamp() - _start) * TickMs;
            long allocKb = (GC.GetTotalMemory(false) - _memBefore) / 1024;
            int gc0 = GC.CollectionCount(0) - _gc0, gc1 = GC.CollectionCount(1) - _gc1, gc2 = GC.CollectionCount(2) - _gc2;
            _saveNo++;

            string storage = "?", sizeKb = "";
            try
            {
                var profile = Game.instance?.GetPlayerProfile();
                if (profile != null)
                {
                    storage = profile.m_fileSource.ToString();
                    var path = profile.GetPath();
                    if (!string.IsNullOrEmpty(path) && File.Exists(path)) sizeKb = (new FileInfo(path).Length / 1024).ToString();
                }
            }
            catch { /* only informative */ }

            // how the map was handled: skipped save, map not written, or game / fast / cache
            if (SaveScheduler.SkippedNow) _source += " (skipped)";
            else _source += Parts.ContainsKey("mapBuild") ? " map=" + FastMapData.LastPath : " map=none";

            var line = new StringBuilder();
            line.Append($"[profile #{_saveNo}] {_source} logoutPoint={_logoutPoint} total={total:F0} ms |");
            foreach (var c in Columns)
                if (Parts.TryGetValue(c, out var ms)) line.Append($" {c}={ms:F1}");
            line.Append($" | storage={storage} file={(sizeKb == "" ? "?" : sizeKb + " KB")} alloc={allocKb} KB gc={gc0}/{gc1}/{gc2}");
            if (__exception != null) line.Append($" | EXCEPTION {__exception.GetType().Name}");

            // CSV numbers always with a dot, whatever the PC's language is
            var row = new StringBuilder();
            row.Append(FormattableString.Invariant($"{DateTime.Now:yyyy-MM-dd HH:mm:ss},{_saveNo},{_source},{_logoutPoint},{total:F1}"));
            foreach (var c in Columns)
                row.Append(',').Append(Parts.TryGetValue(c, out var ms) ? ms.ToString("F1", CultureInfo.InvariantCulture) : "");
            row.Append(FormattableString.Invariant($",{storage},{sizeKb},{allocKb},{gc0},{gc1},{gc2},{_avgFrameMs:F1}"));

            _pendingLine = line.ToString();
            _pendingRow = row.ToString();
            _framesAfter.Clear();
            return __exception;
        }

        // ------------------------------------------------------------ parts

        // An overload may call another overload of the same method: only the outermost call is timed.
        private static readonly Dictionary<string, int> Running = new Dictionary<string, int>();

        private static void PartPrefix(MethodBase __originalMethod, out long __state)
        {
            __state = 0;
            if (_depth == 0 || !Labels.TryGetValue(__originalMethod, out var label)) return;
            Running.TryGetValue(label, out int n);
            Running[label] = n + 1;
            if (n == 0) __state = Stopwatch.GetTimestamp();
        }

        private static void PartPostfix(MethodBase __originalMethod, long __state)
        {
            if (_depth == 0 || !Labels.TryGetValue(__originalMethod, out var label)) return;
            if (Running.TryGetValue(label, out int n) && n > 0) Running[label] = n - 1;
            if (__state == 0) return;
            double ms = (Stopwatch.GetTimestamp() - __state) * TickMs;
            Parts[label] = Parts.TryGetValue(label, out var sum) ? sum + ms : ms;
        }

        // ------------------------------------------------------------ frame times (called from Plugin.Update)

        internal static void Tick()
        {
            if (!Active) return;
            float ms = Time.unscaledDeltaTime * 1000f;
            if (_pendingLine == null)
            {
                // smooth normal frame time, ignoring spikes
                if (ms < _avgFrameMs * 3f || _avgFrameMs <= 0f) _avgFrameMs = _avgFrameMs * 0.95f + ms * 0.05f;
                return;
            }
            _framesAfter.Add(ms);
            if (_framesAfter.Count >= FramesToWatch) FlushPending();
        }

        private static void FlushPending()
        {
            if (_pendingLine == null) return;
            string frames = string.Join(" ", _framesAfter.ConvertAll(f => f.ToString("F0")));
            Plugin.Log.LogInfo($"{_pendingLine} | frames after: {frames} ms (normal {_avgFrameMs:F0} ms)");
            try
            {
                bool header = !File.Exists(CsvPath);
                using (var w = new StreamWriter(CsvPath, true))
                {
                    if (header)
                        w.WriteLine("time,no,source,logoutPoint,totalMs," + string.Join(",", Columns) +
                                    ",storage,fileKb,allocKb,gc0,gc1,gc2,normalFrameMs,frameAfter1,frameAfter2,frameAfter3");
                    var f = _framesAfter.ConvertAll(x => x.ToString("F1", CultureInfo.InvariantCulture));
                    while (f.Count < FramesToWatch) f.Add("");
                    w.WriteLine(_pendingRow + "," + string.Join(",", f));
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"profiler: could not write CSV: {e.Message}");
            }
            _pendingLine = null;
            _pendingRow = null;
            _framesAfter.Clear();
        }
    }
}
