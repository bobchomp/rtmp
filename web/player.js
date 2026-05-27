(function () {
  'use strict';

  const cfg = window.STREAM_CONFIG || {};
  const hlsUrl = cfg.hlsUrl || '';
  const password = cfg.password || '';
  const title = cfg.title || 'Live Stream';
  const retryMs = Math.max(5, cfg.retryInterval || 10) * 1000;

  document.title = title;
  document.getElementById('gate-title').textContent = title;
  document.getElementById('hud-title').textContent = title;

  let hls = null;
  let retryTimeout = null;
  let countdownInterval = null;

  // ── Screen management ──────────────────────────────────────────────────────

  function show(id) {
    document.querySelectorAll('.screen').forEach(s => s.classList.remove('active'));
    document.getElementById(id).classList.add('active');
  }

  // ── HUD visibility ─────────────────────────────────────────────────────────

  const playerScreen = document.getElementById('player-screen');
  let hudTimer = null;

  function showHud() {
    playerScreen.classList.add('hud-visible');
    clearTimeout(hudTimer);
    hudTimer = setTimeout(() => playerScreen.classList.remove('hud-visible'), 3000);
  }

  playerScreen.addEventListener('mousemove', showHud);
  playerScreen.addEventListener('touchstart', showHud, { passive: true });
  showHud();

  // ── Auth ───────────────────────────────────────────────────────────────────

  function isAuthenticated() {
    if (!password) return true;
    return sessionStorage.getItem('rtmp_auth') === btoa(password);
  }

  function tryAuth() {
    const input = document.getElementById('gate-input');
    const err = document.getElementById('gate-error');
    if (input.value === password) {
      sessionStorage.setItem('rtmp_auth', btoa(password));
      err.textContent = '';
      startPlayer();
    } else {
      err.textContent = 'Incorrect password — try again.';
      input.value = '';
      input.focus();
    }
  }

  document.getElementById('gate-btn').addEventListener('click', tryAuth);
  document.getElementById('gate-input').addEventListener('keydown', e => {
    if (e.key === 'Enter') tryAuth();
  });

  // ── Offline / retry ────────────────────────────────────────────────────────

  function goOffline() {
    if (hls) { hls.destroy(); hls = null; }
    show('offline');
    scheduleRetry();
  }

  function scheduleRetry() {
    clearTimeout(retryTimeout);
    clearInterval(countdownInterval);

    let secs = Math.round(retryMs / 1000);
    const countEl = document.getElementById('retry-count');
    countEl.textContent = secs;

    countdownInterval = setInterval(() => {
      secs = Math.max(0, secs - 1);
      countEl.textContent = secs;
    }, 1000);

    retryTimeout = setTimeout(() => {
      clearInterval(countdownInterval);
      startPlayer();
    }, retryMs);
  }

  // ── HLS player ─────────────────────────────────────────────────────────────

  function startPlayer() {
    if (!hlsUrl) { goOffline(); return; }

    const video = document.getElementById('video');

    if (typeof Hls !== 'undefined' && Hls.isSupported()) {
      if (hls) hls.destroy();
      hls = new Hls({
        enableWorker: true,
        lowLatencyMode: true,
        backBufferLength: 8,
        manifestLoadingTimeOut: 8000,
        manifestLoadingMaxRetry: 0,
        levelLoadingMaxRetry: 0,
      });
      hls.loadSource(hlsUrl);
      hls.attachMedia(video);

      hls.on(Hls.Events.MANIFEST_PARSED, () => {
        show('player-screen');
        showHud();
        video.play().catch(() => {
          // Autoplay blocked — unmute gesture needed
          video.muted = true;
          video.play().catch(() => {});
        });
      });

      hls.on(Hls.Events.ERROR, (_, data) => {
        if (data.fatal) {
          hls.destroy();
          hls = null;
          goOffline();
        }
      });

    } else if (video.canPlayType('application/vnd.apple.mpegurl')) {
      // Safari native HLS
      video.src = hlsUrl;
      const onMeta = () => {
        show('player-screen');
        showHud();
        video.play().catch(() => {});
        video.removeEventListener('error', onErr);
      };
      const onErr = () => {
        video.removeEventListener('loadedmetadata', onMeta);
        goOffline();
      };
      video.addEventListener('loadedmetadata', onMeta, { once: true });
      video.addEventListener('error', onErr, { once: true });

    } else {
      goOffline();
    }
  }

  // ── Controls ───────────────────────────────────────────────────────────────

  let muted = true;
  const video = document.getElementById('video');

  document.getElementById('mute-btn').addEventListener('click', () => {
    muted = !muted;
    video.muted = muted;
    document.getElementById('mute-btn').textContent = muted ? '🔇' : '🔊';
  });

  document.getElementById('fs-btn').addEventListener('click', () => {
    const el = document.getElementById('player-screen');
    if (!document.fullscreenElement) {
      el.requestFullscreen().catch(() => {});
    } else {
      document.exitFullscreen();
    }
  });

  // Double-tap fullscreen on mobile
  let lastTap = 0;
  playerScreen.addEventListener('touchend', () => {
    const now = Date.now();
    if (now - lastTap < 300) {
      const el = document.getElementById('player-screen');
      if (!document.fullscreenElement) el.requestFullscreen().catch(() => {});
      else document.exitFullscreen();
    }
    lastTap = now;
  });

  // ── Entry point ────────────────────────────────────────────────────────────

  if (isAuthenticated()) {
    startPlayer();
  } else {
    show('gate');
    setTimeout(() => document.getElementById('gate-input').focus(), 100);
  }

})();
