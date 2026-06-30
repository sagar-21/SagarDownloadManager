// Sagar Download Manager — background service worker

const SDM_BASE = 'http://localhost:6336';

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {

    if (msg.action === 'download') {
        fetch(`${SDM_BASE}/api/download`, {
            method:  'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                url:     msg.url,
                pageUrl: msg.pageUrl || '',
                title:   msg.title   || '',
                quality: msg.quality || '',   // e.g. "720p", "1080p", "mp3", "best"
            }),
        })
        .then(r => r.ok ? r.json() : Promise.reject(`HTTP ${r.status}`))
        .then(()   => sendResponse({ ok: true }))
        .catch(err => sendResponse({ ok: false, error: String(err) }));
        return true;
    }

    if (msg.action === 'ping') {
        fetch(`${SDM_BASE}/api/ping`)
            .then(r => sendResponse({ running: r.ok }))
            .catch(()  => sendResponse({ running: false }));
        return true;
    }

    // Validate a direct file URL via HEAD request.
    // Background service worker can do cross-origin fetches (extension has host_permissions).
    if (msg.action === 'validateUrl') {
        fetch(msg.url, { method: 'HEAD', signal: AbortSignal.timeout(5000) })
            .then(r => {
                const ct = r.headers.get('content-type') || '';
                const cl = parseInt(r.headers.get('content-length') || '-1', 10);
                // Considered valid if the response is OK and either:
                //  (a) content-type suggests a media/binary file, or
                //  (b) content-length > 0 (some servers omit content-type for octet streams)
                const isMedia =
                    ct.startsWith('video/') || ct.startsWith('audio/') ||
                    ct.includes('mp4')  || ct.includes('mp3')  || ct.includes('webm') ||
                    ct.includes('ogg')  || ct.includes('mpeg') || ct.includes('flv')  ||
                    ct.includes('mkv')  || ct.includes('wav')  ||
                    ct === 'application/octet-stream';
                const valid = r.ok && (isMedia || cl > 1024);
                sendResponse({ valid, contentType: ct, size: cl });
            })
            .catch(() => sendResponse({ valid: false }));
        return true;
    }

    if (msg.action === 'updateBadge') {
        const n = msg.count || 0;
        chrome.action.setBadgeText({ text: n > 0 ? String(n) : '' });
        chrome.action.setBadgeBackgroundColor({ color: '#2563eb' });
    }
});
