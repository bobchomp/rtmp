# RTMP Projector — notes for Claude

Windows desktop app (C# WPF, .NET 8, x64) that runs a local RTMP server (MediaMTX)
for DJI drones / encoders, projects streams fullscreen (LibVLC), records them,
sends them out as NDI, and pushes them to YouTube/Facebook/etc. ("Go Live", FFmpeg).
Auto-updates from GitHub releases via an Inno Setup installer.

## Working rules (from the repo owner)

- **Merge every change into `main` automatically.** Commit on the working branch,
  open a PR, and merge it straight away — don't wait to be asked.
- **Compile before merging.** Every change must build with 0 errors (see below).
- Report findings and ask before making large or behaviour-changing edits
  (e.g. a codebase review: report first, fix after approval).
- The owner is not a developer: explain changes in plain language, by what the
  user sees, not by class names.

## Building

On Windows:

```powershell
dotnet build src/RTMPProjector.csproj -c Release -r win-x64
```

On Linux (e.g. a Claude cloud session) WPF can be compiled but not run:

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
bash dotnet-install.sh --channel 8.0 --install-dir "$HOME/.dotnet"
"$HOME/.dotnet/dotnet" build src/RTMPProjector.csproj -c Release -r win-x64 -p:EnableWindowsTargeting=true
```

(The cloud environment must allow `dot.net`, `builds.dotnet.microsoft.com` and
`api.nuget.org`.) There is no test project — the build is the check, and the
**Build** workflow compiles every PR on a Windows runner.

## Releasing

Push a tag `vX.Y.Z` (or `vX.Y.Z-beta.N` for a pre-release, which the in-app
updater ignores). `.github/workflows/release.yml` publishes the app, bundles
MediaMTX + FFmpeg (checksum-verified) and the VC++ runtime, builds the installer,
and publishes the GitHub release only after all files are uploaded.

## Layout

- `src/App.xaml.cs` — startup, single instance, tray, update state machine, shutdown
- `src/MainWindow.xaml` / `ViewModels/MainViewModel.cs` — control panel
- `src/ProjectionWindow.xaml.cs` — fullscreen LibVLC player
- `src/Services/` — `MediaMtxService` (server + config), `StreamMonitorService`
  (polls MediaMTX API), `RestreamService` (Go Live / FFmpeg), `NdiSenderService`,
  `UpdaterService`, `SettingsService`, `DownloadVerifier`
- `src/Themes/Colours.xaml` — the single colour palette (light only)
- `installer/RTMPProjector.iss` — Inno Setup script
- Root `index.html` / `style.css` / `app.js` — marketing website (Vercel)

## Gotchas

- WPF `ImplicitUsings` do **not** include `System.IO` or `System.Net.Http`; add them explicitly.
  Watch for `System.Windows` vs `System.Windows.Forms` name clashes (`MessageBox`, `Screen`, `Application`).
- MediaMTX rejects unknown config fields and exits — only use fields the bundled
  (latest) version supports. Quote every YAML value (`MediaMtxService.Quote`).
- Saving settings hot-reloads the MediaMTX config file; only a port change restarts
  the server. Don't reintroduce full restarts on save — they drop live streams.
- Streams on paths that match no stream key are accepted (DJI quirks) but must
  never be auto-projected.
- Custom `ProgressBar` templates need a `PART_Track` element or the bar never fills.
- In Inno Setup strings a literal `{` is written `{{`; `}` needs no escaping.
- The Go Live stream key is stored DPAPI-encrypted (`RestreamStreamKeyProtected`);
  never write it to settings or logs in plain text.
