using System.Collections.Generic;
using RoR2;
using UnityEngine;
using UnityEngine.Networking;

namespace MarioRoR2
{
    /// <summary>
    /// Turns Mario's SM64 attack actions (sheets/attacks.json) into RoR2 hits: damage from his RoR2 damage stat,
    /// on-hit items through GlobalEventManager, as OverlapAttack does. Runs on the server (the host in solo).
    /// </summary>
    internal sealed class MarioAttacks
    {
        private const uint ActFlagAir = 1u << 11;
        private const uint ActGroundPound = 0x008008A9;
        private static readonly Collider[] hits = new Collider[64];

        private readonly HashSet<HealthComponent> hitThisAction = new HashSet<HealthComponent>();
        private readonly Dictionary<HealthComponent, float> stompCooldown = new Dictionary<HealthComponent, float>();
        private uint lastAction;
        private int lastAnim;

        /// <summary>Hits landed (for the test's check).</summary>
        public int HitsLanded;
        public float DamageDealt;

        public void Tick(MarioController mc)
        {
            if (!NetworkServer.active || !mc.Body.healthComponent.alive)
            {
                return;
            }
            ref SM64MarioState s = ref mc.State;
            Sheets.AttackRow? row = Find(s.action);
            bool newInstance = s.action != lastAction || (row.HasValue && row.Value.PerStep && s.animID != lastAnim);
            if (newInstance)
            {
                hitThisAction.Clear();
            }
            lastAction = s.action;
            lastAnim = s.animID;

            Vector3 feet = MarioMath.ToUnity(s);
            Vector3 fwd = MarioMath.Forward(s.faceAngle);
            if (row.HasValue)
            {
                Sheets.AttackRow r = row.Value;
                if (newInstance)
                {
                    Plugin.Log.LogDebug($"attack {r.Id} (anim {s.animID})");
                }
                Hit(mc, r, feet + fwd * r.Forward + Vector3.up * r.Up, fwd, hitThisAction, null);
            }
            // Stomp: falling onto something's head (not during a ground pound, which has its own landing hit).
            if ((s.action & ActFlagAir) != 0 && s.vy < 0f && s.action != ActGroundPound)
            {
                Sheets.AttackRow stomp = StompRow();
                Hit(mc, stomp, feet, fwd, null, stompCooldown);
            }
        }

        private void Hit(MarioController mc, Sheets.AttackRow r, Vector3 center, Vector3 fwd, HashSet<HealthComponent>? once, Dictionary<HealthComponent, float>? cooldown)
        {
            CharacterBody body = mc.Body;
            TeamIndex team = body.teamComponent.teamIndex;
            int n = Physics.OverlapSphereNonAlloc(center, r.Radius, hits, LayerIndex.entityPrecise.mask, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
            {
                HurtBox hb = hits[i].GetComponent<HurtBox>();
                if (hb == null || hb.healthComponent == null)
                {
                    continue;
                }
                HealthComponent hc = hb.healthComponent;
                if (hc == body.healthComponent || !hc.alive || !FriendlyFireManager.ShouldDirectHitProceed(hc, team))
                {
                    continue;
                }
                if (once != null && !once.Add(hc))
                {
                    continue;
                }
                if (cooldown != null)
                {
                    if (cooldown.TryGetValue(hc, out float until) && Time.time < until)
                    {
                        continue;
                    }
                    // Only a real stomp counts: SM64 decides from Mario's fall onto the target's top.
                    Bounds bb = hits[i].bounds;
                    Vector3 basePos = MarioMath.ToSm64(new Vector3(bb.center.x, bb.min.y, bb.center.z));
                    if (!Native.sm64_mario_attack(mc.MarioId, basePos.x, basePos.y, basePos.z, bb.size.y * MarioMath.S))
                    {
                        continue;
                    }
                    cooldown[hc] = Time.time + 0.4f;
                }
                else if (once != null && once.Count == 1)
                {
                    // SM64's own recoil off what Mario punched (first target of each action only).
                    Bounds bb = hits[i].bounds;
                    Vector3 basePos = MarioMath.ToSm64(new Vector3(bb.center.x, bb.min.y, bb.center.z));
                    Native.sm64_mario_attack(mc.MarioId, basePos.x, basePos.y, basePos.z, bb.size.y * MarioMath.S);
                }

                var di = new DamageInfo
                {
                    attacker = body.gameObject,
                    inflictor = body.gameObject,
                    damage = body.damage * r.DamageCoefficient,
                    crit = body.RollCrit(),
                    position = hits[i].ClosestPoint(center),
                    force = (fwd + Vector3.up * 0.3f).normalized * r.Force,
                    procCoefficient = r.ProcCoefficient,
                    procChainMask = default,
                    damageColorIndex = DamageColorIndex.Default,
                    damageType = DamageType.Generic,
                    inflictedHurtbox = hb,
                };
                di.ModifyDamageInfo(hb.damageModifier);
                hc.TakeDamage(di);
                GlobalEventManager.instance.OnHitEnemy(di, hc.gameObject);
                GlobalEventManager.instance.OnHitAll(di, hc.gameObject);
                HitsLanded++;
                DamageDealt += di.damage;
            }
        }

        private static Sheets.AttackRow? Find(uint action)
        {
            foreach (Sheets.AttackRow r in Sheets.Attacks)
            {
                if (r.Action == action && action != 0)
                {
                    return r;
                }
            }
            return null;
        }

        private static Sheets.AttackRow StompRow()
        {
            foreach (Sheets.AttackRow r in Sheets.Attacks)
            {
                if (r.Id == "stomp")
                {
                    return r;
                }
            }
            throw new System.InvalidOperationException("sheets/attacks.json has no stomp row");
        }
    }
}
