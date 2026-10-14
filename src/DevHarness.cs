using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using RoR2;
using Path = System.IO.Path;
using UnityEngine;
using UnityEngine.Networking;

namespace MarioRoR2
{
    /// <summary>
    /// Scripted play for testing: Melty's test runs (MeltyTest) and BepInEx/mario_cmd.txt during development.
    /// One command per line, run in order; waits block the queue. Players never have either file.
    /// </summary>
    internal sealed class DevHarness : MonoBehaviour
    {
        private readonly Queue<string> queue = new Queue<string>();
        private float sleepUntil;
        private string? waitScene;
        private bool waitMario;
        private bool waitIdle;
        private bool waitInput;
        private float waitNear = -1f;
        private float waitUntil;
        private float nextPoll;
        private MeltyTest? melty;
        private string cmdFile = "";
        private string shotDir = "";

        private void Start()
        {
            cmdFile = Path.Combine(BepInEx.Paths.BepInExRootPath, "mario_cmd.txt");
            shotDir = Path.Combine(BepInEx.Paths.BepInExRootPath, "mario-shots");
            melty = MeltyTest.TryLoad();
            if (melty != null)
            {
                MeltyTest.EndOnQuit();
                foreach (string line in MeltyTest.Script)
                {
                    queue.Enqueue(line);
                }
            }
        }

        private bool noMonsters;

        private float nextTestFilePoll;

        private void Update()
        {
            // Melty takes its test file away when it ends a run: leave then, before its close, as RiskOfCraft does
            // (Risk of Rain 2 is slow to close, and a game seen closing late keeps Melty from starting the next run).
            if (melty != null && melty.AllReported && Time.unscaledTime >= nextTestFilePoll)
            {
                nextTestFilePoll = Time.unscaledTime + 0.25f;
                if (!File.Exists(melty.TestFile))
                {
                    MeltyTest.ExitNow("Melty ended the run (its test file is gone)");
                }
                else if (DateTime.UtcNow >= melty.LeaveAt)
                {
                    MeltyTest.ExitNow("the run's time is up");
                }
            }
            if (noMonsters)
            {
                foreach (CombatDirector d in CombatDirector.instancesList.ToArray()) // disabling one removes it from the list
                {
                    d.enabled = false;
                }
            }
            if (Time.unscaledTime >= nextPoll)
            {
                nextPoll = Time.unscaledTime + 0.25f;
                Poll();
            }
            if (waitScene != null || waitMario || waitIdle || waitInput || waitNear > 0f)
            {
                MarioController? m = MarioController.Local;
                bool done = waitNear > 0f ? m != null && Nearest(m, out float d) != null && d <= waitNear
                    : waitInput ? m == null || m.OverrideTicks <= 0
                    : waitIdle ? m != null && m.State.action == 0x0C400201 // ACT_IDLE: standing still on the ground
                    : waitMario ? m != null && m.MarioId >= 0
                    : UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == waitScene;
                if (!done && Time.unscaledTime < waitUntil)
                {
                    return;
                }
                if (!waitInput || !done)
                {
                    Plugin.Log.LogInfo(done ? "dev: wait done" : "dev: wait timed out");
                }
                waitScene = null;
                waitMario = false;
                waitIdle = false;
                waitInput = false;
                waitNear = -1f;
            }
            while (queue.Count > 0 && Time.unscaledTime >= sleepUntil && waitScene == null && !waitMario && !waitIdle && !waitInput && waitNear <= 0f)
            {
                string line = queue.Dequeue();
                try
                {
                    Run(line);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"dev: '{line}' failed: {e}");
                }
            }
        }

        private void Poll()
        {
            try
            {
                if (!File.Exists(cmdFile))
                {
                    return;
                }
                string[] lines = File.ReadAllLines(cmdFile);
                File.Delete(cmdFile);
                foreach (string l in lines)
                {
                    if (l.Trim().Length > 0)
                    {
                        queue.Enqueue(l.Trim());
                    }
                }
            }
            catch (IOException)
            {
                // being written; next poll
            }
        }

        private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        /// <summary>The closest living monster to Mario's feet (distance in metres).</summary>
        private static CharacterBody? Nearest(MarioController mc, out float dist)
        {
            CharacterBody? best = null;
            dist = float.MaxValue;
            Vector3 feet = MarioMath.ToUnity(mc.State);
            foreach (TeamComponent t in TeamComponent.GetTeamMembers(TeamIndex.Monster))
            {
                if (t.body == null || !t.body.healthComponent.alive)
                {
                    continue;
                }
                float d = Vector3.Distance(t.body.footPosition, feet);
                if (d < dist)
                {
                    dist = d;
                    best = t.body;
                }
            }
            return best;
        }

        /// <summary>Holds the override for that long in SM64 ticks, then 2 released ticks; the queue waits for it.</summary>
        private void Hold(MarioController mc, float seconds)
        {
            mc.OverrideHoldTicks = Mathf.Max(1, Mathf.RoundToInt(seconds * Sheets.TickRate));
            mc.OverrideTicks = mc.OverrideHoldTicks + 2;
            waitInput = true;
            waitUntil = Time.unscaledTime + 30f;
        }

        private void Run(string line)
        {
            string[] a = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            Plugin.Log.LogInfo("dev> " + line);
            MarioController? mc = MarioController.Local;
            switch (a[0])
            {
                case "log":
                    Plugin.Log.LogInfo("dev: " + line.Substring(3).Trim());
                    break;
                case "sleep":
                    sleepUntil = Time.unscaledTime + F(a[1]);
                    break;
                case "waitscene":
                    waitScene = a[1];
                    waitUntil = Time.unscaledTime + (a.Length > 2 ? F(a[2]) : 120f);
                    break;
                case "waitidle":
                    waitIdle = true;
                    waitUntil = Time.unscaledTime + (a.Length > 1 ? F(a[1]) : 10f);
                    break;
                case "waitmario":
                    waitMario = true;
                    waitUntil = Time.unscaledTime + (a.Length > 1 ? F(a[1]) : 120f);
                    break;
                case "host":
                    if (LocalUserManager.readOnlyLocalUsersList.Count == 0)
                    {
                        RoR2.Console.instance.SubmitCmd(null, "test_splitscreen 1"); // signs in the last profile, as the title's picker would
                    }
                    RoR2.Console.instance.SubmitCmd(null, "host 0");
                    break;
                case "pickmario":
                    if (MarioContent.Survivor != null)
                    {
                        foreach (LocalUser u in LocalUserManager.readOnlyLocalUsersList)
                        {
                            u.userProfile.SetSurvivorPreference(MarioContent.Survivor);
                            u.currentNetworkUser?.CallCmdSetBodyPreference(BodyCatalog.FindBodyIndex(MarioContent.Survivor.bodyPrefab));
                        }
                    }
                    break;
                case "startrun":
                    if (PreGameController.instance != null && NetworkServer.active)
                    {
                        PreGameController.instance.StartRun();
                    }
                    break;
                case "exitpod":
                    foreach (LocalUser u in LocalUserManager.readOnlyLocalUsersList)
                    {
                        CharacterBody? b = u.cachedBody;
                        if (b != null && b.currentVehicle != null && NetworkServer.active)
                        {
                            b.currentVehicle.OnInteractionBegin(b.GetComponent<Interactor>());
                        }
                    }
                    break;
                case "nomonsters":
                    noMonsters = true; // kept up on every stage (Update)
                    if (NetworkServer.active)
                    {
                        // the stage's opening monsters too: a scripted Mario doesn't fight back
                        foreach (TeamComponent t in new List<TeamComponent>(TeamComponent.GetTeamMembers(TeamIndex.Monster)))
                        {
                            t.body?.healthComponent?.Suicide();
                        }
                    }
                    break;
                case "mark":
                    mc?.MarkForTest();
                    break;
                case "move":
                {
                    // move <degrees from Mario's facing> <seconds> [A] [B] [Z]
                    if (mc == null) break;
                    Vector3 f = MarioMath.Forward(mc.State.faceAngle);
                    mc.OverrideMove = Quaternion.Euler(0, F(a[1]), 0) * f;
                    mc.OverrideA = Array.IndexOf(a, "A") > 2;
                    mc.OverrideB = Array.IndexOf(a, "B") > 2;
                    mc.OverrideZ = Array.IndexOf(a, "Z") > 2;
                    Hold(mc, F(a[2]));
                    break;
                }
                case "press":
                    // press <A|B|Z ...> <seconds>: buttons with the stick at rest
                    if (mc == null) break;
                    mc.OverrideMove = Vector3.zero;
                    mc.OverrideA = Array.IndexOf(a, "A") > 0;
                    mc.OverrideB = Array.IndexOf(a, "B") > 0;
                    mc.OverrideZ = Array.IndexOf(a, "Z") > 0;
                    Hold(mc, F(a[a.Length - 1]));
                    break;
                case "spawn":
                    // spawn <master> <metres in front of Mario>
                    if (mc == null || !NetworkServer.active) break;
                    GameObject master = MasterCatalog.FindMasterPrefab(a[1]);
                    if (master == null)
                    {
                        Plugin.Log.LogWarning("dev: no master " + a[1]);
                        break;
                    }
                    Vector3 at = mc.transform.position + MarioMath.Forward(mc.State.faceAngle) * F(a[2]) + Vector3.up * 0.5f;
                    CharacterMaster? cm = new MasterSummon { masterPrefab = master, position = at, rotation = Quaternion.LookRotation(-MarioMath.Forward(mc.State.faceAngle)), teamIndexOverride = TeamIndex.Monster, ignoreTeamMemberLimit = true }.Perform();
                    Plugin.Log.LogInfo($"dev: spawned {a[1]} at {at}: {(cm != null && cm.GetBody() != null ? cm.GetBody().transform.position.ToString() : "no body")}");
                    break;
                case "teleport":
                    mc?.Teleport(new Vector3(F(a[1]), F(a[2]), F(a[3])));
                    break;
                case "status":
                    Plugin.Log.LogInfo(mc == null ? "status: no Mario" : $"status: pos {MarioMath.ToUnity(mc.State)} action 0x{mc.State.action:X8} anim {mc.State.animID} tris {mc.Renderer?.Triangles} hits {mc.Attacks.HitsLanded} dmg {mc.Attacks.DamageDealt:0} hp {mc.Body.healthComponent.health:0}/{mc.Body.healthComponent.fullHealth:0} surfaces {LevelCollision.LoadedSurfaces} chunks {LevelCollision.LoadedChunks} audioPeak {MarioAudio.PeakSample} tick {mc.TickMs:0.00} ms; forward {MarioMath.Forward(mc.State.faceAngle)} velocity {new Vector3(-mc.State.vx, 0, mc.State.vz).normalized} asked {mc.OverrideMove.normalized} body-forward {mc.transform.forward}");
                    break;
                case "waitnear":
                    // waitnear <metres> <timeout>: until a monster is that close (they walk up to Mario)
                    waitNear = F(a[1]);
                    waitUntil = Time.unscaledTime + (a.Length > 2 ? F(a[2]) : 8f);
                    break;
                case "facetarget":
                    if (mc != null && Nearest(mc, out float near) is CharacterBody target)
                    {
                        Vector3 d = target.footPosition - MarioMath.ToUnity(mc.State);
                        float yaw = Mathf.Atan2(-d.x, d.z); // inverse of MarioMath.Forward
                        Native.sm64_set_mario_faceangle(mc.MarioId, yaw);
                        mc.State.faceAngle = yaw;
                        Plugin.Log.LogInfo($"dev: facing {target.name} {near:0.0} m away");
                    }
                    break;
                case "facecam":
                    // turn Mario to look where the camera looks (showcase shots)
                    if (mc != null && CameraRigController.readOnlyInstancesList.Count > 0)
                    {
                        Vector3 f = CameraRigController.readOnlyInstancesList[0].transform.forward;
                        float yaw = Mathf.Atan2(-f.x, f.z);
                        Native.sm64_set_mario_faceangle(mc.MarioId, yaw);
                        mc.State.faceAngle = yaw;
                    }
                    break;
                case "stage":
                    if (NetworkServer.active && RoR2.Run.instance != null)
                    {
                        RoR2.Run.instance.AdvanceStage(SceneCatalog.FindSceneDef(a[1]));
                    }
                    break;
                case "camstatus":
                    foreach (CameraRigController c in CameraRigController.readOnlyInstancesList)
                    {
                        Plugin.Log.LogInfo($"camera: target {(c.target ? c.target.name : "none")} at {(c.target ? c.target.transform.position.ToString() : "-")}, camera at {c.transform.position}, mode {c.cameraMode?.GetType().Name}, body {(mc != null ? mc.transform.position.ToString() : "-")}");
                    }
                    break;
                case "shot":
                    Directory.CreateDirectory(shotDir);
                    string path = Path.Combine(shotDir, (a.Length > 1 ? a[1] : DateTime.Now.ToString("HHmmss")) + ".png");
                    ScreenCapture.CaptureScreenshot(path);
                    Plugin.Log.LogInfo("dev: screenshot " + path);
                    break;
                case "console":
                    RoR2.Console.instance.SubmitCmd(null, line.Substring(7).Trim());
                    break;
                case "check":
                    (bool pass, string why) = MeltyTest.Judge(a[1], mc);
                    Plugin.Log.LogInfo($"check {a[1]}: {(pass ? "pass" : "fail: " + why)}");
                    melty?.Report(a[1], pass, why);
                    break;
                default:
                    Plugin.Log.LogWarning("dev: unknown command " + a[0]);
                    break;
            }
        }
    }
}
