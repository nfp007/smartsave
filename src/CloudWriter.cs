using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Steamworks;

namespace SmartSave
{
    /// <summary>When the Steam Cloud file work of a character save happens.</summary>
    public enum BackgroundWriteMode
    {
        Off,
        CloudOnly,
    }

    /// <summary>
    /// Background Steam Cloud write for the character file (BackgroundWrite = CloudOnly).
    ///
    /// The game builds the complete character file in the frame, exactly as always. Only the Steam Cloud file work
    /// is taken over: instead of FileHelpers.CloudFileWriteInChunks (writes .fch.new) and FileHelpers.ReplaceOldFile
    /// (three "moves", each a full copy, because Steam Cloud has no rename), the finished bytes are written with
    /// Steam's own asynchronous API (SteamRemoteStorage.FileWriteAsync / FileReadAsync). Steam does the disk work;
    /// the "done" callbacks arrive on the main thread through SteamAPI.RunCallbacks, which the game calls every frame.
    /// No own thread, no game objects touched off the main thread. The data arrays stay referenced until their write is
    /// confirmed (Unity's garbage collector does not move objects, so the memory Steam reads stays valid).
    ///
    /// Order, one step after another:  write .fch.new  →  copy the current .fch to .fch.old  →  write .fch  →  read .fch back
    /// and compare  →  delete .fch.new
    /// (vanilla: delete .old, move .fch → .old, move .new → .fch; there .fch is briefly missing, here never).
    ///
    /// Safety:
    ///   - one job at a time: a new save first waits for the running job (normally already done, saves are seconds apart)
    ///   - saves while the game shuts down / logs out are never taken over and wait for the running job first
    ///   - ZNet.ConsiderAutoBackup of the save is run after the job, so it never sees the old file
    ///   - any Steam error: the data is written as a local backup, like vanilla does, and the feature is off until restart
    /// </summary>
    internal static class CloudWriter
    {
        private sealed class Job
        {
            public string Fch, New, Old;
            public byte[] Data;
            public object[] BackupArgs;
            public readonly Stopwatch Sw = Stopwatch.StartNew();
            public double TNew, TRead, TOld, TFch, TVerify;
            public long Last;
        }

        private static bool _disabled;
        private static Job _job;            // running job (async steps in flight)
        private static string _capturedPath; // .fch.new path captured during the current save
        private static byte[] _captured;
        private static Job _queuedThisSave;

        private static CallResult<RemoteStorageFileWriteAsyncComplete_t> _writeResult;
        private static CallResult<RemoteStorageFileReadAsyncComplete_t> _readResult;
        private static Action<bool> _onWrite;
        private static Action<byte[]> _onRead;
        private static byte[] _readBuffer;

        private static readonly MethodInfo ConsiderAutoBackup = AccessTools.Method(typeof(ZNet), "ConsiderAutoBackup");
        private static readonly MethodInfo Mount = AccessTools.Method(typeof(FileHelpers), "Mount");
        private static readonly MethodInfo Unmount = AccessTools.Method(typeof(FileHelpers), "Unmount");

        /// <summary>True between the start and the end of a character save that is taken over.</summary>
        internal static bool Active { get; private set; }

        /// <summary>True while a deferred auto-backup check runs (our prefix must let it through).</summary>
        internal static bool Bypass { get; private set; }

        internal static bool Busy => _job != null;

        /// <summary>Milliseconds the current save had to wait for the previous background job (for the log).</summary>
        internal static double LastWaitMs { get; private set; }

        // ------------------------------------------------------------ save start / end (PlayerProfile.SavePlayerToDisk)

        internal static void OnSaveStart(PlayerProfile profile)
        {
            LastWaitMs = 0;
            Wait("a new save");
            _capturedPath = null;
            _captured = null;
            _queuedThisSave = null;
            Active = Eligible(profile);
        }

        internal static void OnSaveEnd()
        {
            Active = false;
            if (_captured != null && _queuedThisSave == null)
                Plugin.Log.LogWarning("background cloud write: the save stopped before the file was handed over; the game reported it");
            _capturedPath = null;
            _captured = null;
            _queuedThisSave = null;
        }

        private static bool Eligible(PlayerProfile profile)
        {
            if (_disabled || !Plugin.Enabled.Value || Plugin.BackgroundWrite.Value != BackgroundWriteMode.CloudOnly) return false;
            if (profile == null || !FileSourceHelper.IsCloud(profile.m_fileSource)) return false;
            var game = Game.instance;
            if (game == null || game.IsShuttingDown() || Player.m_localPlayer == null) return false;
            if (!SteamManager.Initialized && !SteamManager2.Initialized) return false;
            return true;
        }

        // ------------------------------------------------------------ taking over the two file steps

        /// <summary>Prefix of FileHelpers.CloudFileWriteInChunks: keep the bytes of .fch.new instead of writing them now.</summary>
        internal static bool TryCapture(string path, byte[] data)
        {
            if (!Active || path == null || data == null || data.Length == 0) return false;
            if (!path.EndsWith(".fch.new", StringComparison.OrdinalIgnoreCase)) return false;
            _capturedPath = path;
            _captured = (byte[])data.Clone();
            return true;
        }

        /// <summary>Prefix of FileHelpers.ReplaceOldFile: start the background job instead of the three copies.</summary>
        internal static bool TryReplace(string saveFile, string newFile, string oldFile)
        {
            if (!Active || _captured == null || !string.Equals(newFile, _capturedPath, StringComparison.Ordinal)) return false;
            var job = new Job { Fch = saveFile, New = newFile, Old = oldFile, Data = _captured };
            _captured = null;
            try
            {
                Start(job);
                _queuedThisSave = job;
                return true;
            }
            catch (Exception e)
            {
                // nothing written yet: hand the save back to the game in this same frame
                Disable($"could not start ({e.GetType().Name}: {e.Message})");
                _job = null;
                _captured = job.Data;
                WriteSync(job.New, job.Data);
                return false; // vanilla ReplaceOldFile runs now with the .new we just wrote
            }
        }

        /// <summary>Prefix of ZNet.ConsiderAutoBackup during a taken-over save: run it after the job instead.</summary>
        internal static bool TryDeferBackup(object[] args)
        {
            if (Bypass || !Active || _queuedThisSave == null) return false;
            _queuedThisSave.BackupArgs = (object[])args.Clone();
            return true;
        }

        // ------------------------------------------------------------ the job

        private static void EnsureCallResults()
        {
            if (_writeResult == null) _writeResult = CallResult<RemoteStorageFileWriteAsyncComplete_t>.Create(OnWriteComplete);
            if (_readResult == null) _readResult = CallResult<RemoteStorageFileReadAsyncComplete_t>.Create(OnReadComplete);
        }

        private static void Start(Job job)
        {
            EnsureCallResults();
            _job = job;
            job.Last = job.Sw.ElapsedTicks;
            WriteAsync(job.New, job.Data, ok =>
            {
                if (!ok) { Fail("writing " + job.New); return; }
                job.TNew = Lap(job);
                if (SteamRemoteStorage.FileExists(job.Fch)) ReadCurrentForOld(job);
                else WriteMain(job);
            });
        }

        private static void ReadCurrentForOld(Job job)
        {
            ReadAsync(job.Fch, buf =>
            {
                if (buf == null) { Fail("reading " + job.Fch); return; }
                job.TRead = Lap(job);
                if (buf.Length == 0) { WriteMain(job); return; }
                WriteAsync(job.Old, buf, ok =>
                {
                    if (!ok) { Fail("writing " + job.Old); return; }
                    job.TOld = Lap(job);
                    WriteMain(job);
                });
            });
        }

        private static void WriteMain(Job job)
        {
            WriteAsync(job.Fch, job.Data, ok =>
            {
                if (!ok) { Fail("writing " + job.Fch); return; }
                job.TFch = Lap(job);
                // read it back and compare before .new goes away: only then is the save confirmed
                ReadAsync(job.Fch, back =>
                {
                    if (back == null || !Same(back, job.Data)) { Fail("checking " + job.Fch + " (read-back differs)"); return; }
                    job.TVerify = Lap(job);
                    SteamRemoteStorage.FileDelete(job.New);
                    Done(job);
                });
            });
        }

        /// <summary>Read a whole file asynchronously. Calls back with the bytes, an empty array if it has no content, or null on error.</summary>
        private static void ReadAsync(string path, Action<byte[]> then)
        {
            int size = SteamRemoteStorage.GetFileSize(path);
            if (size <= 0) { then(new byte[0]); return; }
            _readBuffer = new byte[size];
            _onRead = then;
            var call = SteamRemoteStorage.FileReadAsync(path, 0, (uint)size);
            if (call == SteamAPICall_t.Invalid) { _onRead = null; _readBuffer = null; then(null); return; }
            _readResult.Set(call);
        }

        private static void OnReadComplete(RemoteStorageFileReadAsyncComplete_t r, bool ioFailure)
        {
            var then = _onRead;
            var buf = _readBuffer;
            _onRead = null;
            _readBuffer = null;
            if (then == null) return;
            if (ioFailure || r.m_eResult != EResult.k_EResultOK || buf == null ||
                !SteamRemoteStorage.FileReadAsyncComplete(r.m_hFileReadAsync, buf, r.m_cubRead))
            {
                then(null);
                return;
            }
            if (r.m_cubRead != buf.Length) Array.Resize(ref buf, (int)r.m_cubRead);
            then(buf);
        }

        private static void WriteAsync(string path, byte[] data, Action<bool> then)
        {
            _onWrite = then;
            var call = SteamRemoteStorage.FileWriteAsync(path, data, (uint)data.Length);
            if (call == SteamAPICall_t.Invalid) { _onWrite = null; then(false); return; }
            _writeResult.Set(call);
        }

        private static void OnWriteComplete(RemoteStorageFileWriteAsyncComplete_t r, bool ioFailure)
        {
            var then = _onWrite;
            _onWrite = null;
            then?.Invoke(!ioFailure && r.m_eResult == EResult.k_EResultOK);
        }

        private static void Done(Job job)
        {
            _job = null;
            SaveSystem.InvalidateCache();
            RunDeferredBackup(job);
            if (Plugin.LogSaves.Value)
                Plugin.Log.LogInfo($"background cloud write done in {job.Sw.Elapsed.TotalMilliseconds:F0} ms " +
                                   $"(.new {job.TNew:F0}, read {job.TRead:F0}, .old {job.TOld:F0}, .fch {job.TFch:F0}, check {job.TVerify:F0} ms; {job.Data.Length / 1024} KB, verified)");
        }

        private static void Fail(string what)
        {
            var job = _job;
            _job = null;
            _onWrite = null;
            _onRead = null;
            _readBuffer = null;
            Disable("Steam Cloud error while " + what);
            if (job == null) return;
            // like vanilla on a failed cloud save: keep the data as a local backup the player can restore
            try
            {
                string path = SaveSystem.GetCharacterFolderPath(FileHelpers.FileSource.Local) + Path.GetFileNameWithoutExtension(job.Fch) +
                              "_backup_cloud-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".fch";
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, job.Data);
                SaveSystem.InvalidateCache();
                ZLog.LogError($"Cloud save to location \"{job.Fch}\" failed! Saved as local backup \"{path}\". " +
                              "Use the \"Manage saves\" menu to restore this backup.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"background cloud write: could not write the local backup either: {e.Message}");
            }
        }

        private static void RunDeferredBackup(Job job)
        {
            if (job.BackupArgs == null || ConsiderAutoBackup == null) return;
            bool mounted = false;
            Bypass = true;
            try
            {
                mounted = Mount != null && (bool)Mount.Invoke(null, new[] { Enum.ToObject(Mount.GetParameters()[0].ParameterType, 3) });
                ConsiderAutoBackup.Invoke(null, job.BackupArgs);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"background cloud write: auto-backup check failed: {e.InnerException?.Message ?? e.Message}");
            }
            finally
            {
                Bypass = false;
                if (mounted && Unmount != null)
                    Unmount.Invoke(null, new[] { Enum.ToObject(Unmount.GetParameters()[0].ParameterType, 1) });
            }
        }

        // ------------------------------------------------------------ waiting (new save, shutdown)

        /// <summary>Block until the running job is finished: pumps Steam's callbacks. Normally returns at once.</summary>
        internal static void Wait(string why)
        {
            if (_job == null) return;
            var sw = Stopwatch.StartNew();
            while (_job != null && sw.ElapsedMilliseconds < 5000)
            {
                SteamAPI.RunCallbacks();
                if (_job != null) System.Threading.Thread.Sleep(1);
            }
            LastWaitMs = sw.Elapsed.TotalMilliseconds;
            if (_job != null)
            {
                Fail("waiting for the previous write (no answer from Steam in 5 s)");
                return;
            }
            if (Plugin.LogSaves.Value)
                Plugin.Log.LogInfo($"background cloud write: {why} waited {LastWaitMs:F0} ms for the previous write");
        }

        // ------------------------------------------------------------ helpers

        private static void WriteSync(string path, byte[] data)
        {
            try { SteamRemoteStorage.FileWrite(path, data, data.Length); }
            catch (Exception e) { Plugin.Log.LogError($"background cloud write: direct write failed: {e.Message}"); }
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static double Lap(Job job)
        {
            long now = job.Sw.ElapsedTicks;
            double ms = (now - job.Last) * 1000.0 / Stopwatch.Frequency;
            job.Last = now;
            return ms;
        }

        private static void Disable(string why)
        {
            if (_disabled) return;
            _disabled = true;
            Plugin.Log.LogWarning($"background cloud write switched off until restart, using the game's own cloud save: {why}");
        }
    }
}
