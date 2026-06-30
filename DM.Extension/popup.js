// popup.js — runs in the extension toolbar popup context

const dot       = document.getElementById('dot');
const appStatus = document.getElementById('app-status');
const mediaList = document.getElementById('media-list');
const emptyMsg  = document.getElementById('empty-msg');
const statusMsg = document.getElementById('status-msg');

function setAppStatus(ok) {
  if (ok) {
    dot.className = 'status-dot connected';
    appStatus.textContent = 'App running';
  } else {
    dot.className = 'status-dot error';
    appStatus.textContent = 'App not found';
  }
}

function setStatus(msg, isErr) {
  statusMsg.textContent = msg;
  statusMsg.className = isErr ? 'err' : '';
  statusMsg.style.display = msg ? 'block' : 'none';
  if (!isErr) setTimeout(() => { statusMsg.style.display = 'none'; }, 2500);
}

function renderMedia(items) {
  if (!items || items.length === 0) {
    emptyMsg.textContent = 'No media detected on this page.';
    emptyMsg.style.display = 'block';
    return;
  }
  emptyMsg.style.display = 'none';

  items.forEach(item => {
    const name     = item.url.split('/').pop().split('?')[0] || 'media';
    const isStream = item.url.endsWith('.m3u8') || item.url.endsWith('.mpd');

    const row = document.createElement('div');
    row.className = 'media-row';
    row.innerHTML = `
      <span class="media-label ${isStream ? 'stream' : ''}">${item.label}</span>
      <span class="media-name" title="${item.url}">${name}</span>
      <button class="dl-btn" title="Download">⬇</button>
    `;
    row.querySelector('.dl-btn').addEventListener('click', () => {
      chrome.runtime.sendMessage(
        { type: 'DM_SEND_TO_APP', url: item.url, pageUrl: item.pageUrl, title: document.title },
        (res) => {
          if (chrome.runtime.lastError || !res?.ok) {
            setStatus('Failed — is Download Manager running?', true);
          } else {
            setStatus('Sent ✓', false);
          }
        }
      );
    });
    mediaList.appendChild(row);
  });
}

// ── Init ─────────────────────────────────────────────────────────────────────

// Ping app
chrome.runtime.sendMessage({ type: 'DM_PING_APP' }, (res) => {
  setAppStatus(!chrome.runtime.lastError && res?.ok);
});

// Get media for active tab
chrome.tabs.query({ active: true, currentWindow: true }, ([tab]) => {
  if (!tab) { emptyMsg.textContent = 'No active tab.'; return; }
  chrome.runtime.sendMessage({ type: 'DM_GET_MEDIA_FOR_TAB', tabId: tab.id }, (res) => {
    if (chrome.runtime.lastError) { emptyMsg.textContent = 'Error loading media.'; return; }
    renderMedia(res?.items ?? []);
  });
});
