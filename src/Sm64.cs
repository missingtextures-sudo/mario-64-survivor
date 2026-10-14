using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace MarioRoR2
{
    /// <summary>
    /// The player's own Super Mario 64, loaded once: libsm64 from Melty's build folder and the ROM Melty readied
    /// (its path is the rom= line of %LOCALAPPDATA%\MarioRoR2\melty.properties). Nothing of SM64 ships with the mod.
    /// </summary>
    internal static class Sm64
    {
        public static bool Ready { get; private set; }
        public static string Problem { get; private set; } = "not started";
        /// <summary>Mario's textures from the ROM, 704 x 64 RGBA, rows in libsm64's order.</summary>
        public static Color32[] Atlas = Array.Empty<Color32>();

        private static GCHandle romHandle;

        public static string SettingsDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarioRoR2");

        public static void Init(string pluginDir)
        {
            try
            {
                string? dll = FindDll(pluginDir);
                if (dll == null)
                {
                    Problem = "libsm64 (sm64.dll) wasn't found. Melty builds it into BepInEx/MarioRoR2-native the first time you press Play.";
                    return;
                }
                if (!Native.Load(dll, out string err))
                {
                    Problem = err;
                    return;
                }
                byte[]? bytes = FindRom(out string rom);
                if (bytes == null)
                {
                    Problem = "Your Super Mario 64 (US) ROM wasn't found. Put it in Downloads, Desktop or Documents, or set RomPath in BepInEx/config/" + Plugin.Guid + ".cfg, then restart the game.";
                    return;
                }
                romHandle = GCHandle.Alloc(bytes, GCHandleType.Pinned); // libsm64's audio keeps reading the ROM
                // No sm64_register_debug_print_function: libsm64's audio prints several lines per note, every tick.
                byte[] tex = new byte[4 * Native.TextureWidth * Native.TextureHeight];
                Native.sm64_global_init(romHandle.AddrOfPinnedObject(), tex);
                Atlas = new Color32[Native.TextureWidth * Native.TextureHeight];
                for (int i = 0; i < Atlas.Length; i++)
                {
                    Atlas[i] = new Color32(tex[i * 4], tex[i * 4 + 1], tex[i * 4 + 2], tex[i * 4 + 3]);
                }
                Ready = true;
                Problem = "";
                Plugin.Log.LogInfo($"libsm64 ready: {dll}, ROM {rom} ({bytes.Length} bytes)");
                try
                {
                    Native.sm64_audio_init(romHandle.AddrOfPinnedObject());
                    MarioAudio.Start();
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("Mario's sound is off: " + e.Message);
                }
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or IOException or BadImageFormatException)
            {
                Problem = e.Message;
                Plugin.Log.LogError("libsm64 failed to start: " + e);
            }
        }

        private static string? FindDll(string pluginDir)
        {
            var candidates = new List<string>();
            string? env = Environment.GetEnvironmentVariable("MARIOROR2_NATIVE");
            if (!string.IsNullOrEmpty(env))
            {
                candidates.Add(Path.Combine(env, "sm64.dll"));
            }
            candidates.Add(Path.Combine(BepInEx.Paths.BepInExRootPath, "MarioRoR2-native", "sm64.dll"));
            candidates.Add(Path.Combine(pluginDir, "sm64.dll"));
            foreach (string c in candidates)
            {
                if (File.Exists(c))
                {
                    return c;
                }
            }
            // Melty's build may sit one folder down (e.g. dist/sm64.dll).
            string native = Path.Combine(BepInEx.Paths.BepInExRootPath, "MarioRoR2-native");
            if (Directory.Exists(native))
            {
                foreach (string f in Directory.GetFiles(native, "sm64.dll", SearchOption.AllDirectories))
                {
                    return f;
                }
            }
            Plugin.Log.LogWarning("sm64.dll not found in: " + string.Join("; ", candidates));
            return null;
        }

        private const int RomSize = 8 * 1024 * 1024;
        private const string UsSha1 = "9bef1128717f958171a4afac3ed78ee2bb4e86ce";
        private const FileAttributes CloudOnly = (FileAttributes)0x440000; // RECALL_ON_OPEN | RECALL_ON_DATA_ACCESS

        /// <summary>
        /// The player's SM64 (US) ROM, as big-endian (.z64) bytes. The mod looks for it itself, skipping any file it
        /// can't read: Melty's own ROM search stops Play on the first unreadable .z64 (e.g. a OneDrive cloud-only file).
        /// Tried in order: RomPath, an older Melty's melty.properties, the copies Melty remembers, then a search.
        /// </summary>
        private static byte[]? FindRom(out string found)
        {
            var named = new List<string> { Plugin.RomPath.Value };
            try
            {
                string props = Path.Combine(SettingsDir, "melty.properties");
                if (File.Exists(props))
                {
                    foreach (string line in File.ReadAllLines(props))
                    {
                        if (line.StartsWith("rom=")) named.Add(line.Substring(4).Trim());
                    }
                }
                string memory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Melty", "mods", "own-copies.json");
                if (File.Exists(memory))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(memory), @"""super-mario-64""\s*:\s*\[([^\]]*)\]");
                    foreach (System.Text.RegularExpressions.Match s in System.Text.RegularExpressions.Regex.Matches(m.Groups[1].Value, @"""((?:\\.|[^""\\])*)"""))
                    {
                        string p = System.Text.RegularExpressions.Regex.Unescape(s.Groups[1].Value);
                        named.AddRange(Directory.Exists(p) ? Directory.GetFiles(p) : new[] { p });
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Plugin.Log.LogWarning("ROM: couldn't read Melty's notes: " + e.Message);
            }
            foreach (string path in named)
            {
                byte[]? rom = path.Length > 0 ? TryRom(path, true) : null;
                if (rom != null) return Keep(found = path, rom);
            }
            foreach (string path in SearchRoms())
            {
                byte[]? rom = TryRom(path, false);
                if (rom != null) return Keep(found = path, rom);
            }
            found = "";
            return null;
        }

        private static byte[] Keep(string path, byte[] rom)
        {
            Plugin.RomPath.Value = path; // next start goes straight to it
            return rom;
        }

        private static byte[]? TryRom(string path, bool named)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length != RomSize || (!named && (fi.Attributes & CloudOnly) != 0))
                {
                    return null; // a cloud-only file would be downloaded just to check it
                }
                byte[] b = ToBigEndian(File.ReadAllBytes(path));
                using var sha = System.Security.Cryptography.SHA1.Create();
                return BitConverter.ToString(sha.ComputeHash(b)).Replace("-", "").ToLowerInvariant() == UsSha1 ? b : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
            {
                Plugin.Log.LogWarning($"ROM: skipped {path}: {e.Message}");
                return null;
            }
        }

        /// <summary>.v64 is byte-swapped, .n64 little-endian; libsm64 wants .z64 order.</summary>
        internal static byte[] ToBigEndian(byte[] b)
        {
            if (b.Length < 4) return b;
            if (b[0] == 0x37 && b[1] == 0x80)
            {
                for (int i = 0; i + 1 < b.Length; i += 2) (b[i], b[i + 1]) = (b[i + 1], b[i]);
            }
            else if (b[0] == 0x40 && b[1] == 0x12)
            {
                for (int i = 0; i + 3 < b.Length; i += 4) { (b[i], b[i + 3]) = (b[i + 3], b[i]); (b[i + 1], b[i + 2]) = (b[i + 2], b[i + 1]); }
            }
            return b;
        }

        /// <summary>ROM-sized .z64/.v64/.n64 files in the places Melty looks, for up to 10 s.</summary>
        private static IEnumerable<string> SearchRoms()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var roots = new List<(string, int)>
            {
                (Path.Combine(home, "Downloads"), 4), (Path.Combine(home, "Desktop"), 4), (Path.Combine(home, "OneDrive", "Desktop"), 4),
                (Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), 4), (Path.Combine(home, "Videos"), 2), (home, 3),
            };
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                if (d.DriveType == DriveType.Fixed) roots.Add((d.Name, 3));
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            foreach ((string root, int depth) in roots)
            {
                var queue = new Queue<(string, int)>();
                queue.Enqueue((root, 0));
                while (queue.Count > 0 && clock.ElapsedMilliseconds < 10000)
                {
                    (string dir, int level) = queue.Dequeue();
                    if (!seen.Add(dir)) continue;
                    string[] files, dirs;
                    try
                    {
                        files = Directory.GetFiles(dir);
                        dirs = level < depth ? Directory.GetDirectories(dir) : Array.Empty<string>();
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    {
                        continue;
                    }
                    foreach (string f in files)
                    {
                        string ext = Path.GetExtension(f).ToLowerInvariant();
                        if (ext == ".z64" || ext == ".v64" || ext == ".n64") yield return f;
                    }
                    foreach (string sub in dirs)
                    {
                        string name = Path.GetFileName(sub);
                        FileAttributes a;
                        try { a = File.GetAttributes(sub); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                        if ((a & FileAttributes.ReparsePoint) == 0 && name != "Windows" && name != "AppData" && !name.StartsWith("$")) queue.Enqueue((sub, level + 1));
                    }
                }
            }
        }

        /// <summary>The byte-order fix turns a .v64 or .n64 header into the .z64 one.</summary>
        public static bool SelfCheck(out string why)
        {
            byte[] z = { 0x80, 0x37, 0x12, 0x40 };
            why = "";
            if (!System.Linq.Enumerable.SequenceEqual(ToBigEndian(new byte[] { 0x37, 0x80, 0x40, 0x12 }), z)) why = ".v64 swap is wrong";
            else if (!System.Linq.Enumerable.SequenceEqual(ToBigEndian(new byte[] { 0x40, 0x12, 0x37, 0x80 }), z)) why = ".n64 swap is wrong";
            return why.Length == 0;
        }
    }
}
