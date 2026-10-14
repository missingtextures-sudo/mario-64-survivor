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
                string? rom = FindRom();
                if (rom == null)
                {
                    Problem = "Your Super Mario 64 ROM wasn't found. Press Play in Melty so it can find your copy.";
                    return;
                }
                byte[] bytes = File.ReadAllBytes(rom);
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

        private static string? FindRom()
        {
            string props = Path.Combine(SettingsDir, "melty.properties");
            if (File.Exists(props))
            {
                foreach (string line in File.ReadAllLines(props))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0 && line.Substring(0, eq).Trim() == "rom")
                    {
                        string path = line.Substring(eq + 1).Trim();
                        if (File.Exists(path))
                        {
                            return path;
                        }
                        Plugin.Log.LogWarning($"melty.properties names a ROM that isn't there: {path}");
                    }
                }
            }
            string config = Plugin.RomPath.Value;
            return config.Length > 0 && File.Exists(config) ? config : null;
        }
    }
}
