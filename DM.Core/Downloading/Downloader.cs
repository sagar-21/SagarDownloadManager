using DM.Core.Settings;
using System.Diagnostics;
using System.Text.Json;

namespace DM.Core.Downloading;

/// <summary>
/// Downloads a URL to a file, using parallel segments when the server supports
/// byte-range requests (HTTP 206 / Accept-Ranges: bytes).
///
/// Pause / resume:
///   A .dmstate JSON file sits next to the download while it is in progress.
///   Each segment records how many bytes it has completed.  On resume, the
///   Range header skips the bytes already on disk — no re-downloading.
///   The state file is deleted automatically on successful completion.
///   To truly cancel (not just pause), call <see cref="DeleteState"/> afterward.
/// </summary>
public sealed class Downloader
{
    private const int    BufferSize            = 81_920;            // 80 KB — below LOH threshold
    private const double ProgressIntervalSecs  = 0.25;             // 4 progress reports / second
    private const double StateSaveIntervalSecs = 3.0;              // how often .dmstate is flushed
    private const long   MultiSegmentThreshold = 1L * 1024 * 1024; // 1 MB minimum for multi-segment

    private static readonly JsonSerializerOptions StateJsonOptions = new()
    {
        WriteIndented      = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient     _http;
    private readonly EngineSettings _settings;

    public Downloader(HttpClient http, EngineSettings? settings = null)
    {
        _http     = http;
        _settings = settings ?? new EngineSettings();
    }

    // ── State file helpers (public so DownloadEngine can query / clean up) ─

    public static string GetStatePath(string destinationPath) =>
        destinationPath + ".dmstate";

    public static bool HasState(string destinationPath) =>
        File.Exists(GetStatePath(destinationPath));

    /// <summary>
    /// Call this after a true cancel (not a pause) to remove the leftover state file.
    /// On pause you deliberately keep the state file so resume can use it.
    /// </summary>
    public static void DeleteState(string destinationPath) =>
        TryDeleteFile(GetStatePath(destinationPath));

    // ── Public entry point ─────────────────────────────────────────────────

    public async Task DownloadAsync(
        string url,
        string destinationPath,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
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

    private async Task<ServerCapabilities> ProbeAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Head, url);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!res.IsSuccessStatusCode)
            return ServerCapabilities.None;

        bool supportsRanges = res.Headers.AcceptRanges.Contains("bytes");
        long contentLength  = res.Content.Headers.ContentLength ?? -1L;
        return new ServerCapabilities(supportsRanges && contentLength > 0, contentLength);
    }

    // ── Step 2a: multi-segment with state persistence ──────────────────────

    private async Task MultiSegmentDownloadAsync(
        string url,
        string destinationPath,
        long totalBytes,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        string statePath = GetStatePath(destinationPath);

        // Load saved state (resume) or create fresh state (new download).
        DownloadState state = await LoadOrCreateStateAsync(
            url, destinationPath, totalBytes, statePath, ct);

        int segCount = state.Segments.Length;

        // Pre-allocate the file only when starting fresh.
        // On resume the file already exists with the previously downloaded bytes intact.
        if (!File.Exists(destinationPath))
        {
            var dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await using var prealloc = new FileStream(
                destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            prealloc.SetLength(totalBytes);
        }

        // bytesCompleted[i] = total bytes done for segment i across all sessions.
        // Seeded from state so the aggregate progress starts from where we left off.
        var bytesCompleted = state.Segments.Select(s => s.BytesCompleted).ToArray();

        using var segCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var tasks = new Task[segCount];
        for (int i = 0; i < segCount; i++)
        {
            var idx             = i;                        // closure capture
            var seg             = state.Segments[i];
            var initialCompleted = seg.BytesCompleted;     // cross-session baseline

            if (seg.IsComplete)
            {
                tasks[idx] = Task.CompletedTask;           // skip already-finished segments
                continue;
            }

            tasks[idx] = DownloadSegmentWithRetryAsync(
                segmentIndex:    idx,
                startByte:       seg.StartByte,
                resumeFromByte:  seg.ResumeFromByte,       // = StartByte + BytesCompleted
                endByte:         seg.EndByte,
                url:             url,
                destinationPath: destinationPath,
                // onProgress receives session-only bytes; add baseline for the running total.
                onProgress: sessionBytes =>
                    Volatile.Write(ref bytesCompleted[idx], initialCompleted + sessionBytes),
                ct: segCts.Token);
        }

        // Progress reporter: sums bytesCompleted[] every 250 ms.
        using var reportCts = CancellationTokenSource.CreateLinkedTokenSource(segCts.Token);
        var reportTask = progress is null
            ? Task.CompletedTask
            : ReportAggregateProgressAsync(bytesCompleted, totalBytes, progress, reportCts.Token);

        // State persistence loop: flushes .dmstate every StateSaveIntervalSecs.
        // On cancel/pause, its finally block writes one last snapshot before returning.
        using var persistCts = CancellationTokenSource.CreateLinkedTokenSource(segCts.Token);
        var persistTask = PersistStateLoopAsync(
            state, bytesCompleted, statePath,
            TimeSpan.FromSeconds(StateSaveIntervalSecs), persistCts.Token);

        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
            await segCts.CancelAsync();
            await Task.WhenAll(tasks.Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default)));
            throw;
        }
        finally
        {
            // Stop persist loop — its own finally writes the final snapshot so the
            // state file reflects the exact pause/stop point.
            await persistCts.CancelAsync();
            await persistTask;                                 // wait for final save

            await reportCts.CancelAsync();
            await reportTask.ContinueWith(_ => { }, TaskScheduler.Default);
        }

        // Only reachable on full success — remove state file and report 100%.
        TryDeleteFile(statePath);
        progress?.Report(new DownloadProgress(totalBytes, totalBytes, SpeedBytesPerSecond: 0));
    }

    // ── State management ───────────────────────────────────────────────────

    private async Task<DownloadState> LoadOrCreateStateAsync(
        string url, string destinationPath, long totalBytes,
        string statePath, CancellationToken ct)
    {
        if (File.Exists(statePath))
        {
            try
            {
                await using var f = File.OpenRead(statePath);
                var saved = await JsonSerializer.DeserializeAsync<DownloadState>(
                    f, StateJsonOptions, ct);

                // Validate that the saved state matches this exact download request.
                if (saved is not null
                    && saved.Url        == url
                    && saved.TotalBytes == totalBytes
                    && saved.Segments.Length > 0)
                {
                    return saved;  // ← resume path
                }
            }
            catch { /* corrupted or incompatible state — fall through */ }
        }

        // Fresh download: compute segments and write the initial state file immediately
        // so even a very early crash leaves a recoverable .dmstate on disk.
        var ranges  = CalculateSegments(totalBytes, _settings.MaxConnectionsPerFile);
        var state   = new DownloadState
        {
            Url             = url,
            DestinationPath = destinationPath,
            TotalBytes      = totalBytes,
            Segments        = ranges.Select((r, i) => new SegmentState
            {
                Index          = i,
                StartByte      = r.Start,
                EndByte        = r.End,
                BytesCompleted = 0
            }).ToArray()
        };
        var zeros = new long[state.Segments.Length];  // all zeros
        await PersistStateAsync(state, zeros, statePath);
        return state;
    }

    /// <summary>
    /// Reads the live bytesCompleted[] counters, stamps them into the state object,
    /// then writes to a temp file and atomically renames it over the real state file.
    /// Atomic rename means a crash mid-write never leaves a corrupted .dmstate.
    /// </summary>
    private static async Task PersistStateAsync(
        DownloadState state, long[] bytesCompleted, string statePath)
    {
        for (int i = 0; i < state.Segments.Length; i++)
            state.Segments[i].BytesCompleted = Volatile.Read(ref bytesCompleted[i]);

        var tmpPath = statePath + ".tmp";
        await using (var stream = new FileStream(
            tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, state, StateJsonOptions);
            await stream.FlushAsync();
        }
        File.Move(tmpPath, statePath, overwrite: true);
    }

    private static async Task PersistStateLoopAsync(
        DownloadState state, long[] bytesCompleted,
        string statePath, TimeSpan interval, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(interval, ct);
                await PersistStateAsync(state, bytesCompleted, statePath);
            }
        }
        catch (OperationCanceledException) { }

        // Final snapshot: captures the exact pause/stop point regardless of
        // when the periodic save last ran.
        await PersistStateAsync(state, bytesCompleted, statePath);
    }

    // ── Segment retry wrapper ──────────────────────────────────────────────

    private async Task DownloadSegmentWithRetryAsync(
        int    segmentIndex,
        long   startByte,        // original range start (for error messages)
        long   resumeFromByte,   // where to actually start the request this session
        long   endByte,
        string url,
        string destinationPath,
        Action<long> onProgress,
        CancellationToken ct)
    {
        int        maxAttempts = _settings.RetryCount + 1;
        Exception? lastEx      = null;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (attempt > 0)
            {
                var delay = TimeSpan.FromMilliseconds(
                    _settings.RetryDelay.TotalMilliseconds * (1 << (attempt - 1)));
                await Task.Delay(delay, ct);

                // Reset session counter to 0 — the closure adds initialCompleted back,
                // so bytesCompleted[idx] snaps back to the cross-session baseline.
                onProgress(0);
            }

            try
            {
                await DownloadSegmentAsync(
                    segmentIndex, resumeFromByte, endByte, url, destinationPath, onProgress, ct);
                return;
            }
            catch (DownloadException ex) when (ex.Reason is not DownloadFailureReason.Cancelled)
            {
                lastEx = ex;
            }
        }

        throw new DownloadException(
            DownloadFailureReason.NetworkError,
            $"Segment {segmentIndex} (bytes {startByte}–{endByte}) failed after {maxAttempts} attempt(s).",
            lastEx);
    }

    // ── Single segment (one Range request) ────────────────────────────────

    /// <summary>
    /// Issues  Range: bytes=resumeFromByte-endByte  and writes the response body
    /// directly into the pre-allocated file starting at that same offset.
    ///
    /// HOW RESUME AVOIDS RE-DOWNLOADING DATA
    /// ──────────────────────────────────────
    /// Every byte before resumeFromByte is already correct on disk (written in a
    /// prior session).  The Range header tells the server to skip those bytes
    /// entirely; it streams only the remaining suffix:
    ///
    ///   Segment covers:  bytes 0 – 2,621,439   (2.5 MB)
    ///   Previous session wrote: 0 – 524,287    (512 KB done)
    ///   Resume request:  Range: bytes=524288-2621439
    ///   Server returns:  2,097,152 bytes (the remaining 2 MB)
    ///   File write seeks to offset 524288 and fills the rest in-place.
    ///
    /// The file therefore assembles without any copy or merge step.
    /// </summary>
    private async Task DownloadSegmentAsync(
        int    segmentIndex,
        long   resumeFromByte,
        long   endByte,
        string url,
        string destinationPath,
        Action<long> onProgress,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(resumeFromByte, endByte);

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
                await using var file = new FileStream(
                    destinationPath, FileMode.Open, FileAccess.Write,
                    FileShare.ReadWrite, BufferSize, useAsync: true);

                // Seek to the exact byte where writing should continue.
                file.Seek(resumeFromByte, SeekOrigin.Begin);
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

    // ── Step 2b: single-stream fallback (no state file) ───────────────────

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
                var  sw            = Stopwatch.StartNew();
                var  lastAt        = TimeSpan.Zero;
                var  lastBytes     = 0L;

                await WriteWithProgressAsync(body, file, bytes =>
                {
                    bytesReceived = bytes;
                    if (progress is null) return;

                    var now   = sw.Elapsed;
                    var delta = (now - lastAt).TotalSeconds;
                    if (delta < ProgressIntervalSecs) return;

                    progress.Report(new DownloadProgress(bytes, totalBytes, (bytes - lastBytes) / delta));
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

    // ── Shared stream helpers ──────────────────────────────────────────────

    private static async Task WriteWithProgressAsync(
        Stream source, Stream destination,
        Action<long> onProgress, CancellationToken ct)
    {
        var  buffer       = new byte[BufferSize];
        long bytesWritten = 0;

        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            bytesWritten += read;
            onProgress(bytesWritten);
        }
    }

    private static async Task ReportAggregateProgressAsync(
        long[] bytesCompleted, long totalBytes,
        IProgress<DownloadProgress> progress, CancellationToken ct)
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
                for (int i = 0; i < bytesCompleted.Length; i++)
                    current += Volatile.Read(ref bytesCompleted[i]);

                var deltaSecs = (now - lastTime).TotalSeconds;
                var speed     = deltaSecs > 0
                    ? Math.Max(0, (current - lastTotal) / deltaSecs)
                    : 0;

                progress.Report(new DownloadProgress(current, totalBytes, speed));
                lastTime  = now;
                lastTotal = current;
            }
        }
        catch (OperationCanceledException) { }
    }

    // ── Segment calculation ────────────────────────────────────────────────

    private static (long Start, long End)[] CalculateSegments(long totalBytes, int count)
    {
        var  segs    = new (long Start, long End)[count];
        long segSize = totalBytes / count;

        for (int i = 0; i < count; i++)
        {
            long start = i * segSize;
            long end   = (i == count - 1) ? totalBytes - 1 : start + segSize - 1;
            segs[i]    = (start, end);
        }

        return segs;
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private readonly record struct ServerCapabilities(bool SupportsRanges, long ContentLength)
    {
        public static ServerCapabilities None => new(false, -1);
    }
}
