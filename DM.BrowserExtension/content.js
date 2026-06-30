// Sagar Download Manager — content script v4

(function () {
    'use strict';
    if (window._sdmInjected) return;
    window._sdmInjected = true;

    const L = (...a) => console.warn('[SDM]', ...a);   // warn so it shows in default DevTools filter
    L('v4 loaded on', location.hostname, location.pathname.slice(0, 40));

    // Visible on-screen toast — confirms script is running without needing DevTools
    (function showLoadToast() {
        const t = document.createElement('div');
        t.style.cssText = [
            'all:initial','position:fixed','bottom:16px','right:16px',
            'background:#1e293b','color:#60a5fa','border:1px solid #334155',
            'padding:10px 14px','border-radius:8px','font-size:13px',
            'font-weight:600','z-index:2147483647','opacity:1',
            'transition:opacity .5s','box-shadow:0 4px 16px rgba(0,0,0,.6)',
            'font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif',
            'pointer-events:none',
        ].join('!important;') + '!important';
        t.textContent = '↓ SDM Active';
        document.documentElement.appendChild(t);
        setTimeout(() => { t.style.opacity = '0'; setTimeout(() => t.remove(), 600); }, 3500);
    })();

    // ── Constants ─────────────────────────────────────────────────────────────
    const VIDEO_SITES = [
        'youtube.com','youtu.be','vimeo.com','dailymotion.com','twitch.tv',
        'tiktok.com','twitter.com','x.com','instagram.com','facebook.com',
        'reddit.com','bilibili.com','rumble.com','odysee.com','soundcloud.com',
    ];
    const pageHost    = location.hostname.replace(/^www\./, '');
    const isKnownSite = VIDEO_SITES.some(s => pageHost === s || pageHost.endsWith('.'+s));
    const BLOCKED     = ['/s/search/','/generate_204','/pagead/','/pcs/','/youtubei/'];
    const isBad       = u => !u || BLOCKED.some(p => u.includes(p));

    // Is current page URL a valid video page?
    function isVideoPage() {
        if (!isKnownSite) return true;
        try {
            const u = new URL(location.href), h = u.hostname.replace(/^www\./,''), p = u.pathname;
            if (h === 'youtube.com')
                return (p === '/watch' && u.searchParams.has('v')) || /^\/(shorts|live|embed)\/\w/.test(p);
            if (h === 'youtu.be') return p.length > 1;
            if (h === 'vimeo.com') return /\/\d{4,}/.test(p);
            if (h === 'twitch.tv') return p.split('/').filter(Boolean).length >= 1;
            if (h === 'dailymotion.com') return p.includes('/video/');
            if (h === 'tiktok.com') return p.includes('/video/');
            if (h === 'twitter.com' || h === 'x.com') return p.includes('/status/');
            return p.length > 1;
        } catch { return false; }
    }

    // ── Popup media list ──────────────────────────────────────────────────────
    const mediaList = [];
    function addMedia(rawUrl, type, title) {
        if (!rawUrl || rawUrl.startsWith('blob:') || rawUrl.startsWith('data:')) return;
        let url; try { url = new URL(rawUrl, location.href).href; } catch { return; }
        if (isBad(url) || mediaList.some(m => m.url === url)) return;
        mediaList.push({ url, type, title: title || url.split('/').pop().split('?')[0] || 'media', pageUrl: location.href });
        try { chrome.runtime.sendMessage({ action: 'updateBadge', count: mediaList.length }); } catch {}
    }
    chrome.runtime.onMessage.addListener((msg, _s, reply) => {
        if (msg.action === 'getMediaList') { reply({ media: mediaList }); return true; }
    });

    // ── Bar element (plain DOM, appended to <html>) ───────────────────────────
    let barEl = null, titleEl = null, dlLblEl = null;
    let activeMeta = null, hideTimer = null;

    function ensureBar() {
        if (barEl && document.documentElement.contains(barEl)) return;

        // Reset barEl if it was removed from DOM
        barEl = null;

        const style = document.createElement('style');
        style.id = '_sdm_style';
        style.textContent = `
#_sdm_bar{
  all:initial;
  /* These need !important to beat YouTube's page CSS */
  position:fixed!important; z-index:2147483647!important;
  display:flex!important; align-items:center!important;
  height:44px!important; overflow:hidden!important;
  background:linear-gradient(180deg,rgba(5,5,14,.97),rgba(5,5,14,.84))!important;
  border-bottom:1px solid rgba(255,255,255,.12)!important; color:#fff!important;
  font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif!important;
  box-sizing:border-box!important;
  /* NO !important — these are set/animated via JS inline style or class toggle */
  left:0; top:0; width:0;
  opacity:0; transform:translateY(-44px);
  transition:opacity .2s,transform .2s;
  pointer-events:none;
}
/* Higher specificity beats the default rule without needing !important */
#_sdm_bar._sdm_on{opacity:1;transform:translateY(0);pointer-events:auto;}
#_sdm_bar *{box-sizing:border-box!important;font-family:inherit!important;}
._sdm_btn{all:unset!important;display:flex!important;align-items:center!important;gap:6px!important;
  height:100%!important;padding:0 15px!important;border-right:1px solid rgba(255,255,255,.1)!important;
  cursor:pointer!important;flex-shrink:0!important;font-size:12px!important;font-weight:700!important;color:#fff!important;}
._sdm_btn:hover{background:rgba(255,255,255,.1)!important;}
#_sdm_title{flex:1!important;min-width:0!important;font-size:11px!important;
  color:rgba(255,255,255,.45)!important;overflow:hidden!important;text-overflow:ellipsis!important;
  white-space:nowrap!important;padding:0 12px!important;}
#_sdm_brand{font-size:10px!important;font-weight:700!important;letter-spacing:.07em!important;
  color:rgba(255,255,255,.22)!important;text-transform:uppercase!important;padding:0 12px!important;flex-shrink:0!important;}
#_sdm_def{display:flex!important;align-items:center!important;flex:1!important;min-width:0!important;height:100%!important;}
#_sdm_qty{display:none!important;align-items:center!important;flex:1!important;min-width:0!important;height:100%!important;}
._sdm_qm #_sdm_def{display:none!important;}
._sdm_qm #_sdm_qty{display:flex!important;}
#_sdm_back{all:unset!important;display:flex!important;align-items:center!important;justify-content:center!important;
  width:36px!important;height:100%!important;border-right:1px solid rgba(255,255,255,.08)!important;
  cursor:pointer!important;font-size:14px!important;color:rgba(255,255,255,.5)!important;}
#_sdm_back:hover{background:rgba(255,255,255,.08)!important;color:#fff!important;}
._sdm_ql{font-size:10px!important;color:rgba(255,255,255,.3)!important;padding:0 8px!important;flex-shrink:0!important;}
._sdm_q{all:unset!important;display:flex!important;align-items:center!important;justify-content:center!important;
  height:100%!important;padding:0 13px!important;font-size:11.5px!important;font-weight:700!important;
  color:rgba(255,255,255,.75)!important;cursor:pointer!important;flex-shrink:0!important;
  border-right:1px solid rgba(255,255,255,.06)!important;white-space:nowrap!important;}
._sdm_q:hover{background:rgba(37,99,235,.85)!important;color:#fff!important;}
._sdm_q[data-q=best]{color:#60a5fa!important;}
._sdm_q[data-q=mp3]{color:#a78bfa!important;}`;

        document.documentElement.appendChild(style);

        barEl = document.createElement('div');
        barEl.id = '_sdm_bar';
        barEl.innerHTML = `
<div id="_sdm_def">
  <button class="_sdm_btn" id="_sdm_dlbtn">
    <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><polyline points="7 10 12 15 17 10"/><line x1="12" y1="15" x2="12" y2="3"/></svg>
    <span id="_sdm_lbl">Download</span>
  </button>
  <span id="_sdm_title"></span>
  <span id="_sdm_brand">SDM</span>
</div>
<div id="_sdm_qty">
  <button id="_sdm_back">✕</button>
  <span class="_sdm_ql">Quality:</span>
  <button class="_sdm_q" data-q="best">★ Best</button>
  <button class="_sdm_q" data-q="1080p">1080p</button>
  <button class="_sdm_q" data-q="720p">720p</button>
  <button class="_sdm_q" data-q="480p">480p</button>
  <button class="_sdm_q" data-q="360p">360p</button>
  <button class="_sdm_q" data-q="144p">144p</button>
  <button class="_sdm_q" data-q="mp3">♪ MP3</button>
  <span style="flex:1"></span>
  <span style="font-size:10px;font-weight:700;letter-spacing:.07em;color:rgba(255,255,255,.2);text-transform:uppercase;padding:0 12px;flex-shrink:0">SDM</span>
</div>`;
        document.documentElement.appendChild(barEl);

        titleEl = document.getElementById('_sdm_title');
        dlLblEl = document.getElementById('_sdm_lbl');

        document.getElementById('_sdm_dlbtn').addEventListener('click', e => {
            e.stopPropagation(); barEl.classList.add('_sdm_qm');
        });
        document.getElementById('_sdm_back').addEventListener('click', e => {
            e.stopPropagation(); barEl.classList.remove('_sdm_qm');
        });
        barEl.querySelectorAll('._sdm_q').forEach(q => {
            q.addEventListener('click', e => {
                e.stopPropagation();
                if (!activeMeta) return;
                const quality = q.dataset.q;
                barEl.classList.remove('_sdm_qm');
                dlLblEl.textContent = 'Sending…';
                L('download request | quality:', quality, '| url:', activeMeta.url.slice(-60));
                chrome.runtime.sendMessage(
                    { action:'download', url:activeMeta.url, pageUrl:activeMeta.pageUrl,
                      title:activeMeta.title, quality },
                    resp => {
                        const ok = !chrome.runtime.lastError && resp?.ok;
                        dlLblEl.textContent = ok ? '✓ Added!' : '✕ App offline';
                        dlLblEl.parentElement.style.background = ok
                            ? 'rgba(22,155,72,.55)' : 'rgba(185,35,35,.55)';
                        setTimeout(() => {
                            dlLblEl.textContent = 'Download';
                            dlLblEl.parentElement.style.background = '';
                            barEl.classList.remove('_sdm_on','_sdm_qm');
                            activeMeta = null;
                        }, 2000);
                    }
                );
            });
        });
        barEl.addEventListener('mouseenter', () => clearTimeout(hideTimer));
        barEl.addEventListener('mouseleave', scheduleHide);
        L('bar built & attached');
    }

    function showBar(meta, rect) {
        ensureBar();
        activeMeta          = meta;
        titleEl.textContent = meta.title;
        barEl.style.left    = rect.left  + 'px';
        barEl.style.top     = rect.top   + 'px';
        barEl.style.width   = rect.width + 'px';
        requestAnimationFrame(() => barEl && barEl.classList.add('_sdm_on'));
        clearTimeout(hideTimer);
    }

    function scheduleHide() {
        clearTimeout(hideTimer);
        hideTimer = setTimeout(() => {
            if (barEl) { barEl.classList.remove('_sdm_on','_sdm_qm'); activeMeta = null; }
        }, 600);
    }

    // ── Video registry ────────────────────────────────────────────────────────
    const validVideos = [];

    function registerVideo(v) {
        if (validVideos.includes(v)) return;
        const r = v.getBoundingClientRect();
        validVideos.push(v);
        L('registered | total:', validVideos.length, '| size:', Math.round(r.width)+'x'+Math.round(r.height),
          '| readyState:', v.readyState);
        if (isVideoPage()) {
            const src = isKnownSite ? location.href : (v.currentSrc || v.src || '');
            addMedia(src, 'video', document.title || '');
        }
    }

    function attachVideo(v) {
        if (v._sdm) return;
        v._sdm = true;
        if (isKnownSite) {
            // Known site: register immediately; URL validity re-checked at hover time
            registerVideo(v);
        } else {
            const tryReg = () => {
                const src = v.currentSrc || v.src;
                if (!src || src.startsWith('blob:') || src.startsWith('data:') || isBad(src)) return;
                registerVideo(v);
            };
            if (v.readyState >= 1) tryReg();
            else v.addEventListener('loadedmetadata', tryReg, { once: true });
        }
    }

    function attachAudio(a) {
        if (a._sdm) return; a._sdm = true;
        if (isKnownSite) return;
        const tryAdd = () => {
            const src = a.currentSrc || a.src;
            if (!src || isBad(src) || (isFinite(a.duration) && a.duration < 10)) return;
            addMedia(src, 'audio', a.title || src.split('/').pop().split('?')[0] || '');
        };
        if (a.readyState >= 1) tryAdd();
        else a.addEventListener('loadedmetadata', tryAdd, { once: true });
    }

    // ── Scan for video/audio elements ─────────────────────────────────────────
    function scan() {
        const vids = document.querySelectorAll('video');
        vids.forEach(attachVideo);
        document.querySelectorAll('audio').forEach(attachAudio);
        if (vids.length) L('scan: ', vids.length, 'video(s) | registered:', validVideos.length);
    }

    // Initial scan
    scan();

    // MutationObserver — catches dynamically added elements
    new MutationObserver(scan).observe(document.documentElement, { childList:true, subtree:true });

    // YouTube SPA navigation events (fire when navigating between pages)
    document.addEventListener('yt-navigate-finish',    scan);
    document.addEventListener('yt-page-data-updated',  scan);
    document.addEventListener('yt-player-updated',     scan);

    // Polling fallback — every 1.5 s, catches anything the above missed
    setInterval(scan, 1500);

    // ── Global hover detection via bounding-rect (bypasses overlay divs) ──────
    let _raf = null;
    document.addEventListener('mousemove', e => {
        if (_raf) return;
        _raf = requestAnimationFrame(() => {
            _raf = null;
            if (validVideos.length === 0) return;
            if (isKnownSite && !isVideoPage()) { scheduleHide(); return; }

            const mx = e.clientX, my = e.clientY;
            let hit = null;
            for (const v of validVideos) {
                const r = v.getBoundingClientRect();
                if (r.width < 80 || r.height < 40) continue;
                if (mx >= r.left && mx <= r.right && my >= r.top && my <= r.bottom) {
                    hit = v; break;
                }
            }
            if (hit) {
                clearTimeout(hideTimer);
                const url  = isKnownSite ? location.href : (hit.currentSrc || hit.src || location.href);
                const meta = { url, pageUrl: location.href, title: document.title || 'Video' };
                if (!barEl?.classList.contains('_sdm_on') || activeMeta?.url !== url) {
                    showBar(meta, hit.getBoundingClientRect());
                } else {
                    const r = hit.getBoundingClientRect();
                    if (barEl) { barEl.style.top = r.top+'px'; barEl.style.left = r.left+'px'; barEl.style.width = r.width+'px'; }
                }
            } else {
                scheduleHide();
            }
        });
    }, { passive:true });

    // ── Debug helpers ─────────────────────────────────────────────────────────
    window._sdmDebug = () => {
        const vids = document.querySelectorAll('video');
        const info = {
            version: 'v4', isKnownSite, isVideoPage: isVideoPage(),
            domVideoCount: vids.length,
            validVideoCount: validVideos.length,
            videoDetails: [...vids].map((v,i) => {
                const r = v.getBoundingClientRect();
                return { i, w: Math.round(r.width), h: Math.round(r.height),
                         readyState: v.readyState, registered: validVideos.includes(v),
                         src: (v.currentSrc||v.src||'').slice(0,50) };
            }),
            barBuilt: !!barEl,
            barInDOM: barEl ? document.documentElement.contains(barEl) : false,
            barVisible: barEl?.classList.contains('_sdm_on') ?? false,
            mediaItems: mediaList.length,
        };
        console.log('[SDM] DEBUG:', JSON.stringify(info, null, 2));
        return info;
    };

    // Force-show bar on the first video — call from console to test bar independently
    window._sdmForceShow = () => {
        const v = document.querySelector('video');
        if (!v) { L('no video in DOM'); return; }
        const r = v.getBoundingClientRect();
        L('force show | rect:', Math.round(r.left), Math.round(r.top), Math.round(r.width), Math.round(r.height));
        showBar({ url: location.href, pageUrl: location.href, title: document.title }, r);
    };

    L('ready | validVideos:', validVideos.length, '| videoPage:', isVideoPage());
    L('→ type _sdmDebug() or _sdmForceShow() in console to test');
})();
