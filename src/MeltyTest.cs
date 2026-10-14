using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace MarioRoR2
{
    /// <summary>
    /// Melty's on-PC release test. While %LOCALAPPDATA%\Melty\test\&lt;slug&gt;.json exists, the mod plays its own
    /// run as Mario (DevHarness runs Script) and appends "pass name" / "fail name: why" per check to the file
    /// Melty names. It never quits the game; Melty closes it. Players never have that file.
    /// </summary>
    internal sealed class MeltyTest
    {
        public const string Slug = "mario-64-survivor";
        private readonly string checksPath;
        public readonly string TestFile;
        /// <summary>When to leave (UTC): 12 s after the run's time is up, between Melty's end-of-run checks and its force-kill.</summary>
        public readonly DateTime LeaveAt = DateTime.MaxValue;
        private readonly System.Collections.Generic.HashSet<string> reported = new System.Collections.Generic.HashSet<string>();

        private MeltyTest(string checksPath, string testFile, DateTime leaveAt)
        {
            this.checksPath = checksPath;
            TestFile = testFile;
            LeaveAt = leaveAt;
        }

        /// <summary>Every check has been reported: the run's own work is done.</summary>
        public bool AllReported => reported.Count >= 8;

        public static readonly string[] Script =
        {
            "waitscene title 240", "sleep 3", "host", "waitscene lobby 90", "sleep 3", "pickmario", "sleep 2", "startrun", "sleep 15",
            "exitpod", "sleep 3", "exitpod", "waitmario 120", "nomonsters", "sleep 4",
            "check rom-loaded", "check mario-survivor", "check mario-spawned", "check level-collision",
            // jump first, where the drop pod landed: it fell from open sky, so nothing overhead
            "waitidle 4", "mark", "status", "press A 0.3", "sleep 1.2", "waitidle 3", "press A 0.3", "sleep 1.2", "check mario-jumps",
            "waitidle 4", "mark", "move 0 2.5", "sleep 3", "check mario-moves", "shot run",
            // the Lemurian walks up to Mario (on a slope it can land well below him); punch once it is in reach
            "nomonsters", "waitidle 8", "spawn LemurianMaster 2", "waitnear 2.2 10", "facetarget", "press B 0.15", "sleep 0.35", "press B 0.15", "sleep 0.35", "press B 0.15", "sleep 1.2",
            "waitidle 4", "waitnear 2.2 8", "facetarget", "press B 0.15", "sleep 0.35", "press B 0.15", "sleep 0.35", "press B 0.15", "sleep 1.2",
            "check mario-punches", "check mario-sounds", "shot punch", "status",
            "log melty test: all checks reported",
        };

        public static MeltyTest? TryLoad()
        {
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Melty", "test", Slug + ".json");
                if (!File.Exists(path))
                {
                    return null;
                }
                string json = File.ReadAllText(path);
                Match m = Regex.Match(json, "\"checks\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
                if (!m.Success)
                {
                    Plugin.Log.LogWarning("melty test: no checks path in " + path);
                    return null;
                }
                string checks = Regex.Unescape(m.Groups[1].Value);
                Plugin.Log.LogInfo("melty test: checks go to " + checks);
                // "until" is launch + 3 min to appear + the run's minutes. Melty checks the game responds just after the
                // run's time, then force-kills it ~15 s later; a game still dying then blocks the next run, so leave first.
                DateTime leaveAt = DateTime.MaxValue;
                Match u = Regex.Match(json, @"""until""\s*:\s*""([^""]+)""");
                if (u.Success && DateTime.TryParse(u.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime until))
                {
                    leaveAt = until.AddSeconds(-180 + 12);
                    Plugin.Log.LogInfo($"melty test: run ends at {until.AddSeconds(-180):HH:mm:ss}Z; leaving at {leaveAt:HH:mm:ss}Z once every check is in");
                }
                return new MeltyTest(checks, path, leaveAt);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Plugin.Log.LogWarning("melty test: couldn't read the test file: " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// Test runs only: when Melty closes the game, end the process at once. Risk of Rain 2 itself crashes late in a
        /// polite shutdown on some PCs (ntdll+0x1ad67, seen here with other mods before this one), which would fail the
        /// run's crash check after its own checks passed. Players' games quit normally.
        /// </summary>
        public static void EndOnQuit()
        {
            // wantsToQuit comes first, before RoR2's own ~5 s shutdown (Wwise, profile save): Melty counts a game that
            // takes that long to close as still running and never starts the next run.
            Application.wantsToQuit += () =>
            {
                ExitNow("the game is closing");
                return true;
            };
        }

        /// <summary>Ends this test game at once (see EndOnQuit), logging why.</summary>
        public static void ExitNow(string why)
        {
            MarioAudio.Stop();
            Plugin.Log.LogInfo($"melty test: {why} at {DateTime.Now:HH:mm:ss.fff}; ending the game now");
            foreach (BepInEx.Logging.ILogListener l in BepInEx.Logging.Logger.Listeners)
            {
                (l as BepInEx.Logging.DiskLogListener)?.LogWriter?.Flush();
            }
            System.Diagnostics.Process.GetCurrentProcess().Kill();
        }

        public void Report(string name, bool pass, string why)
        {
            reported.Add(name);
            try
            {
                File.AppendAllText(checksPath, (pass ? $"pass {name}" : $"fail {name}: {why}") + Environment.NewLine);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Plugin.Log.LogWarning($"melty test: couldn't write {checksPath}: {e.Message}");
            }
        }

        /// <summary>A check, judged on the game's state now.</summary>
        public static (bool, string) Judge(string name, MarioController? mc)
        {
            switch (name)
            {
                case "rom-loaded":
                    return (Sm64.Ready, Sm64.Problem);
                case "mario-survivor":
                    return (mc != null && mc.Body.name.StartsWith("MarioBody"), "the local body isn't Mario");
                case "mario-spawned":
                    return (mc != null && mc.MarioId >= 0 && mc.Renderer != null && mc.Renderer.Triangles > 100, "no libsm64 Mario drawn");
                case "level-collision":
                    return (mc != null && LevelCollision.LoadedSurfaces > 0 && (mc.State.action & (1u << 11)) == 0,
                        $"{LevelCollision.LoadedSurfaces} surfaces loaded, action 0x{mc?.State.action:X8}");
                case "mario-moves":
                {
                    if (mc == null) return (false, "no Mario");
                    Vector3 d = MarioMath.ToUnity(mc.State) - mc.MarkPosition;
                    d.y = 0;
                    return (d.magnitude > 2f, $"moved {d.magnitude:0.0} m");
                }
                case "mario-jumps":
                    return (mc != null && mc.MaxYSinceMark - mc.MarkPosition.y > 0.8f, $"rose {(mc == null ? 0 : mc.MaxYSinceMark - mc.MarkPosition.y):0.00} m");
                case "mario-punches":
                    return (mc != null && mc.Attacks.HitsLanded > 0, "no punch landed on the Lemurian");
                case "mario-sounds":
                    return (MarioAudio.Running && MarioAudio.PeakSample > 300, $"audio {(MarioAudio.Running ? "on" : "off")}, peak {MarioAudio.PeakSample}");
                default:
                    return (false, "unknown check");
            }
        }
    }
}
