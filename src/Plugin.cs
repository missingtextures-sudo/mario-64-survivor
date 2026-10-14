using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace MarioRoR2
{
    [BepInPlugin(Guid, "Mario 64 Survivor", Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "gg.melty.marioror2";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log = null!;
        internal static ConfigEntry<string> RomPath = null!;
        internal static ConfigEntry<float> Volume = null!;

        private void Awake()
        {
            Log = Logger;
            RomPath = Config.Bind("Super Mario 64", "RomPath", "", "Your Super Mario 64 (US) ROM. Melty fills this in for you through melty.properties; set it here only when playing without Melty.");
            Volume = Config.Bind("Super Mario 64", "Volume", 0.8f, "Mario's voice and sound effects, 0 to 1.");
            MarioAudio.Volume = Volume.Value;

            Log.LogInfo(MarioMath.SelfCheck(out string why) ? "self-check: SM64/Unity conversions pass" : "self-check FAILED: " + why);
            CheckHooks();
            Sm64.Init(Path.GetDirectoryName(Info.Location)!);
            if (!Sm64.Ready)
            {
                Log.LogError("Mario isn't playable: " + Sm64.Problem);
            }
            MarioContent.Register();
            DamageReaction.Hook();
            gameObject.AddComponent<DevHarness>();
        }

        /// <summary>Every game member in sheets/hooks.json must exist in this build of the game.</summary>
        private static void CheckHooks()
        {
            int missing = 0;
            foreach (Sheets.HookRow h in Sheets.Hooks)
            {
                Type? t = Type.GetType(h.GameType + ", RoR2") ?? Type.GetType(h.GameType + ", KinematicCharacterController") ?? Type.GetType(h.GameType + ", RoR2.UI");
                if (t == null || t.GetMember(h.GameMember, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance).Length == 0)
                {
                    Log.LogError($"hook {h.Id}: {h.GameType}.{h.GameMember} isn't in this version of Risk of Rain 2");
                    missing++;
                }
            }
            Log.LogInfo($"hooks: {Sheets.Hooks.Length - missing}/{Sheets.Hooks.Length} game members present");
        }
    }
}
