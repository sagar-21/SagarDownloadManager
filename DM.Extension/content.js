// ─────────────────────────────────────────────────────────────────────────────
// content.js  —  Injected into every page.
//
// Does two things:
//   1. Listens for DM_MEDIA_ADDED messages from background.js (webRequest hits)
//      and shows/updates the floating download panel.
//   2. Scans the DOM for <video> elements and adds a small download badge on
//      each one. Clicking the badge shows the panel.
//
// The panel is a fixed-position overlay (bottom-right, z-index max) that lists
// all detected media for the current page with one-click download buttons.
// ─────────────────────────────────────────────────────────────────────────────

(function () {
  // Guard: run only once per frame
  if (window.__dmExtensionLoaded) return;
  window.__dmExtensionLoaded = true;

  let panelEl   = null;
  let mediaItems = [];   // { url, label, pageUrl }

  // ── Panel DOM ───────────────────────────────────────────────────────────────

  function createPanel() {
    if (panelEl) return;

    panelEl = document.createElement('div');
    panelEl.id        = 'dm-panel';
    panelEl.className = 'dm-panel dm-panel-hidden';
    panelEl.innerHTML = `
      <div class="dm-panel-header">
        <span class="dm-panel-title">⬇ Download Manager</span>
        <span class="dm-panel-count" id="dm-count">0</span>
        <button class="dm-panel-dismiss" id="dm-dismiss" title="Dismiss">✕</button>
      </div>
      <div class="dm-panel-body" id="dm-body"></div>
      <div class="dm-panel-status" id="dm-status"></div>
    `;
    document.body.appendChild(panelEl);

    document.getElementById('dm-dismiss').addEventListener('click', hidePanel);

    // Slide in after a tick so the CSS transition fires
    requestAnimationFrame(() => {
      requestAnimationFrame(() => panelEl.classList.remove('dm-panel-hidden'));
    });
  }

  function showPanel() {
    if (!panelEl) createPanel();
    panelEl.classList.remove('dm-panel-hidden');
    renderItems();
  }

  function hidePanel() {
    panelEl?.classList.add('dm-panel-hidden');
  }

  function renderItems() {
    if (!panelEl) return;

    const countEl = document.getElementById('dm-count');
    const bodyEl  = document.getElementById('dm-body');
    if (!countEl || !bodyEl) return;

    countEl.textContent = mediaItems.length;
    bodyEl.innerHTML    = '';

    if (mediaItems.length === 0) {
      bodyEl.innerHTML = '<div class="dm-empty">No media detected yet.</div>';
      return;
    }

    mediaItems.forEach((item, idx) => {
      const row = document.createElement('div');
      row.className = 'dm-media-row';
      const name = item.url.split('/').pop().split('?')[0] || 'media';
      const isStream = item.url.endsWith('.m3u8') || item.url.endsWith('.mpd');
      row.innerHTML = `
        <span class="dm-media-label ${isStream ? 'dm-label-stream' : 'dm-label-direct'}">${item.label}</span>
        <span class="dm-media-name" title="${escapeHtml(item.url)}">${escapeHtml(name)}</span>
        <button class="dm-download-btn" data-idx="${idx}" title="Send to Download Manager">⬇</button>
      `;
      bodyEl.appendChild(row);
    });

    bodyEl.querySelectorAll('.dm-download-btn').forEach(btn => {
      btn.addEventListener('click', () => {
        const item = mediaItems[Number(btn.dataset.idx)];
        if (!item) return;
        sendToApp(item.url, item.pageUrl ?? location.href, document.title);
      });
    });
  }

  function setStatus(msg, isError) {
    const el = document.getElementById('dm-status');
    if (!el) return;
    el.textContent  = msg;
    el.style.color  = isError ? '#ff6b6b' : '#7EC8E3';
    el.style.display = msg ? 'block' : 'none';
    if (!isError) setTimeout(() => { if (el.textContent === msg) el.style.display = 'none'; }, 2500);
  }

  function escapeHtml(s) {
    return s.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
  }

  // ── Download trigger ────────────────────────────────────────────────────────

  function sendToApp(url, pageUrl, title) {
    setStatus('Sending…', false);
    chrome.runtime.sendMessage(
      { type: 'DM_SEND_TO_APP', url, pageUrl, title },
      (res) => {
        if (chrome.runtime.lastError || !res?.ok) {
          setStatus('App not running — is Download Manager open?', true);
        } else {
          setStatus('Sent to Download Manager ✓', false);
        }
      }
    );
  }

  // ── Background → content messaging ─────────────────────────────────────────

  chrome.runtime.onMessage.addListener((msg) => {
    if (msg.type !== 'DM_MEDIA_ADDED') return;
    mediaItems = msg.items ?? [];
    if (mediaItems.length > 0) showPanel();
    else renderItems();
  });

  // ── Load existing media for this tab on script injection ────────────────────

  chrome.runtime.sendMessage({ type: 'DM_GET_MEDIA' }, (res) => {
    if (chrome.runtime.lastError) return;
    if (res?.items?.length > 0) {
      mediaItems = res.items;
      showPanel();
    }
  });

  // ── Video badge injection ───────────────────────────────────────────────────

  function addBadge(video) {
    if (video.dataset.dmBadged) return;
    video.dataset.dmBadged = '1';

    const badge = document.createElement('button');
    badge.className   = 'dm-video-badge';
    badge.textContent = '⬇';
    badge.title       = 'Download with DM';
    document.body.appendChild(badge);

    function updatePos() {
      const r = video.getBoundingClientRect();
      if (r.width < 10 || r.height < 10) { badge.style.display = 'none'; return; }
      badge.style.display = 'block';
      badge.style.left    = (r.left + window.scrollX + 8) + 'px';
      badge.style.top     = (r.top  + window.scrollY + 8) + 'px';
    }
    updatePos();

    let af;
    window.addEventListener('scroll', () => { cancelAnimationFrame(af); af = requestAnimationFrame(updatePos); }, { passive: true });
    window.addEventListener('resize', () => { cancelAnimationFrame(af); af = requestAnimationFrame(updatePos); }, { passive: true });

    badge.addEventListener('click', (e) => {
      e.stopPropagation();
      // If we have a captured media URL for this video's src, prefer that
      const src = video.currentSrc;
      if (src && !src.startsWith('blob:')) {
        sendToApp(src, location.href, document.title);
      } else {
        // blob: URL or MSE — show the panel to pick from intercepted network requests
        showPanel();
      }
    });
  }

  function scanVideos() {
    document.querySelectorAll('video').forEach(addBadge);
  }

  scanVideos();

  const observer = new MutationObserver(() => scanVideos());
  observer.observe(document.body, { childList: true, subtree: true });

})();
