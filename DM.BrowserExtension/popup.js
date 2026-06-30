const SDM_BASE  = 'http://localhost:6336';
let   connected = false;

const dot       = document.getElementById('dot');
const statusTxt = document.getElementById('status-text');
const badge     = document.getElementById('badge');
const mediaList = document.getElementById('media-list');
const emptyEl   = document.getElementById('empty-state');
const pageBtn   = document.getElementById('page-btn');

const QUALITIES = [
    { label: '★ Best', q: 'best',  cls: 'best' },
    { label: '1080p',  q: '1080p', cls: ''      },
    { label: '720p',   q: '720p',  cls: ''      },
    { label: '480p',   q: '480p',  cls: ''      },
    { label: '360p',   q: '360p',  cls: ''      },
    { label: '144p',   q: '144p',  cls: ''      },
    { label: '♪ MP3',  q: 'mp3',   cls: 'mp3'  },
];

// ── Connection check ──────────────────────────────────────────────────────────
async function checkConnection() {
    try {
        const r = await fetch(`${SDM_BASE}/api/ping`,
            { signal: AbortSignal.timeout(3000) });
        setConn(r.ok);
    } catch {
        setConn(false);
    }
}

function setConn(on) {
    connected             = on;
    dot.className         = 'dot ' + (on ? 'green' : 'red');
    statusTxt.textContent = on ? 'SDM is running' : 'SDM is not running';
    pageBtn.disabled      = !on;
}

// ── Load media from content script ───────────────────────────────────────────
async function loadMedia() {
    const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
    if (!tab?.id) return;

    chrome.tabs.sendMessage(tab.id, { action: 'getMediaList' }, resp => {
        if (chrome.runtime.lastError || !resp?.media) return;
        renderMedia(resp.media);
    });
}

function renderMedia(items) {
    mediaList.querySelectorAll('.media-item').forEach(el => el.remove());

    if (!items || items.length === 0) {
        emptyEl.style.display = '';
        badge.classList.add('hidden');
        return;
    }

    emptyEl.style.display = 'none';
    badge.textContent = items.length;
    badge.classList.remove('hidden');

    items.forEach(item => {
        const name = item.title || item.url.split('/').pop().split('?')[0] || 'media';
        const disp = name.length > 36 ? name.slice(0, 33) + '…' : name;

        const row = document.createElement('div');
        row.className = 'media-item';

        const qBtns = QUALITIES.map(({ label, q, cls }) =>
            `<button class="q-btn ${cls}" data-q="${q}">${label}</button>`
        ).join('');

        row.innerHTML = `
            <div class="item-top">
              <span class="type-badge ${item.type === 'video' ? 'type-video' : 'type-audio'}">
                ${item.type === 'video' ? 'Video' : 'Audio'}
              </span>
              <span class="item-name" title="${esc(name)}">${esc(disp)}</span>
            </div>
            <div class="item-bottom">${qBtns}</div>`;

        row.querySelectorAll('.q-btn').forEach(btn => {
            btn.addEventListener('click', () => {
                if (!connected) return;
                downloadItem(row, btn, item, btn.dataset.q);
            });
        });

        mediaList.appendChild(row);
    });
}

async function downloadItem(row, clickedBtn, item, quality) {
    // Disable all quality buttons for this item
    row.querySelectorAll('.q-btn').forEach(b => { b.disabled = true; });

    try {
        const r = await fetch(`${SDM_BASE}/api/download`, {
            method:  'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                url:     item.url,
                pageUrl: item.pageUrl || item.url,
                title:   item.title  || '',
                quality,
            }),
            signal: AbortSignal.timeout(5000),
        });

        if (r.ok) {
            clickedBtn.classList.add('done');
            clickedBtn.textContent = '✓ Added!';
            // Re-enable others after success
            setTimeout(() => {
                row.querySelectorAll('.q-btn').forEach(b => {
                    b.disabled = false;
                    if (b !== clickedBtn) return;
                    b.classList.remove('done');
                    b.textContent = QUALITIES.find(x => x.q === quality)?.label || quality;
                });
            }, 2500);
        } else {
            flashErr(row, clickedBtn, quality);
        }
    } catch {
        flashErr(row, clickedBtn, quality);
    }
}

function flashErr(row, btn, quality) {
    btn.classList.add('err');
    btn.textContent = '✕';
    setTimeout(() => {
        row.querySelectorAll('.q-btn').forEach(b => {
            b.disabled = false;
            b.classList.remove('err');
        });
        btn.textContent = QUALITIES.find(x => x.q === quality)?.label || quality;
    }, 2000);
}

// ── Download this page ────────────────────────────────────────────────────────
pageBtn.addEventListener('click', async () => {
    if (!connected) return;
    const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
    if (!tab?.url) return;

    pageBtn.disabled    = true;
    pageBtn.textContent = 'Sending…';

    try {
        const r = await fetch(`${SDM_BASE}/api/download`, {
            method:  'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                url: tab.url, pageUrl: tab.url, title: tab.title || '', quality: 'best',
            }),
        });
        pageBtn.textContent = r.ok ? '✓ Added to queue!' : '✕ Failed';
    } catch {
        pageBtn.textContent = '✕ SDM not running';
    }

    setTimeout(() => {
        pageBtn.textContent = '↓ Download This Page';
        pageBtn.disabled    = !connected;
    }, 2200);
});

function esc(s) {
    return s.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
}

checkConnection();
loadMedia();
