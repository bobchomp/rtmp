using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using LibVLCSharp.Shared;
using RTMPProjector.Interop;

namespace RTMPProjector.Services;

/// <summary>
/// Captures decoded video and audio from a local RTMP stream via a headless LibVLC
/// instance and forwards the raw frames to an NDI sender.
///
/// Video/audio callbacks are wired directly against libvlc.dll via P/Invoke instead
/// of going through LibVLCSharp's wrapper, so we define our own delegate types and
/// avoid dependency on type names that may not be public in a given LibVLCSharp version.
///
/// Pipeline: MediaMTX RTMP → LibVLC headless → BGRA frames + f32l audio → NDI SDK
/// </summary>
public sealed class NdiSenderService : IDisposable
{
    // ── libvlc delegate types (own definitions — no LibVLCSharp dependency) ────

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint VlcVideoFormatCb(ref IntPtr opaque, IntPtr chroma,
        ref uint width, ref uint height, ref uint pitches, ref uint lines);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VlcVideoCleanupCb(IntPtr opaque);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr VlcVideoLockCb(IntPtr opaque, ref IntPtr planes);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VlcVideoDisplayCb(IntPtr opaque, IntPtr picture);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VlcAudioPlayCb(IntPtr data, IntPtr samples, uint count, long pts);

    // ── libvlc P/Invoke ───────────────────────────────────────────────────────

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    private static extern void libvlc_video_set_format_callbacks(
        IntPtr mp,
        VlcVideoFormatCb setup,
        VlcVideoCleanupCb? cleanup);

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    private static extern void libvlc_video_set_callbacks(
        IntPtr mp,
        VlcVideoLockCb @lock,
        IntPtr unlock,
        VlcVideoDisplayCb display,
        IntPtr opaque);

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl,
               CharSet = CharSet.Ansi)]
    private static extern void libvlc_audio_set_format(
        IntPtr mp,
        string format,
        uint rate,
        uint channels);

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    private static extern void libvlc_audio_set_callbacks(
        IntPtr mp,
        VlcAudioPlayCb play,
        IntPtr pause,
        IntPtr resume,
        IntPtr flush,
        IntPtr drain,
        IntPtr opaque);

    // ── Instance state ────────────────────────────────────────────────────────

    private LibVLC?      _vlc;
    private MediaPlayer? _player;
    private Media?       _media;

    private IntPtr _ndiSender = IntPtr.Zero;

    private byte[]?  _ndiNameBytes;
    private GCHandle _ndiNamePin;

    private byte[]?  _frameBuffer;
    private GCHandle _framePin;
    private int      _frameWidth;
    private int      _frameHeight;

    // Frame-rate heartbeat: count frames, log every ~5 s worth
    private long _frameCount;
    private long _frameCountAtLastReport;
    private long _lastReportTick;         // Environment.TickCount64
    private bool _firstFrameLogged;
    private bool _firstAudioLogged;

    // Strong references to delegates (prevent GC while VLC holds function pointers)
    private VlcVideoFormatCb?  _fmtCb;
    private VlcVideoCleanupCb? _fmtClean;
    private VlcVideoLockCb?    _lockCb;
    private VlcVideoDisplayCb? _displayCb;
    private VlcAudioPlayCb?    _audioPlayCb;

    // LogMessage is invoked from VLC's decoder thread — callers must be thread-safe
    public event Action<string>? LogMessage;
    public bool IsRunning { get; private set; }

    // ── Start ─────────────────────────────────────────────────────────────────

    public void Start(string rtmpUrl, string ndiName)
    {
        if (!NdiLib.IsAvailable)
        {
            LogMessage?.Invoke("[NDI] Runtime not found — cannot start NDI output.");
            return;
        }

        Stop();

        _frameCount              = 0;
        _frameCountAtLastReport  = 0;
        _lastReportTick          = Environment.TickCount64;
        _firstFrameLogged        = false;
        _firstAudioLogged        = false;

        try
        {
            // Create the NDI sender
            _ndiNameBytes = System.Text.Encoding.UTF8.GetBytes(ndiName + '\0');
            _ndiNamePin   = GCHandle.Alloc(_ndiNameBytes, GCHandleType.Pinned);

            var create = new NdiLib.SendCreate
            {
                NdiName    = _ndiNamePin.AddrOfPinnedObject(),
                Groups     = IntPtr.Zero,
                ClockVideo = true,
                ClockAudio = false,
            };
            _ndiSender = NdiLib.NDIlib_send_create(ref create);
            if (_ndiSender == IntPtr.Zero)
                throw new InvalidOperationException("NDIlib_send_create returned null.");

            LogMessage?.Invoke($"[NDI] Sender created — name \"{ndiName}\"");

            // Create a headless LibVLC instance and media player
            var vlcDir = Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64");
            Core.Initialize(Directory.Exists(vlcDir) ? vlcDir : null);

            _vlc    = new LibVLC(enableDebugLogs: false);
            _player = new MediaPlayer(_vlc);

            // Log VLC player state changes so we can see if it connects to the stream
            _player.Playing  += (_, _) => LogMessage?.Invoke("[NDI] VLC playing — decoding stream.");
            _player.Stopped  += (_, _) => LogMessage?.Invoke("[NDI] VLC stopped.");
            _player.EncounteredError += (_, _) => LogMessage?.Invoke("[NDI] VLC error — stream unreachable or unsupported.");
            _player.EndReached += (_, _) => LogMessage?.Invoke("[NDI] VLC end-of-stream.");

            // Wire video and audio callbacks directly via libvlc.dll P/Invoke
            var mp = _player.NativeReference;

            _fmtCb     = OnVideoFormat;
            _fmtClean  = OnVideoCleanup;
            _lockCb    = OnVideoLock;
            _displayCb = OnVideoDisplay;

            libvlc_video_set_format_callbacks(mp, _fmtCb, _fmtClean);
            libvlc_video_set_callbacks(mp, _lockCb, IntPtr.Zero, _displayCb, IntPtr.Zero);

            libvlc_audio_set_format(mp, "f32l", 48000, 2);
            _audioPlayCb = OnAudioPlay;
            libvlc_audio_set_callbacks(mp, _audioPlayCb,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            // Open the local RTMP stream
            _media = new Media(_vlc, rtmpUrl, FromType.FromLocation,
                ":network-caching=300",
                ":no-video-title-show",
                ":no-sub-autodetect-file");

            LogMessage?.Invoke($"[NDI] Connecting VLC to {rtmpUrl}");
            _player.Play(_media);
            IsRunning = true;
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[NDI] Start failed: {ex.Message}");
            Cleanup();
        }
    }

    // ── VLC video callbacks ───────────────────────────────────────────────────

    private uint OnVideoFormat(ref IntPtr opaque, IntPtr chroma,
        ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        _frameWidth  = (int)width;
        _frameHeight = (int)height;

        Marshal.WriteByte(chroma, 0, (byte)'B');
        Marshal.WriteByte(chroma, 1, (byte)'G');
        Marshal.WriteByte(chroma, 2, (byte)'R');
        Marshal.WriteByte(chroma, 3, (byte)'A');

        pitches = width * 4;
        lines   = height;

        FreeFramePin();
        _frameBuffer = new byte[width * height * 4];
        _framePin    = GCHandle.Alloc(_frameBuffer, GCHandleType.Pinned);

        LogMessage?.Invoke($"[NDI] Video format set — {width}x{height} BGRA");

        return 1;
    }

    private void OnVideoCleanup(IntPtr opaque) => FreeFramePin();

    private IntPtr OnVideoLock(IntPtr opaque, ref IntPtr planes)
    {
        if (!_framePin.IsAllocated) { planes = IntPtr.Zero; return IntPtr.Zero; }
        var ptr = _framePin.AddrOfPinnedObject();
        planes = ptr;
        return ptr;
    }

    private void OnVideoDisplay(IntPtr opaque, IntPtr picture)
    {
        if (_ndiSender == IntPtr.Zero || picture == IntPtr.Zero) return;

        var frame = new NdiLib.VideoFrameV2
        {
            Xres               = _frameWidth,
            Yres               = _frameHeight,
            FourCC             = NdiLib.FourCC_BGRA,
            FrameRateN         = 30000,
            FrameRateD         = 1001,
            PictureAspectRatio = _frameHeight > 0 ? (float)_frameWidth / _frameHeight : 16f / 9f,
            FrameFormatType    = NdiLib.FrameFormat_Progressive,
            Timecode           = NdiLib.Timecode_SynthesizeFromVideo,
            Data               = picture,
            LineStrideInBytes  = _frameWidth * 4,
            Metadata           = IntPtr.Zero,
            Timestamp          = 0,
        };

        NdiLib.NDIlib_send_send_video_v2(_ndiSender, ref frame);

        var count = Interlocked.Increment(ref _frameCount);

        if (!_firstFrameLogged)
        {
            _firstFrameLogged = true;
            LogMessage?.Invoke($"[NDI] Sending first video frame — {_frameWidth}x{_frameHeight}");
        }

        // Log frame rate every ~5 seconds
        var now = Environment.TickCount64;
        var elapsed = now - Volatile.Read(ref _lastReportTick);
        if (elapsed >= 5000)
        {
            var frames = count - Volatile.Read(ref _frameCountAtLastReport);
            var fps = frames * 1000.0 / elapsed;
            LogMessage?.Invoke($"[NDI] Streaming — {fps:F1} fps  ({count} frames total)");
            Volatile.Write(ref _frameCountAtLastReport, count);
            Volatile.Write(ref _lastReportTick, now);
        }
    }

    // ── VLC audio callback ────────────────────────────────────────────────────

    private void OnAudioPlay(IntPtr data, IntPtr samples, uint count, long pts)
    {
        if (_ndiSender == IntPtr.Zero || samples == IntPtr.Zero) return;

        if (!_firstAudioLogged)
        {
            _firstAudioLogged = true;
            LogMessage?.Invoke("[NDI] Sending audio (f32l 48 kHz stereo).");
        }

        var frame = new NdiLib.AudioFrameInterleaved32F
        {
            SampleRate = 48000,
            NoChannels = 2,
            NoSamples  = (int)count,
            Timecode   = NdiLib.Timecode_SynthesizeFromVideo,
            Data       = samples,
            Timestamp  = 0,
        };

        NdiLib.NDIlib_util_send_send_audio_interleaved_32f(_ndiSender, ref frame);
    }

    // ── Stop / cleanup ────────────────────────────────────────────────────────

    public void Stop()
    {
        if (!IsRunning) return;
        LogMessage?.Invoke($"[NDI] Sender stopped — {_frameCount} frames sent total.");
        Cleanup();
    }

    private void Cleanup()
    {
        IsRunning = false;

        _player?.Stop();
        _player?.Dispose();
        _media?.Dispose();
        _vlc?.Dispose();
        _player = null;
        _media  = null;
        _vlc    = null;

        _fmtCb       = null;
        _fmtClean    = null;
        _lockCb      = null;
        _displayCb   = null;
        _audioPlayCb = null;

        if (_ndiSender != IntPtr.Zero)
        {
            NdiLib.NDIlib_send_destroy(_ndiSender);
            _ndiSender = IntPtr.Zero;
        }

        FreeFramePin();

        if (_ndiNamePin.IsAllocated) _ndiNamePin.Free();
        _ndiNameBytes = null;
    }

    private void FreeFramePin()
    {
        if (_framePin.IsAllocated) _framePin.Free();
        _frameBuffer = null;
    }

    public void Dispose() => Cleanup();
}
