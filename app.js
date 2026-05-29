(function () {
  'use strict';

  const REPO = 'bobchomp/rtmp';
  const API  = `https://api.github.com/repos/${REPO}/releases/latest`;

  async function loadRelease() {
    try {
      const res  = await fetch(API, { headers: { Accept: 'application/vnd.github+json' } });
      if (!res.ok) throw new Error(`GitHub API ${res.status}`);
      const data = await res.json();

      const tag     = data.tag_name ?? '';
      const version = tag.replace(/^v/, '');

      // Find the Windows installer asset
      const asset = (data.assets ?? []).find(a =>
        a.name && a.name.toLowerCase().endsWith('-setup.exe')
      );
      const url = asset?.browser_download_url ?? data.html_url;

      // Update hero version note
      const heroVer = document.getElementById('hero-version');
      if (heroVer) heroVer.textContent = tag ? `Latest release: ${tag}` : '';

      // Update hero download button href
      const heroBtn = document.getElementById('hero-download-btn');
      if (heroBtn) heroBtn.href = '#download';

      // Update main download button
      const dlBtn = document.getElementById('download-btn');
      if (dlBtn) dlBtn.href = url;

      const dlLabel = document.getElementById('dl-label');
      if (dlLabel) dlLabel.textContent = 'Download for Windows';

      const dlVer = document.getElementById('dl-version');
      if (dlVer && tag) dlVer.textContent = tag;

    } catch (err) {
      // Fallback: just link to the releases page
      const heroVer = document.getElementById('hero-version');
      if (heroVer) heroVer.textContent = '';

      const dlBtn = document.getElementById('download-btn');
      if (dlBtn) dlBtn.href = `https://github.com/${REPO}/releases/latest`;

      const dlLabel = document.getElementById('dl-label');
      if (dlLabel) dlLabel.textContent = 'Download for Windows';
    }
  }

  loadRelease();
})();
