# RTMP Projector

A Windows desktop application that runs its own RTMP server and projects incoming streams fullscreen onto any connected monitor.

## Features

- **RTMP server** on port 1935 (powered by [MediaMTX](https://github.com/bluenviron/mediamtx), auto-downloaded on first launch)
- **Stream key authentication** — only configured keys are accepted
- **Fullscreen projection** onto any monitor you choose, with auto-detect when a stream connects
- **Stream recording** to a local folder (per key, optional)
- **Restream / forward** to any other RTMP endpoint such as Twitch or YouTube (requires FFmpeg in PATH)
- **System tray** — runs quietly in the background, double-click to open the control panel
- Dark-themed control panel UI

---

## Requirements

| Requirement | Notes |
|---|---|
| Windows 10 / 11 (x64) | Required |
| .NET 8 SDK | For building — [download here](https://dotnet.microsoft.com/download/dotnet/8.0) |
| FFmpeg in PATH | Only required if you enable the **Restream** feature |

No VLC installation needed — the VLC runtime is bundled via NuGet.

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

### Icon (optional)

Place a `tray.ico` file at `src/Assets/tray.ico` before building to customise the system-tray icon. Without it the app runs fine but shows a default icon.

---

## First Run

1. Launch **RTMPProjector.exe** — it appears in the system tray.
2. Double-click the tray icon to open the **Control Panel**.
3. Go to **Stream Keys → + Add Key** and create a key with a friendly name.
4. Click **Start Server** — MediaMTX is downloaded automatically (~10 MB) if not already present.
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

- **MediaMTX** handles all RTMP protocol details, stream key enforcement, recording, and restream delegation.
- **StreamMonitorService** polls `http://localhost:9997/v3/paths/list` every 2 seconds to detect live streams.
- **LibVLCSharp** opens `rtmp://localhost:1935/live/<key>` and renders it fullscreen using VLC's engine.

---

## Settings

Settings are stored at `%AppData%\RTMPProjector\settings.json`.

| Setting | Default | Description |
|---|---|---|
| `rtmpPort` | 1935 | Port the RTMP server listens on |
| `apiPort` | 9997 | Internal MediaMTX API port |
| `projectionMonitorIndex` | 1 | 0-based monitor index |
| `autoProjectOnConnect` | true | Open projection window automatically |
| `recordingPath` | `%AppData%\RTMPProjector\Recordings` | Root folder for recordings |
| `startMinimized` | true | Start in tray |
| `autoStartServer` | false | Start the RTMP server immediately on launch |

---

## Keyboard shortcuts (projection window)

| Key | Action |
|---|---|
| `ESC` | Close projection window |
| `F` | Toggle borderless fullscreen ↔ windowed |
| Mouse move | Show HUD for 3 seconds |

---

## Firewall

Windows Firewall will prompt you to allow `mediamtx.exe` on first run. Accept the prompt (or add an inbound rule for TCP port 1935 manually) so remote publishers can connect.

---

## License

MIT
