using RoR2;

namespace MarioRoR2
{
    /// <summary>A big hit on Mario (sheets/tuning knockbackFraction of his max health) knocks him back the SM64 way.</summary>
    internal static class DamageReaction
    {
        public static int Knockbacks;

        public static void Hook() => GlobalEventManager.onServerDamageDealt += OnDamage;

        private static void OnDamage(DamageReport report)
        {
            if (report.victimBody == null || report.damageInfo == null || report.damageInfo.rejected)
            {
                return;
            }
            MarioController? mc = report.victimBody.GetComponent<MarioController>();
            if (mc == null || mc.MarioId < 0 || report.damageDealt < report.victimBody.healthComponent.fullCombinedHealth * Sheets.KnockbackFraction)
            {
                return;
            }
            UnityEngine.Vector3 from = report.attackerBody != null ? report.attackerBody.corePosition : report.damageInfo.position;
            UnityEngine.Vector3 s = MarioMath.ToSm64(from);
            Native.sm64_mario_take_damage(mc.MarioId, 1, 0, s.x, s.y, s.z);
            Knockbacks++;
        }
    }
}
