using System.Collections.Generic;
using System.Runtime.InteropServices;
using RoR2;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MarioRoR2
{
    /// <summary>
    /// The stage's world colliders as SM64 surfaces. libsm64 checks every loaded triangle each step, so the
    /// stage is cut into square chunks once per scene and only the chunks around Mario are loaded (each one
    /// libsm64 surface object). Chunks far from Mario are dropped; Mario never stands on those.
    /// </summary>
    internal static class LevelCollision
    {
        private static readonly float ChunkSize = Sheets.ChunkSize;
        private static readonly int Radius = (int)Sheets.ChunkRadius;

        private static int builtScene = -1;
        private static readonly Dictionary<long, List<SM64Surface>> chunks = new Dictionary<long, List<SM64Surface>>();
        private static readonly Dictionary<long, (uint id, int count)> loaded = new Dictionary<long, (uint id, int count)>();
        private static readonly List<long> scratch = new List<long>();

        public static int TriangleCount { get; private set; }
        public static int ColliderCount { get; private set; }
        public static int SkippedColliders { get; private set; }
        private static readonly Dictionary<string, int> skipped = new Dictionary<string, int>();
        private static int sampledMeshes;
        private static readonly List<MeshCollider> unreadable = new List<MeshCollider>();
        private static readonly HashSet<long> sampled = new HashSet<long>();
        public static int LoadedChunks => loaded.Count;
        public static int LoadedSurfaces { get; private set; }

        private static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

        public static void EnsureBuilt()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (builtScene == scene.handle)
            {
                return;
            }
            Clear();
            builtScene = scene.handle;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int mask = LayerIndex.world.mask | LayerIndex.collideWithCharacterHullOnly.mask;
            var tri = new List<Vector3>(4096);
            foreach (Collider col in Object.FindObjectsOfType<Collider>())
            {
                if (col.isTrigger || !col.enabled || (mask & (1 << col.gameObject.layer)) == 0)
                {
                    continue;
                }
                if (col.attachedRigidbody != null && !col.attachedRigidbody.isKinematic)
                {
                    continue; // loose physics props
                }
                if (col.GetComponentInParent<UnityEngine.Networking.NetworkIdentity>() != null)
                {
                    continue; // spawned objects (drop pods, shrines, doors): not stage geometry, and Mario lands next to his pod
                }
                tri.Clear();
                string? why = Triangulate(col, tri);
                if (why != null)
                {
                    SkippedColliders++;
                    skipped[why] = skipped.TryGetValue(why, out int n) ? n + 1 : 1;
                    continue;
                }
                ColliderCount++;
                for (int i = 0; i + 2 < tri.Count; i += 3)
                {
                    Add(tri[i], tri[i + 1], tri[i + 2]);
                }
            }
            Plugin.Log.LogInfo($"collision: {scene.name}: {TriangleCount} triangles from {ColliderCount} colliders in {chunks.Count} chunks ({sampledMeshes} unreadable meshes sampled by raycast; {SkippedColliders} skipped{(skipped.Count > 0 ? ": " + string.Join(", ", System.Linq.Enumerable.Select(skipped, kv => kv.Key + " x" + kv.Value)) : "")}) in {sw.ElapsedMilliseconds} ms");
        }

        private static void Add(Vector3 a, Vector3 b, Vector3 c)
        {
            if (Vector3.Cross(b - a, c - a).sqrMagnitude < 1e-8f)
            {
                return;
            }
            SM64Surface s = MarioMath.Surface(a, b, c);
            TriangleCount++;
            int x0 = Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x)) / ChunkSize);
            int x1 = Mathf.FloorToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x)) / ChunkSize);
            int z0 = Mathf.FloorToInt(Mathf.Min(a.z, Mathf.Min(b.z, c.z)) / ChunkSize);
            int z1 = Mathf.FloorToInt(Mathf.Max(a.z, Mathf.Max(b.z, c.z)) / ChunkSize);
            for (int x = x0; x <= x1; x++)
            {
                for (int z = z0; z <= z1; z++)
                {
                    long k = Key(x, z);
                    if (!chunks.TryGetValue(k, out var list))
                    {
                        chunks[k] = list = new List<SM64Surface>();
                    }
                    list.Add(s);
                }
            }
        }

        /// <summary>Appends the collider's world triangles; returns why not when it can't.</summary>
        private static string? Triangulate(Collider col, List<Vector3> o)
        {
            Transform t = col.transform;
            switch (col)
            {
                case MeshCollider mc:
                {
                    Mesh m = mc.sharedMesh;
                    if (m == null)
                    {
                        return "mesh collider without a mesh";
                    }
                    Vector3[] v;
                    int[] idx;
                    if (m.isReadable)
                    {
                        v = m.vertices;
                        idx = m.triangles;
                    }
                    else
                    {
                        sampledMeshes++;
                        unreadable.Add(mc); // sampled chunk by chunk as Mario gets near (SampleChunk)
                        return null;
                    }
                    Matrix4x4 l2w = t.localToWorldMatrix;
                    bool flip = l2w.determinant < 0; // a mirrored instance turns its winding inside out
                    for (int i = 0; i + 2 < idx.Length; i += 3)
                    {
                        Vector3 a = l2w.MultiplyPoint3x4(v[idx[i]]), b = l2w.MultiplyPoint3x4(v[idx[i + 1]]), c = l2w.MultiplyPoint3x4(v[idx[i + 2]]);
                        o.Add(a);
                        o.Add(flip ? c : b);
                        o.Add(flip ? b : c);
                    }
                    return null;
                }
                case BoxCollider bc:
                    Box(o, t, bc.center, bc.size);
                    return null;
                case SphereCollider sc:
                {
                    float r = sc.radius * Mathf.Max(Mathf.Abs(t.lossyScale.x), Mathf.Max(Mathf.Abs(t.lossyScale.y), Mathf.Abs(t.lossyScale.z)));
                    Sphere(o, t.TransformPoint(sc.center), r);
                    return null;
                }
                case CapsuleCollider cc:
                {
                    Vector3 size = new Vector3(cc.radius * 2, cc.radius * 2, cc.radius * 2);
                    size[cc.direction] = Mathf.Max(cc.height, cc.radius * 2);
                    Box(o, t, cc.center, size);
                    return null;
                }
                case TerrainCollider tc:
                    return tc.terrainData != null ? Terrain(o, tc.terrainData, t.position) : "terrain without data";
                default:
                    return col.GetType().Name;
            }
        }

        /// <summary>
        /// Meshes the game ships without a CPU copy (Siphoned Forest's ground, for one), rebuilt for one chunk from
        /// raycasts against each such collider alone: up to 3 stacked layers of top-facing surface, 1 m apart.
        /// The top layer is always joined (its steep parts become SM64 walls); lower layers only where smooth.
        /// </summary>
        private static void SampleChunk(int cx, int cz)
        {
            if (!sampled.Add(Key(cx, cz)) || unreadable.Count == 0)
            {
                return;
            }
            const int Layers = 3;
            const float Step = 1f;
            int n = Mathf.RoundToInt(ChunkSize / Step) + 1;
            float x0 = cx * ChunkSize, z0 = cz * ChunkSize;
            var h = new float[Layers, n, n];
            var tri = new List<Vector3>();
            foreach (MeshCollider mc in unreadable)
            {
                if (mc == null)
                {
                    continue;
                }
                Bounds b = mc.bounds;
                if (b.max.x < x0 || b.min.x > x0 + ChunkSize || b.max.z < z0 || b.min.z > z0 + ChunkSize)
                {
                    continue;
                }
                float top = b.max.y + 1f;
                for (int x = 0; x < n; x++)
                {
                    for (int z = 0; z < n; z++)
                    {
                        float from = top;
                        for (int l = 0; l < Layers; l++)
                        {
                            h[l, x, z] = float.NaN;
                            if (from <= b.min.y)
                            {
                                continue;
                            }
                            var ray = new Ray(new Vector3(x0 + x * Step, from, z0 + z * Step), Vector3.down);
                            if (!mc.Raycast(ray, out RaycastHit hit, from - b.min.y + 1f))
                            {
                                from = b.min.y;
                                continue;
                            }
                            h[l, x, z] = hit.point.y;
                            from = hit.point.y - 0.5f;
                        }
                    }
                }
                tri.Clear();
                for (int l = 0; l < Layers; l++)
                {
                    float maxJump = l == 0 ? float.MaxValue : 1.5f * Step;
                    for (int x = 0; x + 1 < n; x++)
                    {
                        for (int z = 0; z + 1 < n; z++)
                        {
                            float a = h[l, x, z], bb = h[l, x, z + 1], c = h[l, x + 1, z + 1], e = h[l, x + 1, z];
                            if (float.IsNaN(a) || float.IsNaN(bb) || float.IsNaN(c) || float.IsNaN(e)
                                || Mathf.Max(Mathf.Max(a, bb), Mathf.Max(c, e)) - Mathf.Min(Mathf.Min(a, bb), Mathf.Min(c, e)) > maxJump)
                            {
                                continue;
                            }
                            Vector3 P(int i, int k, float y) => new Vector3(x0 + i * Step, y, z0 + k * Step);
                            tri.Add(P(x, z, a)); tri.Add(P(x, z + 1, bb)); tri.Add(P(x + 1, z + 1, c)); // clockwise from above
                            tri.Add(P(x, z, a)); tri.Add(P(x + 1, z + 1, c)); tri.Add(P(x + 1, z, e));
                        }
                    }
                }
                for (int i = 0; i + 2 < tri.Count; i += 3)
                {
                    Add(tri[i], tri[i + 1], tri[i + 2]);
                }
            }
        }

        /// <summary>A terrain heightmap as triangles, about one metre apart.</summary>
        private static string? Terrain(List<Vector3> o, TerrainData d, Vector3 origin)
        {
            int res = d.heightmapResolution;
            float[,] h = d.GetHeights(0, 0, res, res);
            Vector3 size = d.size;
            int step = Mathf.Max(1, Mathf.RoundToInt((res - 1) / Mathf.Max(1f, size.x)));
            Vector3 P(int x, int z) => origin + new Vector3(x * size.x / (res - 1), h[z, x] * size.y, z * size.z / (res - 1));
            for (int z = 0; z + step < res; z += step)
            {
                for (int x = 0; x + step < res; x += step)
                {
                    Vector3 a = P(x, z), b = P(x, z + step), c = P(x + step, z + step), e = P(x + step, z);
                    o.Add(a); o.Add(b); o.Add(c); // clockwise from above: Unity floor
                    o.Add(a); o.Add(c); o.Add(e);
                }
            }
            return null;
        }

        private static void Box(List<Vector3> o, Transform t, Vector3 center, Vector3 size)
        {
            Vector3 h = size * 0.5f;
            Vector3 P(float x, float y, float z) => t.TransformPoint(center + new Vector3(x * h.x, y * h.y, z * h.z));
            Vector3 inside = t.TransformPoint(center);
            Vector3[] c =
            {
                P(-1, -1, -1), P(1, -1, -1), P(1, 1, -1), P(-1, 1, -1),
                P(-1, -1, 1), P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1),
            };
            int[][] faces = { new[] { 0, 1, 2, 3 }, new[] { 5, 4, 7, 6 }, new[] { 4, 0, 3, 7 }, new[] { 1, 5, 6, 2 }, new[] { 3, 2, 6, 7 }, new[] { 4, 5, 1, 0 } };
            foreach (int[] f in faces)
            {
                Outward(o, c[f[0]], c[f[1]], c[f[2]], inside);
                Outward(o, c[f[0]], c[f[2]], c[f[3]], inside);
            }
        }

        private static void Sphere(List<Vector3> o, Vector3 center, float r)
        {
            const int Lat = 6, Lon = 10;
            Vector3 P(int i, int j)
            {
                float th = Mathf.PI * i / Lat, ph = 2 * Mathf.PI * j / Lon;
                return center + r * new Vector3(Mathf.Sin(th) * Mathf.Cos(ph), Mathf.Cos(th), Mathf.Sin(th) * Mathf.Sin(ph));
            }
            for (int i = 0; i < Lat; i++)
            {
                for (int j = 0; j < Lon; j++)
                {
                    Vector3 a = P(i, j), b = P(i + 1, j), c = P(i + 1, j + 1), d = P(i, j + 1);
                    if (i > 0) Outward(o, a, c, d, center);
                    if (i < Lat - 1) Outward(o, a, b, c, center);
                }
            }
        }

        /// <summary>Adds (a, b, c) wound so its Unity normal points away from inside.</summary>
        private static void Outward(List<Vector3> o, Vector3 a, Vector3 b, Vector3 c, Vector3 inside)
        {
            Vector3 n = Vector3.Cross(b - a, c - a);
            bool swap = Vector3.Dot(n, (a + b + c) / 3f - inside) < 0;
            o.Add(a);
            o.Add(swap ? c : b);
            o.Add(swap ? b : c);
        }

        /// <summary>Diagnostics: the triangles in p's chunk whose XZ footprint covers p, with their heights.</summary>
        public static void Describe(Vector3 p)
        {
            long k = Key(Mathf.FloorToInt(p.x / ChunkSize), Mathf.FloorToInt(p.z / ChunkSize));
            if (!chunks.TryGetValue(k, out var list))
            {
                Plugin.Log.LogInfo($"collision at {p}: no chunk");
                return;
            }
            var sb = new System.Text.StringBuilder();
            int covering = 0;
            foreach (SM64Surface s in list)
            {
                Vector3 a = MarioMath.ToUnity(s.v0x, s.v0y, s.v0z), b = MarioMath.ToUnity(s.v1x, s.v1y, s.v1z), c = MarioMath.ToUnity(s.v2x, s.v2y, s.v2z);
                float d = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
                if (Mathf.Abs(d) < 1e-6f) continue;
                float l1 = ((b.z - c.z) * (p.x - c.x) + (c.x - b.x) * (p.z - c.z)) / d;
                float l2 = ((c.z - a.z) * (p.x - c.x) + (a.x - c.x) * (p.z - c.z)) / d;
                float l3 = 1 - l1 - l2;
                if (l1 < 0 || l2 < 0 || l3 < 0) continue;
                covering++;
                if (covering <= 6)
                {
                    sb.Append($" [y {l1 * a.y + l2 * b.y + l3 * c.y:0.00}, sm64 normal {MarioMath.Sm64Normal(s)}]");
                }
            }
            Plugin.Log.LogInfo($"collision at {p}: chunk has {list.Count} triangles, {covering} cover this spot:{sb}");
        }

        /// <summary>Loads the chunks around Mario's position (metres) and drops the rest.</summary>
        public static void Follow(Vector3 p)
        {
            int cx = Mathf.FloorToInt(p.x / ChunkSize), cz = Mathf.FloorToInt(p.z / ChunkSize);
            scratch.Clear();
            foreach (var kv in loaded)
            {
                int x = (int)(kv.Key >> 32), z = (int)kv.Key;
                if (Mathf.Abs(x - cx) > Radius + 1 || Mathf.Abs(z - cz) > Radius + 1)
                {
                    scratch.Add(kv.Key);
                }
            }
            foreach (long k in scratch)
            {
                Native.sm64_surface_object_delete(loaded[k].id);
                LoadedSurfaces -= loaded[k].count;
                loaded.Remove(k);
            }
            for (int x = cx - Radius; x <= cx + Radius; x++)
            {
                for (int z = cz - Radius; z <= cz + Radius; z++)
                {
                    long k = Key(x, z);
                    if (!loaded.ContainsKey(k))
                    {
                        SampleChunk(x, z);
                    }
                    if (loaded.ContainsKey(k) || !chunks.TryGetValue(k, out var list))
                    {
                        continue;
                    }
                    SM64Surface[] arr = list.ToArray();
                    GCHandle h = GCHandle.Alloc(arr, GCHandleType.Pinned);
                    try
                    {
                        var obj = new SM64SurfaceObject { surfaceCount = (uint)arr.Length, surfaces = h.AddrOfPinnedObject() };
                        loaded[k] = (Native.sm64_surface_object_create(ref obj), arr.Length); // libsm64 copies the surfaces
                        LoadedSurfaces += arr.Length;
                    }
                    finally
                    {
                        h.Free();
                    }
                }
            }
        }

        public static void Clear()
        {
            foreach (var l in loaded.Values)
            {
                Native.sm64_surface_object_delete(l.id);
            }
            loaded.Clear();
            chunks.Clear();
            builtScene = -1;
            TriangleCount = ColliderCount = SkippedColliders = LoadedSurfaces = sampledMeshes = 0;
            skipped.Clear();
            unreadable.Clear();
            sampled.Clear();
        }
    }
}
