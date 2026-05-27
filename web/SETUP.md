# Live Player — Setup Guide

This folder is the source for **live.droneoutings.co.uk**.  
It is a static site deployed to Cloudflare Pages (or Vercel).

The stream is served via a **Cloudflare Tunnel** running on your PC — no port
forwarding, no static IP needed.

---

## Architecture

```
Your PC (RTMP Projector)
  └─ MediaMTX  →  HLS on localhost:8888
       └─ cloudflared tunnel  →  stream.droneoutings.co.uk
                                        ↑
                               live.droneoutings.co.uk
                               (this repo on Cloudflare Pages)
                               embeds HLS.js → fetches HLS from stream.*
```

---

## Step 1 — Enable HLS in RTMP Projector

1. Open RTMP Projector → **Settings** tab → **WEB STREAM** section.
2. Check **Enable HLS output**.
3. Leave HLS Port as **8888** (or change it — just keep it consistent below).
4. Click **Save Settings** and start the server.

---

## Step 2 — Install cloudflared on your PC

1. Download the Windows installer from  
   <https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/downloads/>
2. Run the installer (or extract `cloudflared.exe` to `C:\cloudflared\`).

---

## Step 3 — Create your tunnel

Open **PowerShell** or **Command Prompt**:

```powershell
# Log in to Cloudflare (opens browser)
cloudflared tunnel login

# Create a named tunnel
cloudflared tunnel create rtmp-projector

# Note the Tunnel ID printed — you'll need it in Step 4.
```

---

## Step 4 — Configure the tunnel

Copy `cloudflared/tunnel.yml` from this repo to  
`C:\Users\YOUR-USERNAME\.cloudflared\config.yml`  
and edit it:

```yaml
tunnel: YOUR-TUNNEL-ID          # from Step 3
credentials-file: C:\Users\YOUR-USERNAME\.cloudflared\YOUR-TUNNEL-ID.json

ingress:
  - hostname: stream.droneoutings.co.uk
    service: http://localhost:8888
  - service: http_status:404
```

---

## Step 5 — Add a DNS record in Cloudflare

```powershell
cloudflared tunnel route dns rtmp-projector stream.droneoutings.co.uk
```

This creates a `CNAME` in your Cloudflare DNS automatically.

---

## Step 6 — Run the tunnel

```powershell
cloudflared tunnel run rtmp-projector
```

To start it automatically with Windows, install it as a service:

```powershell
cloudflared service install
```

---

## Step 7 — Configure the website

Edit `config.js` in this folder:

```js
window.STREAM_CONFIG = {
  // Paste the "STREAM URL" from RTMP Projector > Settings > Web Stream
  hlsUrl: 'https://stream.droneoutings.co.uk/live/YOUR-STREAM-KEY/index.m3u8',

  title: 'Drone Outings Live',

  // Leave blank for public, or set a PIN to require a password
  // Must match "Website Password" in RTMP Projector > Settings > Web Stream
  password: '',

  retryInterval: 10,
}
```

You can find your stream key in RTMP Projector → the stream key list on the
main tab. The **STREAM URL** in Settings → Web Stream shows the full URL.

---

## Step 8 — Deploy to Cloudflare Pages

1. Push this `web/` folder as its own GitHub repository (or keep it in this
   repo and set the root directory to `web` in Cloudflare Pages).
2. In Cloudflare dashboard → **Pages** → **Create project** → Connect to your repo.
3. Build settings:
   - **Build command**: *(leave empty)*
   - **Build output directory**: `/` (root)
4. Deploy. Your site will be at `YOUR-PROJECT.pages.dev`.
5. Add a custom domain: `live.droneoutings.co.uk`.

---

## Password protection

When `password` is set in `config.js`, viewers see a password gate before the
player loads.

- The password is stored in `sessionStorage` — closing the tab requires
  re-entry; staying on the page does not.
- The same password should be set in RTMP Projector → Settings → Web Stream
  (so you can see both in one place). The app doesn't enforce it on the stream
  itself — it's a website-level gate.

To change the password: update `config.js` and redeploy.

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| Player shows "Offline" immediately | Check cloudflared is running and MediaMTX HLS is enabled |
| CORS error in browser console | Make sure MediaMTX was restarted after enabling HLS in RTMP Projector |
| stream.droneoutings.co.uk returns 404 | The tunnel isn't running — start it with `cloudflared tunnel run rtmp-projector` |
| Video loads but no picture | Check OBS / your encoder is pushing to RTMP Projector |
| Password gate doesn't appear | `config.js` may be cached — force-refresh or redeploy Pages |
