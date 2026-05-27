// ── RTMP Projector Live Player — Configuration ────────────────────────────
// Edit this file to match your setup, then redeploy to Cloudflare Pages.

window.STREAM_CONFIG = {
  // Full HLS URL — copy from RTMP Projector > Settings > Web Stream > "STREAM URL"
  // Example: 'https://stream.droneoutings.co.uk/live/abc123.../index.m3u8'
  hlsUrl: '',

  // Title shown in the player and browser tab
  title: 'Drone Outings Live',

  // Leave empty for public access.
  // Set a password to show a PIN gate before the player loads.
  // Must match the "Website Password" in RTMP Projector > Settings > Web Stream.
  password: '',

  // Seconds between retry attempts when the stream is offline
  retryInterval: 10,
}
