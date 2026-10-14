using System;
using System.IO;
using System.Runtime.InteropServices;

namespace MarioRoR2
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct SM64Surface
    {
        public short type;
        public short force;
        public ushort terrain;
        public int v0x, v0y, v0z;
        public int v1x, v1y, v1z;
        public int v2x, v2y, v2z;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SM64MarioInputs
    {
        public float camLookX, camLookZ;
        public float stickX, stickY;
        public byte buttonA, buttonB, buttonZ;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SM64ObjectTransform
    {
        public float px, py, pz;
        public float rx, ry, rz;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SM64SurfaceObject
    {
        public SM64ObjectTransform transform;
        public uint surfaceCount;
        public IntPtr surfaces;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SM64MarioState
    {
        public float px, py, pz;
        public float vx, vy, vz;
        public float faceAngle;
        public float forwardVelocity;
        public short health;
        public uint action;
        public int animID;
        public short animFrame;
        public uint flags;
        public uint particleFlags;
        public short invincTimer;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SM64MarioGeometryBuffers
    {
        public IntPtr position;
        public IntPtr normal;
        public IntPtr color;
        public IntPtr uv;
        public ushort numTrianglesUsed;
    }

    /// <summary>libsm64 (sm64.dll), which Melty builds on the player's PC from the SM64 decompilation.</summary>
    internal static class Native
    {
        public const int TextureWidth = 64 * 11;
        public const int TextureHeight = 64;
        public const int MaxTriangles = 1024;
        private const string Lib = "sm64";

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void DebugPrint([MarshalAs(UnmanagedType.LPStr)] string text);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);

        /// <summary>Loads sm64.dll by full path (its folder is searched for its own dependencies), so DllImport("sm64") binds to it.</summary>
        public static bool Load(string dllPath, out string error)
        {
            const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x8;
            if (!File.Exists(dllPath))
            {
                error = $"{dllPath} does not exist";
                return false;
            }
            IntPtr h = LoadLibraryExW(dllPath, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
            error = h == IntPtr.Zero ? $"LoadLibrary({dllPath}) failed, Windows error {Marshal.GetLastWin32Error()}" : "";
            return h != IntPtr.Zero;
        }

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void sm64_register_debug_print_function(DebugPrint fn);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void sm64_global_init(IntPtr rom, byte[] outTexture);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void sm64_audio_init(IntPtr rom);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint sm64_audio_tick(uint numQueuedSamples, uint numDesiredSamples, short[] audioBuffer);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int sm64_mario_create(float x, float y, float z);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void sm64_mario_tick(int marioId, ref SM64MarioInputs inputs, ref SM64MarioState outState, ref SM64MarioGeometryBuffers outBuffers);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void sm64_mario_delete(int marioId);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void sm64_set_mario_position(int marioId, float x, float y, float z);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void sm64_set_mario_velocity(int marioId, float x, float y, float z);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void sm64_set_mario_forward_velocity(int marioId, float v);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void sm64_set_mario_faceangle(int marioId, float y);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void sm64_set_mario_action(int marioId, uint action);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void sm64_set_mario_water_level(int marioId, int level);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void sm64_set_mario_gas_level(int marioId, int level);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void sm64_set_mario_health(int marioId, ushort health);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void sm64_mario_take_damage(int marioId, uint damage, uint subtype, float x, float y, float z);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void sm64_mario_kill(int marioId);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool sm64_mario_attack(int marioId, float x, float y, float z, float hitboxHeight);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint sm64_surface_object_create(ref SM64SurfaceObject obj);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void sm64_surface_object_delete(uint objectId);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern float sm64_surface_find_floor_height(float x, float y, float z);
    }
}
