using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace MarioRoR2
{
    /// <summary>
    /// Draws one libsm64 Mario: his triangles from each tick, interpolated between ticks, lit by RoR2's own
    /// deferred shader. SM64 colours untextured parts by vertex colour and blends textures over it by alpha;
    /// RoR2's shader has no vertex colours, so each colour gets its own row in a baked copy of the ROM atlas.
    /// </summary>
    internal sealed class MarioRenderer : IDisposable
    {
        public static Shader? LitShader;

        public SM64MarioGeometryBuffers Buffers;
        public int Triangles => Buffers.numTrianglesUsed;
        public readonly GameObject Object;

        private readonly float[] pos = new float[Native.MaxTriangles * 9];
        private readonly float[] nrm = new float[Native.MaxTriangles * 9];
        private readonly float[] col = new float[Native.MaxTriangles * 9];
        private readonly float[] uv = new float[Native.MaxTriangles * 6];
        private readonly GCHandle hPos, hNrm, hCol, hUv;

        private readonly Vector3[] prev = new Vector3[Native.MaxTriangles * 3];
        private readonly Vector3[] cur = new Vector3[Native.MaxTriangles * 3];
        private readonly Vector3[] verts = new Vector3[Native.MaxTriangles * 3];
        private readonly Vector3[] normals = new Vector3[Native.MaxTriangles * 3];
        private readonly Vector2[] uvs = new Vector2[Native.MaxTriangles * 3];
        private readonly Mesh mesh;
        private int lastTris;
        private bool hasPrev;

        public MarioRenderer(string name, Transform? parent)
        {
            hPos = GCHandle.Alloc(pos, GCHandleType.Pinned);
            hNrm = GCHandle.Alloc(nrm, GCHandleType.Pinned);
            hCol = GCHandle.Alloc(col, GCHandleType.Pinned);
            hUv = GCHandle.Alloc(uv, GCHandleType.Pinned);
            Buffers = new SM64MarioGeometryBuffers
            {
                position = hPos.AddrOfPinnedObject(),
                normal = hNrm.AddrOfPinnedObject(),
                color = hCol.AddrOfPinnedObject(),
                uv = hUv.AddrOfPinnedObject(),
            };

            Object = new GameObject(name);
            if (parent != null)
            {
                Object.transform.SetParent(parent, false);
            }
            Object.layer = RoR2.LayerIndex.defaultLayer.intVal;
            mesh = new Mesh { name = name };
            mesh.MarkDynamic();
            mesh.vertices = verts;
            mesh.normals = normals;
            mesh.uv = uvs;
            int[] idx = new int[verts.Length];
            for (int i = 0; i < idx.Length; i++)
            {
                idx[i] = i;
            }
            mesh.triangles = idx;
            Object.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = Object.AddComponent<MeshRenderer>();
            mr.sharedMaterial = BakedAtlas.Material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        }

        /// <summary>Takes the triangles libsm64 just wrote, relative to origin (SM64 units), into the current frame.</summary>
        public void TakeTick(Vector3 sm64Origin)
        {
            int tris = Buffers.numTrianglesUsed;
            Array.Copy(cur, prev, cur.Length);
            for (int t = 0; t < tris; t++)
            {
                Color32 c = new Color32((byte)(col[t * 9] * 255f), (byte)(col[t * 9 + 1] * 255f), (byte)(col[t * 9 + 2] * 255f), 255);
                bool textured = !(uv[t * 6] == 1f && uv[t * 6 + 1] == 1f && uv[t * 6 + 2] == 1f && uv[t * 6 + 3] == 1f);
                for (int k = 0; k < 3; k++)
                {
                    int v = t * 3 + k;
                    cur[v] = MarioMath.ToUnity(pos[v * 3] - sm64Origin.x, pos[v * 3 + 1] - sm64Origin.y, pos[v * 3 + 2] - sm64Origin.z);
                    normals[v] = new Vector3(-nrm[v * 3], nrm[v * 3 + 1], nrm[v * 3 + 2]);
                    uvs[v] = BakedAtlas.Map(c, textured, uv[v * 2], uv[v * 2 + 1]);
                }
            }
            for (int v = tris * 3; v < lastTris * 3; v++)
            {
                cur[v] = Vector3.zero;
            }
            if (!hasPrev || tris != lastTris)
            {
                Array.Copy(cur, prev, cur.Length); // topology changed (or first tick): no blend
                hasPrev = true;
            }
            lastTris = tris;
            mesh.normals = normals;
            mesh.uv = uvs;
        }

        /// <summary>Blend between the last two ticks (alpha 0..1) and upload.</summary>
        public void Render(float alpha, Vector3 boundsCenter)
        {
            int n = lastTris * 3;
            for (int v = 0; v < n; v++)
            {
                verts[v] = Vector3.LerpUnclamped(prev[v], cur[v], alpha);
            }
            for (int v = n; v < verts.Length && verts[v] != Vector3.zero; v++)
            {
                verts[v] = Vector3.zero;
            }
            mesh.vertices = verts;
            mesh.bounds = new Bounds(boundsCenter, new Vector3(4f, 4f, 4f));
        }

        public void SetVisible(bool visible)
        {
            if (Object && Object.activeSelf != visible)
            {
                Object.SetActive(visible);
            }
        }

        public void Dispose()
        {
            if (Object)
            {
                UnityEngine.Object.Destroy(Object);
            }
            if (mesh)
            {
                UnityEngine.Object.Destroy(mesh);
            }
            hPos.Free();
            hNrm.Free();
            hCol.Free();
            hUv.Free();
        }
    }

    /// <summary>The ROM's Mario atlas, one row per SM64 colour: tile pixels blended over that colour by alpha, plus a solid strip.</summary>
    internal static class BakedAtlas
    {
        private const int Rows = 16;
        private const int Strip = 4;
        private const int W = Native.TextureWidth + Strip;
        private const int H = Native.TextureHeight * Rows;
        private static Texture2D? texture;
        private static Material? material;
        private static readonly Dictionary<int, int> rowByColour = new Dictionary<int, int>();

        public static Material Material
        {
            get
            {
                if (material == null)
                {
                    texture = new Texture2D(W, H, TextureFormat.RGBA32, false) { name = "MarioAtlas", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                    material = new Material(MarioRenderer.LitShader != null ? MarioRenderer.LitShader : Shader.Find("Standard")) { name = "MarioMaterial" };
                    material.mainTexture = texture;
                    material.SetTexture("_MainTex", texture);
                    material.SetColor("_Color", Color.white);
                    material.SetFloat("_Cull", 0f); // the X mirror flips winding; draw both sides
                    material.SetFloat("_Smoothness", 0.15f);
                    material.SetFloat("_EmPower", 0f);
                    material.SetFloat("_NormalStrength", 0f);
                }
                return material;
            }
        }

        public static Vector2 Map(Color32 c, bool textured, float u, float v)
        {
            int row = RowFor(c);
            if (!textured)
            {
                return new Vector2((Native.TextureWidth + Strip * 0.5f) / W, (row * Native.TextureHeight + 32f) / H);
            }
            float py = Mathf.Clamp(v * Native.TextureHeight, 0.5f, Native.TextureHeight - 0.5f);
            return new Vector2(u * Native.TextureWidth / W, (row * Native.TextureHeight + py) / H);
        }

        private static int RowFor(Color32 c)
        {
            int key = c.r << 16 | c.g << 8 | c.b;
            if (rowByColour.TryGetValue(key, out int row))
            {
                return row;
            }
            row = rowByColour.Count;
            if (row >= Rows)
            {
                return 0; // shortcut: more than 16 colours reuse the first row; Mario has about 6
            }
            rowByColour[key] = row;
            _ = Material;
            Color32[] px = new Color32[W * Native.TextureHeight];
            for (int y = 0; y < Native.TextureHeight; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    Color32 o = c;
                    if (x < Native.TextureWidth && Sm64.Atlas.Length > 0)
                    {
                        Color32 t = Sm64.Atlas[y * Native.TextureWidth + x];
                        float a = t.a / 255f;
                        o = new Color32((byte)(c.r + (t.r - c.r) * a), (byte)(c.g + (t.g - c.g) * a), (byte)(c.b + (t.b - c.b) * a), 255);
                    }
                    px[y * W + x] = o;
                }
            }
            texture!.SetPixels32(0, row * Native.TextureHeight, W, Native.TextureHeight, px);
            texture.Apply(false);
            return row;
        }

        /// <summary>A square icon: a coloured disc with one tile of Mario's own texture on it.</summary>
        public static Texture2D Icon(Color background, int tile, int size)
        {
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "MarioIcon" + tile, filterMode = FilterMode.Bilinear };
            int tw = tile == 0 ? 64 : 32, th = tile >= 9 ? 64 : 32; // load_tex_data.c sizes
            var px = new Color32[size * size];
            float r = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - r, dy = y + 0.5f - r;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    Color o = d < r - 1 ? background * (1f - 0.35f * d / r) : new Color(0, 0, 0, 0);
                    o.a = d < r - 1 ? 1f : 0f;
                    // the tile, centred in the inner 70%
                    float fx = (x - size * 0.15f) / (size * 0.7f), fy = (y - size * 0.15f) / (size * 0.7f);
                    if (fx >= 0 && fx < 1 && fy >= 0 && fy < 1 && Sm64.Atlas.Length > 0)
                    {
                        int sx = tile * 64 + (int)(fx * tw);
                        int sy = (int)((1f - fy) * th); // atlas row 0 is the tile's top
                        Color32 s = Sm64.Atlas[Mathf.Clamp(sy, 0, Native.TextureHeight - 1) * Native.TextureWidth + sx];
                        float a = s.a / 255f;
                        o = Color.Lerp(o, new Color(s.r / 255f, s.g / 255f, s.b / 255f, 1f), a * o.a);
                    }
                    px[y * size + x] = o;
                }
            }
            t.SetPixels32(px);
            t.Apply(false);
            return t;
        }
    }
}
