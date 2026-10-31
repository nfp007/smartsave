using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace SmartSave
{
    /// <summary>
    /// A faster Minimap.GetMapData with the same output, plus a cache of the last result.
    ///
    /// Vanilla writes the two explored grids (2048x2048 each) one bool at a time into a ZPackage (~8.4 million
    /// calls), then compresses the ~8.4 MB payload, on every save, even if nothing changed.
    ///
    ///   Fast build:  the bits are copied as 32-bit words and expanded into one byte buffer.
    ///   Cache:       a snapshot (both grids as words + the pins part) is compared exactly with the snapshot of the
    ///                last result. Unchanged → the last compressed result is reused (~1-2 ms instead of ~50 ms).
    ///   Prewarm:     every MapPrewarmSec, if the map changed, the snapshot is taken on the main thread and the
    ///                payload is built and compressed on a background thread, so the next save is a cache hit.
    ///                Only plain byte arrays cross threads; nothing of the game is touched off the main thread.
    ///   Self-check:  once per game start, when the map is loaded on joining a world (Minimap.LoadMapData), the
    ///                game's own GetMapData runs and its decompressed content is compared byte by byte with ours.
    ///                Identical → fast path on. Different or any error → off until restart, with a log warning.
    ///                If the join check didn't run, the first save does the same check.
    /// </summary>
    internal static class FastMapData
    {
        private enum State { Unverified, Verified, Disabled }

        private const int MapVersion = 8;

        private static State _state = State.Unverified;

        private static readonly AccessTools.FieldRef<Minimap, BitArray> Explored =
            AccessTools.FieldRefAccess<Minimap, BitArray>("m_explored");
        private static readonly AccessTools.FieldRef<Minimap, BitArray> ExploredOthers =
            AccessTools.FieldRefAccess<Minimap, BitArray>("m_exploredOthers");
        private static readonly AccessTools.FieldRef<Minimap, List<Minimap.PinData>> Pins =
            AccessTools.FieldRefAccess<Minimap, List<Minimap.PinData>>("m_pins");

        /// <summary>Everything GetMapData's output depends on, as plain data.</summary>
        private sealed class Snapshot
        {
            public int Size, Len;
            public int[] A, B;
            public byte[] Tail;

            public bool SameAs(Snapshot o) =>
                o != null && Size == o.Size && Len == o.Len && Same(A, o.A) && Same(B, o.B) && Same(Tail, o.Tail);
        }

        private static readonly object Lock = new object();
        private static Snapshot _cachedSnap;
        private static byte[] _cachedResult;

        private static volatile bool _workerBusy;
        private static float _nextPrewarm;

        /// <summary>True while the game's own GetMapData runs for the self-check (our prefix must not interfere).</summary>
        internal static bool Bypass { get; private set; }

        /// <summary>How the last map data was produced: "cache", "fast", "game" (for the log and the profiler).</summary>
        internal static string LastPath { get; set; } = "";

        internal static bool Enabled => Plugin.FastMapSave.Value && _state != State.Disabled;
        internal static bool Verified => _state == State.Verified;

        // ------------------------------------------------------------ GetMapData replacement

        /// <summary>Map data from the cache or a fast build. Returns null (and switches off) on any error.</summary>
        internal static byte[] TryGet(Minimap minimap)
        {
            try
            {
                var snap = TakeSnapshot(minimap);
                lock (Lock)
                {
                    if (_cachedResult != null && snap.SameAs(_cachedSnap))
                    {
                        LastPath = "cache";
                        return (byte[])_cachedResult.Clone();
                    }
                }
                var result = Wrap(Utils.Compress(Payload(snap)));
                Store(snap, result);
                LastPath = "fast";
                ZLog.Log("Minimap: compressed mapData (SmartSave fast build) => " + result.Length + " bytes");
                return (byte[])result.Clone();
            }
            catch (Exception e)
            {
                Disable($"error: {e.GetType().Name}: {e.Message}");
                return null;
            }
        }

        /// <summary>Self-check at the first save, if the join check didn't run: compare our payload with the game's result.</summary>
        internal static void VerifyAtSave(Minimap minimap, byte[] vanilla)
        {
            if (_state != State.Unverified) return;
            try
            {
                var snap = TakeSnapshot(minimap);
                Check(snap, vanilla, "first save");
            }
            catch (Exception e)
            {
                Disable($"check failed: {e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>Self-check on joining a world (postfix of Minimap.LoadMapData), usually behind the loading screen.</summary>
        internal static void VerifyAtJoin(Minimap minimap)
        {
            if (_state != State.Unverified || !Plugin.FastMapSave.Value) return;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var snap = TakeSnapshot(minimap);
                byte[] vanilla;
                Bypass = true;
                try { vanilla = (byte[])AccessTools.Method(typeof(Minimap), "GetMapData").Invoke(minimap, null); }
                finally { Bypass = false; }
                Check(snap, vanilla, $"joining the world, {sw.ElapsedMilliseconds} ms");
            }
            catch (Exception e)
            {
                // too early or something missing: not a reason to give up, the first save does the check instead
                Plugin.Log.LogWarning($"fast map save check on joining failed ({e.GetType().Name}: {e.Message}), checking at the first save instead");
            }
        }

        private static void Check(Snapshot snap, byte[] vanilla, string when)
        {
            if (vanilla == null) { Disable("the game returned no map data"); return; }
            var pkg = new ZPackage(vanilla);
            int version = pkg.ReadInt();
            var inner = pkg.ReadCompressedPackage().GetArray();
            if (version == MapVersion && Same(inner, Payload(snap)))
            {
                _state = State.Verified;
                Store(snap, (byte[])vanilla.Clone()); // the game's own result seeds the cache
                Plugin.Log.LogInfo($"fast map save checked ({when}): same result as the game, now in use");
            }
            else
            {
                Disable($"result differs from the game's (map version {version}; another mod may change the map data)");
            }
        }

        // ------------------------------------------------------------ prewarm (called from Plugin.Update)

        internal static void Tick()
        {
            float interval = Plugin.MapPrewarmSec.Value;
            if (interval <= 0f || !Enabled || !Verified || _workerBusy) return;
            if (Time.realtimeSinceStartup < _nextPrewarm) return;
            _nextPrewarm = Time.realtimeSinceStartup + interval;

            var minimap = Minimap.instance;
            if (minimap == null || Player.m_localPlayer == null || ZNet.instance == null) return;
            try
            {
                var snap = TakeSnapshot(minimap);
                lock (Lock)
                    if (_cachedResult != null && snap.SameAs(_cachedSnap)) return; // nothing changed

                _workerBusy = true;
                new Thread(() =>
                {
                    try { Store(snap, Wrap(Utils.Compress(Payload(snap)))); }
                    catch { /* the next save simply builds it itself */ }
                    finally { _workerBusy = false; }
                })
                {
                    IsBackground = true,
                    Name = "SmartSave map prewarm",
                    Priority = System.Threading.ThreadPriority.BelowNormal,
                }.Start();
            }
            catch (Exception e)
            {
                _workerBusy = false;
                Plugin.Log.LogWarning($"map prewarm skipped: {e.Message}");
            }
        }

        // ------------------------------------------------------------ building blocks

        private static Snapshot TakeSnapshot(Minimap minimap)
        {
            var explored = Explored(minimap);
            var others = ExploredOthers(minimap);
            int n = explored.Length;
            if (others == null || others.Length < n) throw new InvalidOperationException("explored grids differ in size");
            return new Snapshot
            {
                Size = minimap.m_textureSize,
                Len = n,
                A = Words(explored, n),
                B = Words(others, n), // vanilla writes m_exploredOthers only up to m_explored.Length
                Tail = PinsPart(minimap),
            };
        }

        /// <summary>The bits as 32-bit words, only the first n bits kept.</summary>
        private static int[] Words(BitArray bits, int n)
        {
            var all = new int[(bits.Length + 31) / 32];
            bits.CopyTo(all, 0);
            var w = new int[(n + 31) / 32];
            Array.Copy(all, w, w.Length);
            int rest = n & 31;
            if (rest != 0) w[w.Length - 1] &= (int)((1u << rest) - 1);
            return w;
        }

        /// <summary>The part of the payload after the two grids: pins and the public-position flag, written like vanilla.</summary>
        private static byte[] PinsPart(Minimap minimap)
        {
            var pkg = new ZPackage();
            var pins = Pins(minimap);
            int count = 0;
            foreach (var pin in pins)
                if (pin.m_save) count++;
            pkg.Write(count);
            foreach (var pin in pins)
            {
                if (!pin.m_save) continue;
                pkg.Write(pin.m_name);
                pkg.Write(pin.m_pos);
                pkg.Write((int)pin.m_type);
                pkg.Write(pin.m_checked);
                pkg.Write(pin.m_ownerID);
                pkg.Write(pin.m_author.ToString());
            }
            pkg.Write(ZNet.instance.IsReferencePositionPublic());
            return pkg.GetArray();
        }

        /// <summary>The uncompressed inner package: int size, one byte (0/1) per bit for both grids, then the pins part.</summary>
        private static byte[] Payload(Snapshot s)
        {
            var o = new byte[4 + 2 * s.Len + s.Tail.Length];
            o[0] = (byte)s.Size; o[1] = (byte)(s.Size >> 8); o[2] = (byte)(s.Size >> 16); o[3] = (byte)(s.Size >> 24);
            int k = 4;
            Expand(s.A, s.Len, o, ref k);
            Expand(s.B, s.Len, o, ref k);
            Buffer.BlockCopy(s.Tail, 0, o, k, s.Tail.Length);
            return o;
        }

        private static void Expand(int[] words, int n, byte[] dst, ref int k)
        {
            for (int i = 0; i < n; i++)
                dst[k++] = (byte)((words[i >> 5] >> (i & 31)) & 1);
        }

        /// <summary>The outer package, as ZPackage.Write(8) + ZPackage.WriteCompressed: int version, int length, bytes.</summary>
        private static byte[] Wrap(byte[] compressed)
        {
            using (var ms = new MemoryStream(compressed.Length + 8))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(MapVersion);
                w.Write(compressed.Length);
                w.Write(compressed);
                w.Flush();
                return ms.ToArray();
            }
        }

        private static void Store(Snapshot snap, byte[] result)
        {
            lock (Lock)
            {
                _cachedSnap = snap;
                _cachedResult = result;
            }
        }

        private static bool Same(int[] a, int[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static void Disable(string why)
        {
            _state = State.Disabled;
            lock (Lock) { _cachedSnap = null; _cachedResult = null; }
            Plugin.Log.LogWarning($"fast map save switched off, using the game's own map save: {why}");
        }
    }
}
