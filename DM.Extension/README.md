# Download Manager Browser Extension

Detects media on web pages and sends them to the Download Manager app with one click.

## Loading the extension (unpacked, for testing)

1. Open **Chrome** or **Edge** and navigate to `chrome://extensions` (or `edge://extensions`).
2. Enable **Developer mode** (toggle, top-right).
3. Click **Load unpacked** and select this `DM.Extension` folder.
4. The extension icon appears in the toolbar. Pin it for easy access.

## How it works

### Media detection — two paths

**Path 1: Network interception (webRequest)**
The background service worker listens to all completed network requests via
`chrome.webRequest.onCompleted`. Any response whose URL ends in a recognized
media extension (`.mp4`, `.mkv`, `.m3u8`, `.mpd`, etc.) or has a media
Content-Type is captured and stored per-tab in `chrome.storage.session`.

This catches:
- Direct video/audio file downloads
- HLS manifests (`.m3u8`) — the key URL yt-dlp needs
- DASH manifests (`.mpd`)

**Path 2: DOM scan (content script)**
The content script scans for `<video>` elements and adds a small ⬇ badge
button in the top-left corner of each one. Clicking the badge:
- If the video has a direct URL (not `blob:`), sends it straight to the app.
- If it's a blob: URL (MSE or HLS assembled in-browser), opens the panel
  so you can pick from the intercepted network requests instead.

### The floating panel
When any media is detected, a panel slides in from the bottom-right corner of
the page listing everything found. Each item has a ⬇ button that sends it to
the app. The panel stays until manually dismissed or the page navigates away.

The toolbar popup shows the same list and lets you check whether the app is
running (green/red dot).

## HLS and blob URLs — what's different

| Scenario | What the extension sees | What to download |
|---|---|---|
| Direct `.mp4` link | The actual file URL | The URL itself |
| HLS stream | `.m3u8` playlist URL (from network) | The `.m3u8` URL — yt-dlp fetches all segments |
| DASH stream | `.mpd` manifest URL (from network) | The `.mpd` URL — yt-dlp/ffmpeg handles it |
| MSE (Media Source) | `blob:https://...` on the `<video>` | Use the `.m3u8` or `.mpd` captured from network |
| DRM-protected | Blob URL + encrypted segments | Not downloadable via this method |

**Why you can't use blob: URLs directly:**  
`blob:https://example.com/...` is a local object URL created by the browser
from decrypted/assembled segment data. It only exists in that browser tab,
has no persistent URL, and yt-dlp cannot reach it. The `.m3u8` or `.mpd`
manifest URL — captured by webRequest before the browser processes it —
is what yt-dlp needs to reconstruct the full stream.

## Localhost security model

The app server binds **only** to `http://localhost:{port}/` — no external
interface, no firewall rule needed. This means:

- Remote attackers cannot reach it (the OS never routes external traffic to
  loopback).
- Other local processes could connect, but the app verifies `RemoteEndPoint`
  is `127.0.0.1` or `::1` as a belt-and-suspenders check.
- The browser extension has `host_permissions` for `http://localhost:6336/*`
  so fetch requests from the service worker are allowed.
- Chrome's Private Network Access (PNA) policy (Chrome 94+) requires an
  `Access-Control-Allow-Private-Network: true` header for requests to localhost
  from non-loopback page contexts. The app always sends this header.

## Changing the port

1. In **Download Manager → Settings → General → Browser Extension**, change
   the port number and restart the app.
2. In `manifest.json`, update `"http://localhost:6336/*"` in `host_permissions`
   to the new port.
3. In `background.js`, update `const APP_BASE = 'http://localhost:6336';`.
4. Re-load the extension (`chrome://extensions` → refresh icon).

> The port `6336` was chosen to avoid conflicts with common dev servers
> (3000, 5173, 8080, etc.). Any unused port in the range 1024–65535 works.
