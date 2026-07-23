using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RTMPProjector.Interop;

/// <summary>
/// P/Invoke wrapper for the NDI SDK runtime DLL.
/// Call NdiLib.TryLoad() once at startup; all other members are only safe
/// to call when IsAvailable is true.
/// </summary>
internal static class NdiLib
{
    private const string DllName = "Processing.NDI.Lib.x64";

    public static bool IsAvailable { get; private set; }

    // ── DLL discovery ─────────────────────────────────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryW(string lpFileName);

    /// <summary>
    /// Locates Processing.NDI.Lib.x64.dll, pre-loads it so subsequent DllImport
    /// calls resolve correctly, then calls NDIlib_initialize().
    /// Returns true if NDI is available and ready to use.
    /// </summary>
    public static bool TryLoad()
    {
        try
        {
            var path = FindDllPath();
            if (path == null) return false;

            var handle = LoadLibraryW(path);
            if (handle == IntPtr.Zero) return false;

            IsAvailable = NDIlib_initialize();
            return IsAvailable;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindDllPath()
    {
        const string DllFile = "Processing.NDI.Lib.x64.dll";

        // 1. Registry (NDI Runtime installer writes this)
        foreach (var ver in new[] { "v6", "v5", "v4" })
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\NDI\Runtime\{ver}");
                if (key == null) continue;
                var dir = key.GetValue(null) as string
                       ?? key.GetValue("InstallDir") as string;
                if (dir != null)
                {
                    var p = Path.Combine(dir, DllFile);
                    if (File.Exists(p)) return p;
                }
            }
            catch { }
        }

        // 2. Well-known install paths
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var candidate in new[]
        {
            Path.Combine(pf, "NDI", "NDI 6 Runtime", "v6", DllFile),
            Path.Combine(pf, "NDI", "NDI 5 Runtime", "v5", DllFile),
            Path.Combine(pf, "NewTek", "NDI 5 Runtime", "v5", DllFile),
            Path.Combine(pf, "NewTek", "NDI 4 Runtime", "v4", DllFile),
        })
        {
            if (File.Exists(candidate)) return candidate;
        }

        // 3. Already on PATH (LoadLibrary will find it)
        return null;
    }

    // ── Constants ─────────────────────────────────────────────────────────────

    /// <summary>BGRA packed as little-endian FourCC: 'B','G','R','A' = 0x41524742</summary>
    public const uint FourCC_BGRA = 0x41524742;

    public const int FrameFormat_Progressive = 1;

    /// <summary>Timecode sentinel: synthesise from video clock.</summary>
    public const long Timecode_SynthesizeFromVideo = unchecked((long)0x8000000000000000L);

    // ── Structs ───────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct SendCreate
    {
        public IntPtr NdiName;   // UTF-8 null-terminated string
        public IntPtr Groups;    // NULL = default broadcast group
        [MarshalAs(UnmanagedType.I1)] public bool ClockVideo;
        [MarshalAs(UnmanagedType.I1)] public bool ClockAudio;
    }

    /// <summary>NDIlib_video_frame_v2_t — matches NDI SDK 5.x / 6.x on Windows x64.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VideoFrameV2
    {
        public int   Xres;
        public int   Yres;
        public uint  FourCC;
        public int   FrameRateN;
        public int   FrameRateD;
        public float PictureAspectRatio;
        public int   FrameFormatType;
        public long  Timecode;              // int64_t — .NET Sequential aligns to 8
        public IntPtr Data;                 // uint8_t*
        public int   LineStrideInBytes;
        private int  _pad;                  // matches union padding before p_metadata
        public IntPtr Metadata;             // const char*  (NULL)
        public long  Timestamp;             // int64_t      (0 = use SDK clock)
    }

    /// <summary>NDIlib_audio_frame_interleaved_32f_t — interleaved float stereo.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AudioFrameInterleaved32F
    {
        public int   SampleRate;
        public int   NoChannels;
        public int   NoSamples;
        public long  Timecode;              // int64_t — .NET Sequential aligns to 8
        public IntPtr Data;                 // float* (interleaved)
        public long  Timestamp;             // NDI 6.x extension (0 = SDK clock)
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────────

    [DllImport(DllName, EntryPoint = "NDIlib_initialize",
               CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool NDIlib_initialize();

    [DllImport(DllName, EntryPoint = "NDIlib_destroy",
               CallingConvention = CallingConvention.Cdecl)]
    public static extern void NDIlib_destroy();

    [DllImport(DllName, EntryPoint = "NDIlib_send_create",
               CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr NDIlib_send_create(ref SendCreate createSettings);

    [DllImport(DllName, EntryPoint = "NDIlib_send_destroy",
               CallingConvention = CallingConvention.Cdecl)]
    public static extern void NDIlib_send_destroy(IntPtr instance);

    [DllImport(DllName, EntryPoint = "NDIlib_send_send_video_v2",
               CallingConvention = CallingConvention.Cdecl)]
    public static extern void NDIlib_send_send_video_v2(IntPtr instance, ref VideoFrameV2 videoData);

    [DllImport(DllName, EntryPoint = "NDIlib_util_send_send_audio_interleaved_32f",
               CallingConvention = CallingConvention.Cdecl)]
    public static extern void NDIlib_util_send_send_audio_interleaved_32f(
        IntPtr instance, ref AudioFrameInterleaved32F audioData);
}
