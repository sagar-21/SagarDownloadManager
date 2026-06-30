using System.Runtime.InteropServices;
using System.Windows.Interop;
using WpfClipboard = System.Windows.Clipboard;

namespace DM.App.Services;

/// <summary>
/// Listens for clipboard changes using Win32 AddClipboardFormatListener.
/// Fires UrlDetected when a downloadable HTTP/HTTPS URL is copied.
///
/// Hooking approach:
///   Win32 AddClipboardFormatListener(hwnd) registers the window for
///   WM_CLIPBOARDUPDATE (0x031D) notifications — efficient, no polling, no
///   fragile viewer chain. HwndSource.AddHook intercepts those messages on the
///   UI thread before WPF processes them. Multiple apps can listen independently.
/// </summary>
public sealed class ClipboardMonitorService : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private const int WM_CLIPBOARDUPDATE = 0x031D;

    private IntPtr     _hwnd;
    private HwndSource? _source;
    private string     _lastSeen = "";
    private bool       _running;

    /// <summary>Fired on the UI thread when a downloadable URL is detected.</summary>
    public event Action<string>? UrlDetected;

    public bool IsRunning => _running;

    public void Start(IntPtr hwnd)
    {
        if (_running) return;
        _hwnd   = hwnd;
        _source = HwndSource.FromHwnd(hwnd);
        _source.AddHook(WndProc);
        AddClipboardFormatListener(hwnd);
        _running = true;
    }

    public void Stop()
    {
        if (!_running) return;
        RemoveClipboardFormatListener(_hwnd);
        _source?.RemoveHook(WndProc);
        _source  = null;
        _running = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
            CheckClipboard();
        return IntPtr.Zero;
    }

    private void CheckClipboard()
    {
        try
        {
            if (!WpfClipboard.ContainsText()) return;
            var text = WpfClipboard.GetText().Trim();

            // Ignore multi-line blobs (not a URL)
            if (text.Contains('\n') || text.Length > 2048) return;
            if (text == _lastSeen) return;

            if (!IsDownloadable(text)) return;
            _lastSeen = text;
            UrlDetected?.Invoke(text);
        }
        catch { }
    }

    // ── URL classification ────────────────────────────────────────────────────

    public static bool IsDownloadable(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "https")) return false;

        var path  = uri.AbsolutePath.ToLowerInvariant();
        var host  = uri.Host.ToLowerInvariant();
        var query = uri.Query.ToLowerInvariant();

        // Direct file extension
        foreach (var ext in FileExtensions)
            if (path.EndsWith(ext, StringComparison.Ordinal)) return true;

        // Known video/audio streaming hosts (yt-dlp coverage)
        foreach (var h in StreamingHosts)
            if (host == h || host.EndsWith('.' + h)) return true;

        // Query / path signals
        if (query.Contains("dl=1") || query.Contains("dl=true")) return true;
        if (path.Contains("/download/") || path.Contains("/dl/")) return true;
        if (query.Contains("content-disposition") || query.Contains("attachment")) return true;

        return false;
    }

    private static readonly string[] FileExtensions =
    [
        // Archives
        ".zip", ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz", ".zst",
        // Executables / installers
        ".exe", ".msi", ".msix", ".appx", ".apk", ".deb", ".rpm",
        ".dmg", ".pkg", ".bin", ".run",
        // Documents
        ".pdf", ".epub", ".mobi", ".docx", ".xlsx", ".pptx",
        // Video
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v",
        ".ts", ".m2ts",
        // Audio
        ".mp3", ".flac", ".wav", ".ogg", ".m4a", ".aac", ".opus",
        // Images / disc
        ".iso", ".img", ".vhd", ".vhdx",
        // Torrents / other
        ".torrent",
    ];

    private static readonly string[] StreamingHosts =
    [
        "youtube.com", "youtu.be",
        "vimeo.com",
        "dailymotion.com",
        "twitch.tv",
        "reddit.com",
        "twitter.com", "x.com",
        "tiktok.com",
        "instagram.com",
        "facebook.com",
        "soundcloud.com",
        "bandcamp.com",
        "bilibili.com",
        "nicovideo.jp",
        "streamable.com",
        "rumble.com",
        "odysee.com",
    ];

    public void Dispose() => Stop();
}
