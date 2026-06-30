using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;

namespace DM.App.Services;

/// <summary>
/// Lightweight localhost-only HTTP server that receives media URLs from the
/// browser extension and turns them into download requests.
///
/// Security model:
///   • Binds ONLY to http://localhost:{port}/ — the OS never routes external
///     traffic here; remote connections are impossible.
///   • Belt-and-suspenders: each request's RemoteEndPoint is verified to be
///     127.0.0.1 / ::1; anything else gets 403.
///   • CORS headers include Access-Control-Allow-Private-Network: true so
///     Chrome's Private Network Access (PNA) preflight succeeds for content
///     scripts running in page context.
///
/// Endpoints:
///   GET  /api/ping     → { "ok": true, "app": "DownloadManager" }
///   POST /api/download → { "url": "...", "pageUrl": "...", "title": "..." }
///                      ← { "ok": true }
/// </summary>
public sealed class LocalConnectorServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private HttpListener?            _listener;
    private CancellationTokenSource? _cts;

    public int  Port      { get; private set; }
    public bool IsRunning { get; private set; }

    /// <summary>Fired on a thread-pool thread when the extension sends a download request.</summary>
    public event Action<ConnectorRequest>? DownloadRequested;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Start(int port)
    {
        Stop();
        Port = port;
        _cts = new CancellationTokenSource();

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");

        _listener.Start();
        IsRunning = true;
        _ = ListenAsync(_cts.Token);
    }

    public void Stop()
    {
        if (!IsRunning) return;
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
        IsRunning = false;
    }

    // ── Request loop ─────────────────────────────────────────────────────────

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener!.GetContextAsync();
            }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { continue; }

            _ = Task.Run(() => HandleRequest(ctx), ct);
        }
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;

        // Belt-and-suspenders: reject non-loopback (defence against unlikely OS misconfig)
        var remote = req.RemoteEndPoint.Address;
        if (!IPAddress.IsLoopback(remote))
        {
            res.StatusCode = 403;
            res.Close();
            return;
        }

        // CORS headers — needed for browser extension content scripts
        res.AddHeader("Access-Control-Allow-Origin",          "*");
        res.AddHeader("Access-Control-Allow-Methods",         "GET, POST, OPTIONS");
        res.AddHeader("Access-Control-Allow-Headers",         "Content-Type");
        res.AddHeader("Access-Control-Allow-Private-Network", "true"); // Chrome PNA preflight

        if (req.HttpMethod == "OPTIONS")
        {
            res.StatusCode = 204;
            res.Close();
            return;
        }

        var path = req.Url?.AbsolutePath ?? "/";

        if (req.HttpMethod == "GET" && path == "/api/ping")
        {
            Respond(res, 200, """{"ok":true,"app":"DownloadManager","version":"1.0"}""");
            return;
        }

        if (req.HttpMethod == "POST" && path == "/api/download")
        {
            HandleDownloadRequest(req, res);
            return;
        }

        Respond(res, 404, """{"ok":false,"error":"not found"}""");
    }

    // URL path segments that are internal site assets, not downloadable content.
    private static readonly string[] BlockedUrlPaths =
    [
        "/s/search/", "/generate_204", "/pagead/", "/favicon",
        "/pcs/activeview", "/log_event", "/youtubei/", "/api/stats",
        "/videoplayback?", // raw YouTube CDN fragment (use page URL instead)
    ];

    private static bool IsInternalAssetUrl(string url)
    {
        try
        {
            var path = new Uri(url).AbsolutePath;
            return Array.Exists(BlockedUrlPaths, p => url.Contains(p, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private void HandleDownloadRequest(HttpListenerRequest req, HttpListenerResponse res)
    {
        try
        {
            using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
            var body = reader.ReadToEnd();
            var dto  = JsonSerializer.Deserialize<DownloadDto>(body, JsonOpts);

            if (dto?.Url is string url
                && !string.IsNullOrWhiteSpace(url)
                && Uri.TryCreate(url, UriKind.Absolute, out _)
                && !IsInternalAssetUrl(url))
            {
                DownloadRequested?.Invoke(new ConnectorRequest(
                    Url:     url,
                    PageUrl: dto.PageUrl ?? "",
                    Title:   dto.Title   ?? "",
                    Quality: dto.Quality ?? ""));

                Respond(res, 200, """{"ok":true}""");
            }
            else
            {
                Respond(res, 400, """{"ok":false,"error":"invalid or missing url"}""");
            }
        }
        catch
        {
            Respond(res, 400, """{"ok":false,"error":"invalid request body"}""");
        }
    }

    private static void Respond(HttpListenerResponse res, int code, string json)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            res.StatusCode      = code;
            res.ContentType     = "application/json";
            res.ContentLength64 = bytes.Length;
            res.OutputStream.Write(bytes);
            res.Close();
        }
        catch { res.Abort(); }
    }

    private sealed record DownloadDto(string? Url, string? PageUrl, string? Title, string? Quality);

    public void Dispose() => Stop();
}

// ── Public API ────────────────────────────────────────────────────────────────

/// <summary>Parsed download request from the browser extension.</summary>
public sealed record ConnectorRequest(string Url, string PageUrl, string Title, string Quality);
