using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;

namespace SmartSave
{
    /// <summary>
    /// A faster Minimap.GetMapData with the same output.
    ///
    /// Vanilla writes the two explored grids (2048x2048 each) one bool at a time into a ZPackage (~8.5 million
    /// calls, a growing MemoryStream, ~40 MB of garbage). Here the bits are expanded into one byte buffer and
    /// written in a single call into a pre-sized stream. The rest (pins, compression) is the same code as vanilla.
    ///
    /// Safety: the first time per game start, both versions run and their decompressed content is compared.
    /// Only if it is identical is the fast version used from then on; otherwise it is switched off for good.
    /// </summary>
    internal static class FastMapData
    {
        private enum State { Unverified, Verified, Disabled }

        private static State _state = State.Unverified;

        private static readonly AccessTools.FieldRef<Minimap, BitArray> Explored =
            AccessTools.FieldRefAccess<Minimap, BitArray>("m_explored");
        private static readonly AccessTools.FieldRef<Minimap, BitArray> ExploredOthers =
            AccessTools.FieldRefAccess<Minimap, BitArray>("m_exploredOthers");
        private static readonly AccessTools.FieldRef<Minimap, List<Minimap.PinData>> Pins =
            AccessTools.FieldRefAccess<Minimap, List<Minimap.PinData>>("m_pins");
        private static readonly AccessTools.FieldRef<ZPackage, BinaryWriter> Writer =
            AccessTools.FieldRefAccess<ZPackage, BinaryWriter>("m_writer");
        private static readonly AccessTools.FieldRef<ZPackage, MemoryStream> Stream =
            AccessTools.FieldRefAccess<ZPackage, MemoryStream>("m_stream");

        // reused between saves
        private static byte[] _bytes;
        private static int[] _ints;

        internal static bool Enabled => Plugin.FastMapSave.Value && _state != State.Disabled;
        internal static bool Verified => _state == State.Verified;

        /// <summary>Build the map data like Minimap.GetMapData. Returns null (and switches off) on any error.</summary>
        internal static byte[] TryBuild(Minimap minimap)
        {
            try
            {
                return Build(minimap);
            }
            catch (Exception e)
            {
                Disable($"error: {e.GetType().Name}: {e.Message}");
                return null;
            }
        }

        private static byte[] Build(Minimap minimap)
        {
            var explored = Explored(minimap);
            var others = ExploredOthers(minimap);
            int n = explored.Length;
            if (others == null || others.Length < n) throw new InvalidOperationException("explored grids differ in size");

            var outer = new ZPackage();
            outer.Write(8); // map data version, as vanilla

            var inner = new ZPackage();
            var stream = Stream(inner);
            if (stream.Capacity < 2 * n + 64 * 1024) stream.Capacity = 2 * n + 64 * 1024;

            inner.Write(minimap.m_textureSize);
            var writer = Writer(inner);
            writer.Write(BitsToBytes(explored, n), 0, n);
            writer.Write(BitsToBytes(others, n), 0, n); // vanilla also loops to m_explored.Length here

            var pins = Pins(minimap);
            int count = 0;
            foreach (var pin in pins)
                if (pin.m_save) count++;
            inner.Write(count);
            foreach (var pin in pins)
            {
                if (!pin.m_save) continue;
                inner.Write(pin.m_name);
                inner.Write(pin.m_pos);
                inner.Write((int)pin.m_type);
                inner.Write(pin.m_checked);
                inner.Write(pin.m_ownerID);
                inner.Write(pin.m_author.ToString());
            }
            inner.Write(ZNet.instance.IsReferencePositionPublic());

            int size = inner.Size();
            outer.WriteCompressed(inner);
            ZLog.Log("Minimap: compressed mapData " + size + " => " + outer.Size() + " bytes");
            return outer.GetArray();
        }

        /// <summary>One byte (0 or 1) per bit, exactly what BinaryWriter.Write(bool) writes.</summary>
        private static byte[] BitsToBytes(BitArray bits, int n)
        {
            if (_bytes == null || _bytes.Length < n) _bytes = new byte[n];
            int words = (n + 31) / 32;
            if (_ints == null || _ints.Length < words) _ints = new int[words];
            bits.CopyTo(_ints, 0);
            var dst = _bytes;
            var src = _ints;
            for (int i = 0; i < n; i++)
                dst[i] = (byte)((src[i >> 5] >> (i & 31)) & 1);
            return dst;
        }

        /// <summary>Compare our result with vanilla's (decompressed content, so compressor details don't matter).</summary>
        internal static void Verify(byte[] fast, byte[] vanilla)
        {
            if (_state != State.Unverified) return;
            try
            {
                if (vanilla == null) { Disable("the game returned no map data"); return; }
                if (SameContent(fast, vanilla))
                {
                    _state = State.Verified;
                    Plugin.Log.LogInfo("fast map save checked: same result as the game, now in use");
                }
                else
                {
                    Disable("result differs from the game's (another mod may change the map data)");
                }
            }
            catch (Exception e)
            {
                Disable($"check failed: {e.GetType().Name}: {e.Message}");
            }
        }

        private static bool SameContent(byte[] a, byte[] b)
        {
            var pa = new ZPackage(a);
            var pb = new ZPackage(b);
            if (pa.ReadInt() != pb.ReadInt()) return false;
            var ia = pa.ReadCompressedPackage().GetArray();
            var ib = pb.ReadCompressedPackage().GetArray();
            if (ia.Length != ib.Length) return false;
            for (int i = 0; i < ia.Length; i++)
                if (ia[i] != ib[i]) return false;
            return true;
        }

        private static void Disable(string why)
        {
            _state = State.Disabled;
            Plugin.Log.LogWarning($"fast map save switched off, using the game's own map save: {why}");
        }
    }
}
