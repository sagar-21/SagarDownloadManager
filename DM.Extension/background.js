// ─────────────────────────────────────────────────────────────────────────────
// background.js  —  Service Worker (Manifest V3)
//
// Responsibilities:
//   1. Intercept completed network requests and identify media URLs.
//   2. Store detected media per tab in chrome.storage.session (survives
//      service-worker restarts within the session but not a browser restart).
//   3. Notify the content script to update its panel.
//   4. Send media URLs to the Download Manager app via localhost HTTP.
// ─────────────────────────────────────────────────────────────────────────────

const APP_BASE = 'http://localhost:6336';

// ── Media detection ───────────────────────────────────────────────────────────

const MEDIA_EXTENSIONS = new Set([
  'mp4', 'mkv', 'avi', 'mov', 'wmv', 'flv', 'webm', 'm4v', 'ts', 'm2ts',
  'mp3', 'flac', 'wav', 'ogg', 'm4a', 'aac', 'opus',
  'm3u8', 'mpd',                                          // HLS manifest, DASH manifest
]);

const MEDIA_CONTENT_TYPES = [
  'video/',
  'audio/',
  'application/x-mpegurl',     // HLS
  'application/vnd.apple.mpegurl',
  'application/dash+xml',      // DASH
  'application/octet-stream',  // generic binary — only kept when URL has media ext
];

function isMediaUrl(url, contentType) {
  try {
    const path = new URL(url).pathname.toLowerCase();
    const ext  = path.split('.').pop();
    if (MEDIA_EXTENSIONS.has(ext)) return true;
  } catch { return false; }

  if (contentType) {
    const ct = contentType.toLowerCase();
    for (const prefix of MEDIA_CONTENT_TYPES) {
      if (prefix !== 'application/octet-stream' && ct.startsWith(prefix)) return true;
    }
  }
  return false;
}

function labelFor(url) {
  try {
    const u    = new URL(url);
    const name = u.pathname.split('/').pop() || u.hostname;
    const ext  = name.split('.').pop()?.toLowerCase();
    if (ext === 'm3u8') return 'HLS';
    if (ext === 'mpd')  return 'DASH';
    if (['mp4','mkv','avi','mov','webm','m4v'].includes(ext)) return 'Video';
    if (['mp3','flac','wav','ogg','m4a','aac','opus'].includes(ext)) return 'Audio';
    return 'Media';
  } catch { return 'Media'; }
}

// ── Per-tab media store (keyed: "tab:{tabId}") ────────────────────────────────

async function getTabMedia(tabId) {
  const key = `tab:${tabId}`;
  const res = await chrome.storage.session.get(key);
  return res[key] || [];
}

async function addMediaToTab(tabId, item) {
  const key    = `tab:${tabId}`;
  const items  = await getTabMedia(tabId);
  const exists = items.some(i => i.url === item.url);
  if (exists) return false;
  items.push(item);
  await chrome.storage.session.set({ [key]: items });
  return true;
}

async function clearTabMedia(tabId) {
  await chrome.storage.session.remove(`tab:${tabId}`);
}

// ── webRequest listener ───────────────────────────────────────────────────────

chrome.webRequest.onCompleted.addListener(
  async (details) => {
    if (details.tabId < 0) return;  // background / extension request
    if (details.type === 'main_frame') {
      // New page navigation — clear old detections for this tab
      await clearTabMedia(details.tabId);
      return;
    }

    const ct = details.responseHeaders
      ?.find(h => h.name.toLowerCase() === 'content-type')?.value ?? '';

    if (!isMediaUrl(details.url, ct)) return;

    const item = {
      url:       details.url,
      pageUrl:   details.initiator ?? '',
      label:     labelFor(details.url),
      timestamp: Date.now(),
    };

    const added = await addMediaToTab(details.tabId, item);
    if (!added) return;

    // Notify the content script to update its panel
    try {
      await chrome.tabs.sendMessage(details.tabId, {
        type:  'DM_MEDIA_ADDED',
        items: await getTabMedia(details.tabId),
      });
    } catch {
      // Content script not yet loaded (e.g. Chrome extension pages) — ignore
    }
  },
  { urls: ['<all_urls>'] },
  ['responseHeaders']
);

// ── Message handlers (from content.js and popup.js) ──────────────────────────

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  switch (msg.type) {

    case 'DM_GET_MEDIA':
      getTabMedia(sender.tab?.id ?? -1)
        .then(items => sendResponse({ items }));
      return true;  // async

    case 'DM_SEND_TO_APP': {
      const { url, pageUrl, title } = msg;
      sendToApp(url, pageUrl, title)
        .then(ok  => sendResponse({ ok }))
        .catch(() => sendResponse({ ok: false }));
      return true;  // async
    }

    case 'DM_PING_APP':
      pingApp()
        .then(ok  => sendResponse({ ok }))
        .catch(() => sendResponse({ ok: false }));
      return true;

    case 'DM_GET_MEDIA_FOR_TAB':
      getTabMedia(msg.tabId ?? -1)
        .then(items => sendResponse({ items }));
      return true;
  }
});

// ── HTTP helpers ──────────────────────────────────────────────────────────────

async function pingApp() {
  const res = await fetch(`${APP_BASE}/api/ping`, { method: 'GET' });
  return res.ok;
}

async function sendToApp(url, pageUrl, title) {
  const res = await fetch(`${APP_BASE}/api/download`, {
    method:  'POST',
    headers: { 'Content-Type': 'application/json' },
    body:    JSON.stringify({ url, pageUrl, title }),
  });
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  return true;
}

// ── Tab cleanup ───────────────────────────────────────────────────────────────

chrome.tabs.onRemoved.addListener(tabId => clearTabMedia(tabId));
