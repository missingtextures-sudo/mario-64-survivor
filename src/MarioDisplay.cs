using System.Runtime.InteropServices;
using RoR2;
using UnityEngine;

namespace MarioRoR2
{
    /// <summary>
    /// The character select's Mario: a real libsm64 Mario standing on a private floor far from any stage,
    /// idling at 30 ticks a second, drawn where Commando's display model was.
    /// </summary>
    internal sealed class MarioDisplay : MonoBehaviour
    {
        private const float FarX = 300000f; // SM64 units, away from any stage geometry
        private int marioId = -1;
        private uint floorId;
        private bool hasFloor;
        private MarioRenderer? renderer;
        private SM64MarioState state;
        private float accumulator;
        private CharacterModel? model;

        private void Start()
        {
            model = GetComponentInChildren<CharacterModel>();
            if (!Sm64.Ready)
            {
                return; // Commando's model stays, so the screen isn't empty
            }
            if (model != null)
            {
                model.invisibilityCount++;
            }
            foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
            {
                r.enabled = false;
            }
            const int h = 3000;
            var tris = new SM64Surface[2];
            tris[0] = new SM64Surface { terrain = 1, v0x = (int)FarX - h, v0y = 0, v0z = -h, v1x = (int)FarX - h, v1y = 0, v1z = h, v2x = (int)FarX + h, v2y = 0, v2z = h };
            tris[1] = new SM64Surface { terrain = 1, v0x = (int)FarX - h, v0y = 0, v0z = -h, v1x = (int)FarX + h, v1y = 0, v1z = h, v2x = (int)FarX + h, v2y = 0, v2z = -h };
            if (MarioMath.Sm64Normal(tris[0]).y < 0)
            {
                (tris[0].v1x, tris[0].v1z, tris[0].v2x, tris[0].v2z) = (tris[0].v2x, tris[0].v2z, tris[0].v1x, tris[0].v1z);
                (tris[1].v1x, tris[1].v1z, tris[1].v2x, tris[1].v2z) = (tris[1].v2x, tris[1].v2z, tris[1].v1x, tris[1].v1z);
            }
            GCHandle g = GCHandle.Alloc(tris, GCHandleType.Pinned);
            var obj = new SM64SurfaceObject { surfaceCount = 2, surfaces = g.AddrOfPinnedObject() };
            floorId = Native.sm64_surface_object_create(ref obj);
            hasFloor = true;
            g.Free();
            marioId = Native.sm64_mario_create(FarX, 0f, 0f);
            if (marioId < 0)
            {
                Plugin.Log.LogWarning("display: Mario couldn't spawn on his floor");
                return;
            }
            renderer = new MarioRenderer("MarioDisplay", transform);
            renderer.Object.transform.localScale = Vector3.one * 1.1f;
        }

        private void Update()
        {
            if (marioId < 0 || renderer == null)
            {
                return;
            }
            accumulator += Time.deltaTime;
            int n = 0;
            while (accumulator >= 1f / Sheets.TickRate && n++ < 4)
            {
                accumulator -= 1f / Sheets.TickRate;
                var inp = new SM64MarioInputs { camLookZ = -1f };
                Native.sm64_mario_tick(marioId, ref inp, ref state, ref renderer.Buffers);
                renderer.TakeTick(new Vector3(state.px, state.py, state.pz));
            }
            renderer.Render(Mathf.Clamp01(accumulator * Sheets.TickRate), Vector3.up); // mesh is local to the display
        }

        private void OnDestroy()
        {
            if (marioId >= 0)
            {
                Native.sm64_mario_delete(marioId);
            }
            if (hasFloor)
            {
                Native.sm64_surface_object_delete(floorId);
            }
            renderer?.Dispose();
        }
    }
}
