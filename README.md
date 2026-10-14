# RTMP Projector

A Windows desktop application that runs its own RTMP server and projects incoming streams fullscreen onto any connected monitor.

## Features

- **RTMP server** on port 1935 (powered by [MediaMTX](https://github.com/bluenviron/mediamtx), bundled with the installer)
- **Stream keys** — each drone/encoder gets its own key. Only streams on a configured key are projected automatically; streams on any other path still connect (so oddly-formatted DJI URLs can be diagnosed) but are just logged with a warning
- **Fullscreen projection** onto the monitor you choose, opening automatically when a stream connects and resuming by itself after drop-outs
- **Stream recording** to a local folder (per key, optional, fragmented MP4)
- **Go Live** — push any drone stream to YouTube, Facebook, Twitch or a custom RTMP(S) server with one button (FFmpeg is bundled). The platform stream key is stored encrypted (Windows DPAPI)
- **NDI output** — send any stream to the local network as an NDI source (the installer offers to install the free NDI Runtime)
- **Auto-update** — checks GitHub releases hourly; downloads are verified against GitHub's SHA-256 before installing
- **System tray** — runs quietly in the background, double-click to open the control panel
- Dark and light themes

---

## Requirements

| Requirement | Notes |
|---|---|
| Windows 10 / 11 (x64) | Required |
| .NET 8 SDK | For building — [download here](https://dotnet.microsoft.com/download/dotnet/8.0) |

No VLC installation needed — the VLC runtime is bundled via NuGet. Release builds also bundle MediaMTX and FFmpeg; a local dev build downloads FFmpeg to `%AppData%\RTMPProjector` the first time you press **Go Live** (or uses one on your PATH).

---

## Building

```powershell
# Clone the repo
git clone https://github.com/bobchomp/rtmp.git
cd rtmp

# Restore packages and build (x64 Release)
dotnet publish src/RTMPProjector.csproj -c Release -r win-x64 --self-contained true -o publish/

# Run
publish\RTMPProjector.exe
```

Or open **RTMPProjector.sln** in Visual Studio 2022 and press F5.

### Icon

The tray/app icon is `src/Assets/tray.ico` — replace it to customise.

---

## First Run

1. Launch **RTMPProjector.exe** — a short setup wizard picks the RTMP port and creates your first stream key.
2. The **Control Panel** opens (closing it asks whether to minimise to the tray or quit).
3. Add more keys any time under **Stream Keys → + Add Key**.
4. Click **Start Server**.
5. Give the publisher the RTMP URL shown on the key detail panel:

   ```
   rtmp://<your-pc-ip>:1935/live/<stream-key>
   ```

6. When they connect, the stream is automatically projected fullscreen on the configured monitor.

---

## OBS / Encoder setup (publisher side)

| OBS Setting | Value |
|---|---|
| Service | Custom |
| Server | `rtmp://192.168.x.x:1935/live` |
| Stream Key | *(your configured key)* |

---

## Architecture

```
┌─────────────────────────────────────┐
│         RTMPProjector.exe           │
│  ┌──────────────┐  ┌─────────────┐  │
│  │  Control     │  │ Projection  │  │
│  │  Panel (WPF) │  │ Window(VLC) │  │
│  └──────┬───────┘  └──────▲──────┘  │
│         │                 │          │
│  ┌──────▼────────────────-┤          │
│  │   StreamMonitorService │          │
│  │  (polls MediaMTX API)  │          │
│  └──────────────────────--┘          │
│         │                            │
│  ┌──────▼──────────────────┐         │
│  │      MediaMtxService    │         │
│  │  (manages subprocess)   │         │
│  └──────────────────────---┘         │
│         │                            │
│  mediamtx.exe  ← RTMP publishers     │
└─────────────────────────────────────┘
```

- **MediaMTX** handles all RTMP protocol details, stream key enforcement and recording.
- **StreamMonitorService** polls `http://127.0.0.1:9997/v3/paths/list` every 2 seconds to detect live streams. The API only listens on localhost.
- Saving settings rewrites the MediaMTX config, which MediaMTX hot-reloads — streams keep running. Only changing a port restarts the server.
- **LibVLCSharp** opens `rtmp://localhost:1935/live/<key>` and renders it fullscreen using VLC's engine.
- **RestreamService** (Go Live) runs `ffmpeg -i rtmp://localhost:1935/live/<key> -c copy -f flv <server>/<stream-key>` per stream key. No re-encoding; if the drone sends no audio a silent AAC track is added, because YouTube and Facebook expect one. While live, FFmpeg restarts automatically after drone drop-outs and network blips.

---

## Settings

Settings are stored at `%AppData%\RTMPProjector\settings.json`.

Everything is editable in the app; the file is only listed here for reference. If it ever becomes unreadable it is kept as `settings.corrupt-<date>.json` rather than overwritten.

| Setting | Default | Description |
|---|---|---|
| `RtmpPort` | 1935 | Port the RTMP server listens on |
| `ApiPort` | 9997 | Internal MediaMTX API port (localhost only) |
| `StreamKeys` | — | Stream keys, with per-key recording, Go Live and NDI options |
| `ProjectionMonitorIndex` | 1 | 0-based monitor index for projection (falls back to the first monitor if missing) |
| `AutoProjectOnConnect` | true | Open the projection window automatically |
| `RecordingPath` | `%AppData%\RTMPProjector\Recordings` | Root folder for recordings |
| `Theme` | `Dark` | `Dark` or `Light` |

---

## Keyboard shortcuts (projection window)

| Key | Action |
|---|---|
| `ESC` | Close projection window |
| `F` | Toggle borderless fullscreen ↔ windowed |
| `M` | Mute / unmute |
| `Tab` | Cycle to the next live stream |
| `1`–`9` | Jump to the Nth live stream |

---

## Firewall

Windows Firewall will prompt you to allow `mediamtx.exe` on first run. Accept the prompt (or add an inbound rule for TCP port 1935 manually) so remote publishers can connect.

---

## License

MIT
