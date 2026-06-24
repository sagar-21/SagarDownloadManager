using DM.Core.Settings;
using System.Diagnostics;

namespace DM.Core.Downloading;

/// <summary>
/// Downloads a URL to a file, using parallel segments when the server supports
/// byte-range requests (HTTP 206 / Accept-Ranges: bytes).
///
/// Strategy:
///   1. HEAD probe → detect Accept-Ranges + Content-Length.
///   2a. Multi-segment: pre-allocate the file, spawn N concurrent tasks each
///       fetching its slice via Range header, writing directly at its offset.
///       No temp files, no merge step — writes land in the right place immediately.
///   2b. Single-stream fallback: plain streaming GET (server doesn't support
///       ranges, file is below threshold, or MaxConnectionsPerFile = 1).
/// </summary>
public sealed class Downloader
{
    private const int    BufferSize             = 81_920;           // 80 KB — below LOH threshold
    private const double ProgressIntervalSecs   = 0.25;            // 4 reports / second
    private const long   MultiSegmentThreshold  = 1L * 1024 * 1024; // 1 MB minimum

    private readonly HttpClient    _http;
    private readonly EngineSettings _settings;

    // settings is optional so existing callers (tests) that pass only HttpClient still compile.
    public Downloader(HttpClient http, EngineSettings? settings = null)
    {
        _http     = http;
        _settings = settings ?? new EngineSettings();
    }

    // ── Public entry point ─────────────────────────────────────────────────

    public async Task DownloadAsync(
        string url,
        string destinationPath,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        // Probe errors (network, timeout, cancel) are caught here and wrapped
        // so callers always see DownloadException, never raw HTTP exceptions.
        ServerCapabilities cap;
        try
        {
            cap = await ProbeAsync(url, ct);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new DownloadException(DownloadFailureReason.Timeout,
                $"HEAD probe to '{url}' timed out.", ex);
        }
        catch (OperationCanceledException ex)
        {
            throw new DownloadException(DownloadFailureReason.Cancelled,
                "Download was cancelled.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new DownloadException(DownloadFailureReason.NetworkError,
                $"Network error probing '{url}': {ex.Message}", ex);
        }

        bool useMultiSegment =
            cap.SupportsRanges &&
            cap.ContentLength >= MultiSegmentThreshold &&
            _settings.MaxConnectionsPerFile > 1;

        if (useMultiSegment)
            await MultiSegmentDownloadAsync(url, destinationPath, cap.ContentLength, progress, ct);
        else
            await SingleSegmentDownloadAsync(url, destinationPath, progress, ct);
    }

    // ── Step 1: HEAD probe ─────────────────────────────────────────────────

    /// <summary>
    /// Sends a HEAD request to learn whether the server supports byte-range
    /// requests and how large the file is.  Returns <see cref="ServerCapabilities.None"/>
    /// on any non-success HTTP status (triggering single-stream fallback), but
    /// propagates network errors and cancellation so DownloadAsync can wrap them.
    /// </summary>
    private async Task<ServerCapabilities> ProbeAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Head, url);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!res.IsSuccessStatusCode)        // 404, 405, 5xx … → fall back silently
            return ServerCapabilities.None;

        bool supportsRanges = res.Headers.AcceptRanges.Contains("bytes");
        long contentLength  = res.Content.Headers.ContentLength ?? -1L;

        // Both conditions required: no Content-Length means we can't pre-allocate.
        return new ServerCapabilities(supportsRanges && contentLength > 0, contentLength);
    }

    // ── Step 2a: multi-segment ─────────────────────────────────────────────

    private async Task MultiSegmentDownloadAsync(
        string url,
        string destinationPath,
        long totalBytes,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        int segCount = _settings.MaxConnectionsPerFile;
        var segments = CalculateSegments(totalBytes, segCount);

        // Pre-allocate the full file so every segment can seek to its byte offset
        // and write in-place concurrently.  SetLength on NTFS is near-instant.
        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await using (var prealloc = new FileStream(
            destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            prealloc.SetLength(totalBytes);
        }

        // One slot per segment; each segment atomically updates only its own slot.
        var bytesPerSegment = new long[segCount];

        // Linked CTS: if one segment fails permanently, we abort the rest.
        using var segCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var tasks = new Task[segCount];
        for (int i = 0; i < segCount; i++)
        {
            var idx   = i;                        // capture by value (closure safety)
            var start = segments[i].Start;
            var end   = segments[i].End;
            tasks[i]  = DownloadSegmentWithRetryAsync(
                segmentIndex:    idx,
                startByte:       start,
                endByte:         end,
                url:             url,
                destinationPath: destinationPath,
                onProgress:      bytes => Volatile.Write(ref bytesPerSegment[idx], bytes),
                ct:              segCts.Token);
        }

        // Run a progress reporter in parallel with the download tasks.
        using var reportCts = CancellationTokenSource.CreateLinkedTokenSource(segCts.Token);
        var reportTask = progress is null
            ? Task.CompletedTask
            : ReportAggregateProgressAsync(bytesPerSegment, totalBytes, progress, reportCts.Token);

        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
            // A segment exhausted its retries — cancel all peers.
            await segCts.CancelAsync();
            // Drain so exceptions don't become unobserved faults.
            await Task.WhenAll(tasks.Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default)));
            throw;
        }
        finally
        {
            await reportCts.CancelAsync();
            await reportTask.ContinueWith(_ => { }, TaskScheduler.Default);
        }

        // Emit one clean 100% snapshot.
        progress?.Report(new DownloadProgress(totalBytes, totalBytes, SpeedBytesPerSecond: 0));
    }

    // ── Segment retry wrapper ──────────────────────────────────────────────

    private async Task DownloadSegmentWithRetryAsync(
        int    segmentIndex,
        long   startByte,
        long   endByte,
        string url,
        string destinationPath,
        Action<long> onProgress,
        CancellationToken ct)
    {
        int maxAttempts = _settings.RetryCount + 1;
        Exception? lastEx = null;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (attempt > 0)
            {
                // Exponential back-off: base × 2^(attempt−1).
                // Attempt 1 → base, 2 → 2× base, 3 → 4× base …
                var delay = TimeSpan.FromMilliseconds(
                    _settings.RetryDelay.TotalMilliseconds * (1 << (attempt - 1)));
                await Task.Delay(delay, ct);

                // Reset this segment's counter so the UI doesn't jump backward
                // when the retried segment re-downloads bytes it had already counted.
                onProgress(0);
            }

            try
            {
                await DownloadSegmentAsync(
                    segmentIndex, startByte, endByte, url, destinationPath, onProgress, ct);
                return; // success — exit retry loop
            }
            catch (DownloadException ex) when (ex.Reason is not DownloadFailureReason.Cancelled)
            {
                lastEx = ex; // retryable — loop continues
            }
            // Cancellation is NOT retried; it re-throws immediately.
        }

        throw new DownloadException(
            DownloadFailureReason.NetworkError,
            $"Segment {segmentIndex} (bytes {startByte}–{endByte}) failed after {maxAttempts} attempt(s).",
            lastEx);
    }

    // ── Single segment (one Range request) ────────────────────────────────

    /// <summary>
    /// Fetches the byte range [startByte, endByte] from the server using an
    /// HTTP Range header, then writes the body into the pre-allocated file
    /// at the correct byte offset.
    ///
    /// ┌──────────────────────────────────────────────────────────────────┐
    /// │  HOW THE RANGE HEADER WORKS                                      │
    /// │                                                                  │
    /// │  Request:  Range: bytes=&lt;start&gt;-&lt;end&gt;                            │
    /// │  • Both values are *inclusive* and zero-indexed.                 │
    /// │  • The server replies with HTTP 206 Partial Content.             │
    /// │  • Response header: Content-Range: bytes &lt;start&gt;-&lt;end&gt;/&lt;total&gt;  │
    /// │                                                                  │
    /// │  Example — 10 MB file, 4 segments (bytes, zero-indexed):        │
    /// │  Seg 0   Range: bytes=0-2621439         (0 → 2.5 MB − 1)       │
    /// │  Seg 1   Range: bytes=2621440-5242879   (2.5 MB → 5 MB − 1)    │
    /// │  Seg 2   Range: bytes=5242880-7864319   (5 MB → 7.5 MB − 1)    │
    /// │  Seg 3   Range: bytes=7864320-10485759  (7.5 MB → 10 MB − 1)   │
    /// │                                                                  │
    /// │  The last segment's end = totalBytes − 1 (inclusive).           │
    /// │  Each response body contains exactly (end − start + 1) bytes.   │
    /// │  Those bytes are written at file offset = start, so the final   │
    /// │  file is assembled in-place without a separate merge step.      │
    /// └──────────────────────────────────────────────────────────────────┘
    /// </summary>
    private async Task DownloadSegmentAsync(
        int    segmentIndex,
        long   startByte,
        long   endByte,
        string url,
        string destinationPath,
        Action<long> onProgress,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(startByte, endByte);

        HttpResponseMessage res;
        try
        {
            res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new DownloadException(DownloadFailureReason.Timeout,
                $"Segment {segmentIndex} timed out.", ex);
        }
        catch (OperationCanceledException ex)
        {
            throw new DownloadException(DownloadFailureReason.Cancelled, "Download was cancelled.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new DownloadException(DownloadFailureReason.NetworkError,
                $"Segment {segmentIndex} network error: {ex.Message}", ex);
        }

        using (res)
        {
            if (!res.IsSuccessStatusCode)
                throw new DownloadException(DownloadFailureReason.ServerError,
                    $"Segment {segmentIndex}: server returned {(int)res.StatusCode}.",
                    statusCode: (int)res.StatusCode);

            try
            {
                await using var body = await res.Content.ReadAsStreamAsync(ct);

                // Open the pre-allocated file and position at this segment's start.
                // FileShare.ReadWrite lets all concurrent segment writers coexist;
                // they write to disjoint byte ranges so there is no contention.
                await using var file = new FileStream(
                    destinationPath, FileMode.Open, FileAccess.Write,
                    FileShare.ReadWrite, BufferSize, useAsync: true);
                file.Seek(startByte, SeekOrigin.Begin);

                await WriteWithProgressAsync(body, file, onProgress, ct);
            }
            catch (OperationCanceledException ex)
            {
                throw new DownloadException(DownloadFailureReason.Cancelled, "Download was cancelled.", ex);
            }
            catch (IOException ex)
            {
                throw new DownloadException(DownloadFailureReason.IoError,
                    $"Segment {segmentIndex} disk write failed: {ex.Message}", ex);
            }
        }
    }

    // ── Step 2b: single-stream fallback ───────────────────────────────────

    private async Task SingleSegmentDownloadAsync(
        string url,
        string destinationPath,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        HttpResponseMessage res;
        try
        {
            res = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new DownloadException(DownloadFailureReason.Timeout,
                $"Request to '{url}' timed out.", ex);
        }
        catch (OperationCanceledException ex)
        {
            throw new DownloadException(DownloadFailureReason.Cancelled, "Download was cancelled.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new DownloadException(DownloadFailureReason.NetworkError,
                $"Network error reaching '{url}': {ex.Message}", ex);
        }

        using (res)
        {
            if (!res.IsSuccessStatusCode)
                throw new DownloadException(DownloadFailureReason.ServerError,
                    $"Server returned {(int)res.StatusCode} {res.ReasonPhrase} for '{url}'.",
                    statusCode: (int)res.StatusCode);

            long totalBytes = res.Content.Headers.ContentLength ?? -1L;

            try
            {
                await using var body = await res.Content.ReadAsStreamAsync(ct);
                await using var file = new FileStream(
                    destinationPath, FileMode.Create, FileAccess.Write,
                    FileShare.None, BufferSize, useAsync: true);

                long bytesReceived = 0;
                var sw = Stopwatch.StartNew();
                var lastAt    = TimeSpan.Zero;
                var lastBytes = 0L;

                await WriteWithProgressAsync(body, file, bytes =>
                {
                    bytesReceived = bytes;
                    if (progress is null) return;

                    var now = sw.Elapsed;
                    var delta = (now - lastAt).TotalSeconds;
                    if (delta < ProgressIntervalSecs) return;

                    var speed = (bytes - lastBytes) / delta;
                    progress.Report(new DownloadProgress(bytes, totalBytes, speed));
                    lastAt    = now;
                    lastBytes = bytes;
                }, ct);

                progress?.Report(new DownloadProgress(bytesReceived, totalBytes, 0));
            }
            catch (OperationCanceledException ex)
            {
                throw new DownloadException(DownloadFailureReason.Cancelled, "Download was cancelled.", ex);
            }
            catch (IOException ex)
            {
                throw new DownloadException(DownloadFailureReason.IoError,
                    $"Failed writing to '{destinationPath}': {ex.Message}", ex);
            }
        }
    }

    // ── Shared helpers ─────────────────────────────────────────────────────

    private static async Task WriteWithProgressAsync(
        Stream source,
        Stream destination,
        Action<long> onProgress,
        CancellationToken ct)
    {
        var buffer        = new byte[BufferSize];
        long bytesWritten = 0;

        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            bytesWritten += read;
            onProgress(bytesWritten);
        }
    }

    /// <summary>
    /// Runs as a background task alongside the segment tasks, summing all
    /// segment byte-counts every 250 ms and reporting aggregate speed.
    /// </summary>
    private static async Task ReportAggregateProgressAsync(
        long[] bytesPerSegment,
        long totalBytes,
        IProgress<DownloadProgress> progress,
        CancellationToken ct)
    {
        var sw        = Stopwatch.StartNew();
        var lastTime  = TimeSpan.Zero;
        var lastTotal = 0L;

        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(ProgressIntervalSecs), ct);

                var now     = sw.Elapsed;
                var current = 0L;
                for (int i = 0; i < bytesPerSegment.Length; i++)
                    current += Volatile.Read(ref bytesPerSegment[i]);

                var deltaSecs = (now - lastTime).TotalSeconds;
                // Clamp to 0: a segment reset on retry can temporarily push current below lastTotal.
                var speed = deltaSecs > 0 ? Math.Max(0, (current - lastTotal) / deltaSecs) : 0;

                progress.Report(new DownloadProgress(current, totalBytes, speed));
                lastTime  = now;
                lastTotal = current;
            }
        }
        catch (OperationCanceledException) { /* normal shutdown — all segments finished */ }
    }

    /// <summary>
    /// Splits <paramref name="totalBytes"/> into <paramref name="count"/> contiguous,
    /// non-overlapping ranges.  The last segment absorbs any remainder bytes.
    ///
    /// Example: 10 MB (10,485,760 bytes), 4 segments, segSize = 2,621,440:
    ///   (0, 2621439) | (2621440, 5242879) | (5242880, 7864319) | (7864320, 10485759)
    /// </summary>
    private static (long Start, long End)[] CalculateSegments(long totalBytes, int count)
    {
        var    segs    = new (long Start, long End)[count];
        long segSize   = totalBytes / count;

        for (int i = 0; i < count; i++)
        {
            long start = i * segSize;
            long end   = (i == count - 1) ? totalBytes - 1 : start + segSize - 1;
            segs[i]    = (start, end);
        }

        return segs;
    }

    private readonly record struct ServerCapabilities(bool SupportsRanges, long ContentLength)
    {
        public static ServerCapabilities None => new(false, -1);
    }
}
