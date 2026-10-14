using UnityEngine;

namespace MarioRoR2
{
    /// <summary>SM64 space is right-handed in centimetre-ish units; Unity is left-handed metres. X is mirrored.</summary>
    internal static class MarioMath
    {
        public const float S = Sheets.UnitsPerMetre;

        public static Vector3 ToUnity(float x, float y, float z) => new Vector3(-x / S, y / S, z / S);

        public static Vector3 ToUnity(in SM64MarioState s) => ToUnity(s.px, s.py, s.pz);

        public static Vector3 ToSm64(Vector3 v) => new Vector3(-v.x * S, v.y * S, v.z * S);

        /// <summary>SM64 yaw (radians from libsm64's state) as a Unity rotation: SM64 forward is (sin, cos) in x/z, and x is mirrored.</summary>
        public static Quaternion Facing(float faceAngle) => Quaternion.Euler(0f, -faceAngle * Mathf.Rad2Deg, 0f);

        public static Vector3 Forward(float faceAngle) => new Vector3(-Mathf.Sin(faceAngle), 0f, Mathf.Cos(faceAngle));

        /// <summary>
        /// A Unity triangle (a, b, c), front face clockwise, as an SM64 surface. Mirroring X flips the winding,
        /// so it is written (a, c, b); SelfCheck proves a Unity floor stays a floor.
        /// </summary>
        public static SM64Surface Surface(Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 A = ToSm64(a), B = ToSm64(b), C = ToSm64(c);
            return new SM64Surface
            {
                type = (short)Sheets.SurfaceType, force = 0, terrain = 1, // TERRAIN_STONE footstep sounds
                v0x = Mathf.RoundToInt(A.x), v0y = Mathf.RoundToInt(A.y), v0z = Mathf.RoundToInt(A.z),
                v1x = Mathf.RoundToInt(C.x), v1y = Mathf.RoundToInt(C.y), v1z = Mathf.RoundToInt(C.z),
                v2x = Mathf.RoundToInt(B.x), v2y = Mathf.RoundToInt(B.y), v2z = Mathf.RoundToInt(B.z),
            };
        }

        /// <summary>The normal SM64 computes for a surface (load_surfaces.c), to check the winding.</summary>
        public static Vector3 Sm64Normal(in SM64Surface s)
        {
            float x1 = s.v0x, y1 = s.v0y, z1 = s.v0z, x2 = s.v1x, y2 = s.v1y, z2 = s.v1z, x3 = s.v2x, y3 = s.v2y, z3 = s.v2z;
            float nx = (y2 - y1) * (z3 - z2) - (z2 - z1) * (y3 - y2);
            float ny = (z2 - z1) * (x3 - x2) - (x2 - x1) * (z3 - z2);
            float nz = (x2 - x1) * (y3 - y2) - (y2 - y1) * (x3 - x2);
            return new Vector3(nx, ny, nz).normalized;
        }

        /// <summary>Asserts the conversions agree with SM64's conventions; logged at start.</summary>
        public static bool SelfCheck(out string why)
        {
            // A Unity floor: clockwise from above, normal +Y in Unity.
            Vector3 a = new Vector3(0, 0, 0), b = new Vector3(0, 0, 1), c = new Vector3(1, 0, 0);
            if (Vector3.Cross(b - a, c - a).y <= 0) { why = "test triangle isn't a Unity floor"; return false; }
            if (Sm64Normal(Surface(a, b, c)).y < 0.99f) { why = "a Unity floor became an SM64 ceiling"; return false; }
            // A wall facing Unity +X must face SM64 -X.
            Vector3 w0 = new Vector3(1, 0, 0), w1 = new Vector3(1, 1, 0), w2 = new Vector3(1, 0, 1);
            Vector3 un = Vector3.Cross(w1 - w0, w2 - w0).normalized;
            Vector3 sn = Sm64Normal(Surface(w0, w1, w2));
            if (Mathf.Abs(sn.x + un.x) > 0.01f) { why = $"wall normal {un} became {sn}"; return false; }
            // Facing: SM64 yaw 0 looks down +Z in both; yaw +90 degrees looks down SM64 +X = Unity -X.
            if (Vector3.Distance(Forward(0f), Vector3.forward) > 0.001f) { why = "yaw 0 isn't +Z"; return false; }
            if (Vector3.Distance(Forward(Mathf.PI / 2f), Vector3.left) > 0.001f) { why = "yaw 90 isn't Unity -X"; return false; }
            if (Vector3.Distance(Facing(Mathf.PI / 2f) * Vector3.forward, Vector3.left) > 0.001f) { why = "Facing disagrees with Forward"; return false; }
            Vector3 p = new Vector3(1.5f, 2f, -3f);
            Vector3 q = ToSm64(p);
            if (Vector3.Distance(ToUnity(q.x, q.y, q.z), p) > 0.0001f) { why = "ToSm64/ToUnity don't round-trip"; return false; }
            why = "";
            return true;
        }
    }
}
