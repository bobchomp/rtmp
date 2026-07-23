using System.IO;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using RTMPProjector.Interop;

namespace RTMPProjector.Services;

/// <summary>
/// Captures decoded video and audio from a local RTMP stream via a headless LibVLC
/// instance and forwards the raw frames to an NDI sender.
///
/// Pipeline: MediaMTX RTMP → LibVLC headless → BGRA frames + f32l audio → NDI SDK
/// </summary>
public sealed class NdiSenderService : IDisposable
{
    private LibVLC?      _vlc;
    private MediaPlayer? _player;
    private Media?       _media;

    private IntPtr _ndiSender = IntPtr.Zero;

    // NDI name string — must stay pinned for the lifetime of the sender
    private byte[]?  _ndiNameBytes;
    private GCHandle _ndiNamePin;

    // Frame buffer — pinned so VLC can write directly into it
    private byte[]?  _frameBuffer;
    private GCHandle _framePin;
    private int      _frameWidth;
    private int      _frameHeight;

    // Keep delegate references alive (prevent GC)
    private LibVLCVideoFormatCb?  _fmtCb;
    private LibVLCVideoCleanupCb? _fmtClean;
    private LibVLCVideoLockCb?    _lockCb;
    private LibVLCVideoDisplayCb? _displayCb;
    private LibVLCAudioPlayCb?    _audioPlayCb;

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

        try
        {
            // ── Create NDI sender ──────────────────────────────────────────────

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

            // ── Create headless VLC player ─────────────────────────────────────

            var vlcDir = Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64");
            Core.Initialize(Directory.Exists(vlcDir) ? vlcDir : null);

            _vlc    = new LibVLC(enableDebugLogs: false);
            _player = new MediaPlayer(_vlc);

            // Video callbacks — VLC calls lock/display once per decoded frame
            _fmtCb    = OnVideoFormat;
            _fmtClean = OnVideoCleanup;
            _lockCb   = OnVideoLock;
            _displayCb = OnVideoDisplay;

            _player.SetVideoFormatCallback(_fmtCb, _fmtClean);
            _player.SetVideoCallbacks(_lockCb, null, _displayCb);

            // Audio callbacks — VLC delivers decoded f32l samples directly
            _player.SetAudioFormat("f32l", 48000, 2);
            _audioPlayCb = OnAudioPlay;
            _player.SetAudioCallbacks(_audioPlayCb, null, null, null, null);

            // Open the local RTMP stream with low latency settings
            _media = new Media(_vlc, rtmpUrl, FromType.FromLocation,
                ":network-caching=300",
                ":no-video-title-show",
                ":no-sub-autodetect-file");

            _player.Play(_media);
            IsRunning = true;
            LogMessage?.Invoke($"[NDI] Sender started — \"{ndiName}\"  ←  {rtmpUrl}");
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[NDI] Start failed: {ex.Message}");
            Cleanup();
        }
    }

    // ── VLC video callbacks ───────────────────────────────────────────────────

    private uint OnVideoFormat(ref IntPtr opaque, ref uint chroma, ref uint width,
                               ref uint height, ref uint pitches, ref uint lines)
    {
        _frameWidth  = (int)width;
        _frameHeight = (int)height;

        // Request BGRA: VLC decodes into this format which NDI accepts natively
        chroma  = NdiLib.FourCC_BGRA;
        pitches = width * 4;
        lines   = height;

        FreeFramePin();
        _frameBuffer = new byte[width * height * 4];
        _framePin    = GCHandle.Alloc(_frameBuffer, GCHandleType.Pinned);

        return 1; // one plane
    }

    private void OnVideoCleanup(IntPtr opaque)
    {
        FreeFramePin();
    }

    private IntPtr OnVideoLock(IntPtr opaque, ref IntPtr planes)
    {
        if (!_framePin.IsAllocated)
        {
            planes = IntPtr.Zero;
            return IntPtr.Zero;
        }
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
    }

    // ── VLC audio callback ────────────────────────────────────────────────────

    private void OnAudioPlay(IntPtr data, IntPtr samples, uint count, long pts)
    {
        if (_ndiSender == IntPtr.Zero || samples == IntPtr.Zero) return;

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
        LogMessage?.Invoke("[NDI] Sender stopped.");
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

        // Clear delegate refs after VLC is gone (no more callbacks after Dispose)
        _fmtCb    = null;
        _fmtClean = null;
        _lockCb   = null;
        _displayCb = null;
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
