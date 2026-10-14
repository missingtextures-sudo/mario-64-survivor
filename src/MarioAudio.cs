using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace MarioRoR2
{
    /// <summary>
    /// Mario's voice and sounds, mixed by libsm64 from the ROM at 32 kHz stereo. RoR2 ships with Unity audio
    /// switched off (it plays everything through Wwise), so this goes straight to Windows' waveOut.
    /// </summary>
    internal static class MarioAudio
    {
        private const int Buffers = 6;
        private const int MaxFrames = 544 * 2;
        private const int HeaderSize = 48; // WAVEHDR on x64
        private const int FlagsOffset = 24;
        private const uint WHDR_DONE = 1;

        public static float Volume = 0.8f;
        public static long PeakSample; // for the test's sound check
        public static bool Running { get; private set; }

        private static IntPtr device;
        private static readonly IntPtr[] headers = new IntPtr[Buffers];
        private static readonly IntPtr[] data = new IntPtr[Buffers];
        private static readonly int[] frames = new int[Buffers];
        private static readonly bool[] queued = new bool[Buffers];
        private static Thread? thread;

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveFormat
        {
            public ushort wFormatTag, nChannels;
            public uint nSamplesPerSec, nAvgBytesPerSec;
            public ushort nBlockAlign, wBitsPerSample, cbSize;
        }

        [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr h, int deviceId, ref WaveFormat fmt, IntPtr cb, IntPtr inst, int flags);
        [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr h);
        [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr h);

        public static void Start()
        {
            var fmt = new WaveFormat { wFormatTag = 1, nChannels = 2, nSamplesPerSec = (uint)Sheets.AudioRate, wBitsPerSample = 16, nBlockAlign = 4, nAvgBytesPerSec = (uint)Sheets.AudioRate * 4 };
            int err = waveOutOpen(out device, -1, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
            if (err != 0)
            {
                Plugin.Log.LogWarning($"Mario's sound is off: waveOutOpen failed ({err})");
                return;
            }
            for (int i = 0; i < Buffers; i++)
            {
                headers[i] = Marshal.AllocHGlobal(HeaderSize);
                data[i] = Marshal.AllocHGlobal(MaxFrames * 4);
            }
            Running = true;
            thread = new Thread(Loop) { IsBackground = true, Name = "MarioAudio" };
            thread.Start();
            UnityEngine.Application.quitting += Stop;
        }

        /// <summary>Stops the mixer thread before the game unloads (it calls into sm64.dll and winmm).</summary>
        public static void Stop()
        {
            if (!Running)
            {
                return;
            }
            Running = false;
            thread?.Join(500);
            waveOutReset(device);
            for (int i = 0; i < Buffers; i++)
            {
                if (queued[i])
                {
                    waveOutUnprepareHeader(device, headers[i], HeaderSize);
                    queued[i] = false;
                }
            }
            waveOutClose(device);
        }

        private static void Loop()
        {
            short[] buf = new short[MaxFrames * 2];
            while (Running)
            {
                int queuedFrames = 0;
                int free = -1;
                for (int i = 0; i < Buffers; i++)
                {
                    if (queued[i] && (Marshal.ReadInt32(headers[i], FlagsOffset) & WHDR_DONE) != 0)
                    {
                        waveOutUnprepareHeader(device, headers[i], HeaderSize);
                        queued[i] = false;
                    }
                    if (queued[i])
                    {
                        queuedFrames += frames[i];
                    }
                    else if (free < 0)
                    {
                        free = i;
                    }
                }
                if (free >= 0 && queuedFrames < 3000) // about 90 ms ahead
                {
                    uint n = Native.sm64_audio_tick((uint)queuedFrames, 1100, buf);
                    int count = (int)Math.Min(n * 2, MaxFrames); // libsm64 returns half the stereo frames it wrote
                    float vol = Volume;
                    long peak = 0;
                    for (int k = 0; k < count * 2; k++)
                    {
                        int v = (int)(buf[k] * vol);
                        buf[k] = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, v));
                        peak = Math.Max(peak, Math.Abs((long)buf[k]));
                    }
                    if (peak > PeakSample)
                    {
                        PeakSample = peak;
                    }
                    if (count > 0)
                    {
                        Marshal.Copy(buf, 0, data[free], count * 2);
                        byte[] zero = new byte[HeaderSize];
                        Marshal.Copy(zero, 0, headers[free], HeaderSize);
                        Marshal.WriteIntPtr(headers[free], 0, data[free]);
                        Marshal.WriteInt32(headers[free], 8, count * 4);
                        waveOutPrepareHeader(device, headers[free], HeaderSize);
                        waveOutWrite(device, headers[free], HeaderSize);
                        frames[free] = count;
                        queued[free] = true;
                    }
                }
                Thread.Sleep(16);
            }
        }
    }
}
