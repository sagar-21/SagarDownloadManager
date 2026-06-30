using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading.Channels;
using DM.Core.RateLimiting;
using DM.Core.Settings;

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
    private const int    ChunkGranularity      = 4;                // target chunks = maxConns × this
    private const double AutoTuneGrowthThreshold = 0.10;           // 10% speed increase to add a conn

    private static readonly JsonSerializerOptions StateJsonOptions = new()
    {
        WriteIndented      = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient              _http;
    private readonly EngineSettings          _settings;
    private readonly TokenBucketRateLimiter? _globalLimiter;
    private readonly TokenBucketRateLimiter? _perLimiter;

    public Downloader(HttpClient http, EngineSettings? settings = null,
        TokenBucketRateLimiter? globalLimiter = null,
        TokenBucketRateLimiter? perLimiter    = null)
    {
        _http          = http;
        _settings      = settings ?? new EngineSettings();
        _globalLimiter = globalLimiter;
        _perLimiter    = perLimiter;
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

    // ── HTTP client factory ────────────────────────────────────────────────

    /// <summary>
    /// Returns an <see cref="HttpClient"/> tuned for parallel segment downloads:
    /// HTTP/2 preferred with graceful HTTP/1.1 fallback, connection pool sized
    /// to the number of parallel segments, and keep-alive connections reused
    /// across all segments of a file.
    ///
    /// Pass the result into the <see cref="Downloader"/> constructor.
    /// The caller owns the client and must dispose it.
    /// </summary>
    public static HttpClient CreateOptimizedHttpClient(EngineSettings? settings = null)
    {
        var s = settings ?? new EngineSettings();
        var handler = new SocketsHttpHandler
        {
            // Allow more than one TCP connection to the same H2 server so that
            // parallel Range requests are not serialised onto a single connection.
            EnableMultipleHttp2Connections = true,
            // Size the pool to the max number of simultaneous connections per file
            // plus a small headroom for the HEAD probe.
            MaxConnectionsPerServer        = s.MaxConnectionsPerFile + 2,
            PooledConnectionLifetime       = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout    = TimeSpan.FromMinutes(1),
        };
        return new HttpClient(handler)
        {
            Timeout                = s.ConnectionTimeout,
            DefaultRequestVersion  = HttpVersion.Version20,
            DefaultVersionPolicy   = HttpVersionPolicy.RequestVersionOrLower,
        };
    }

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
        using var req = new HttpRequestMessage(HttpMethod.Head, url)
        {
            Version       = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!res.IsSuccessStatusCode)
            return ServerCapabilities.None;

        bool supportsRanges = res.Headers.AcceptRanges.Contains("bytes");
        long contentLength  = res.Content.Headers.ContentLength ?? -1L;
        return new ServerCapabilities(supportsRanges && contentLength > 0, contentLength);
    }

    // ── Step 2a: multi-segment with work-stealing queue and auto-tune ─────────

    private async Task MultiSegmentDownloadAsync(
        string url,
        string destinationPath,
        long totalBytes,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        string statePath = GetStatePath(destinationPath);
        DownloadState state = await LoadOrCreateStateAsync(
            url, destinationPath, totalBytes, statePath, ct);

        if (!File.Exists(destinationPath))
        {
            // State file survived but destination is gone (user deleted partial file,
            // or crash between state write and SetLength).  Stale BytesCompleted values
            // would cause segments to skip re-downloading their prefixes, leaving zeros
            // in those ranges.  Reset all progress so every segment starts fresh.
            foreach (var seg in state.Segments)
                seg.BytesCompleted = 0;

            var dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await using var prealloc = new FileStream(
                destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            prealloc.SetLength(totalBytes);
        }

        var bytesCompleted = state.Segments.Select(s => s.BytesCompleted).ToArray();

        // Over-partitioned chunk queue — work-stealing is implicit: a fast worker
        // simply dequeues the next pending chunk rather than sitting idle.
        var queue = new ConcurrentQueue<int>(
            Enumerable.Range(0, state.Segments.Length)
                      .Where(i => !state.Segments[i].IsComplete));

        using var segCts    = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // workerCts gates the semaphore; cancelled by the last active worker (success)
        // or by segCts propagation (error / user cancel).
        using var workerCts = CancellationTokenSource.CreateLinkedTokenSource(segCts.Token);

        int maxConns     = _settings.MaxConnectionsPerFile;
        int initialConns = _settings.AutoTuneConnections
            ? Math.Clamp(_settings.InitialConnectionsPerFile, 1, maxConns)
            : maxConns;

        // Gate controls how many workers run concurrently.
        // Auto-tuner calls Release() to wake dormant workers one at a time.
        using var gate = new SemaphoreSlim(initialConns, maxConns);

        async Task RunWorkerAsync()
        {
            try   { await gate.WaitAsync(workerCts.Token); }
            catch (OperationCanceledException) { return; }

            while (queue.TryDequeue(out int segIdx))
            {
                var seg              = state.Segments[segIdx];
                var initialCompleted = seg.BytesCompleted;

                await DownloadSegmentWithRetryAsync(
                    segIdx, seg.StartByte, seg.ResumeFromByte, seg.EndByte,
                    url, destinationPath,
                    sessionBytes => Volatile.Write(ref bytesCompleted[segIdx],
                                                   initialCompleted + sessionBytes),
                    segCts.Token);
            }

            // Queue drained — unblock any dormant workers still waiting at the gate.
            workerCts.Cancel(throwOnFirstException: false);
        }

        // Pre-spawn all potential workers. Only initialConns pass the gate right away;
        // the rest block until the auto-tuner releases slots or work finishes.
        var workerTasks = Enumerable.Range(0, maxConns)
            .Select(_ => RunWorkerAsync())
            .ToArray();

        using var reportCts = CancellationTokenSource.CreateLinkedTokenSource(segCts.Token);
        var reportTask = progress is null
            ? Task.CompletedTask
            : ReportAggregateProgressAsync(bytesCompleted, totalBytes, progress, reportCts.Token);

        using var persistCts = CancellationTokenSource.CreateLinkedTokenSource(segCts.Token);
        var persistTask = PersistStateLoopAsync(
            state, bytesCompleted, statePath,
            TimeSpan.FromSeconds(StateSaveIntervalSecs), persistCts.Token);

        using var tuneCts = CancellationTokenSource.CreateLinkedTokenSource(segCts.Token);
        var tuneTask = (_settings.AutoTuneConnections && initialConns < maxConns)
            ? AutoTuneConnectionsAsync(gate, bytesCompleted, maxConns, initialConns, tuneCts.Token)
            : Task.CompletedTask;

        try
        {
            await Task.WhenAll(workerTasks);
        }
        catch
        {
            await segCts.CancelAsync();
            await Task.WhenAll(workerTasks.Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default)));
            throw;
        }
        finally
        {
            await tuneCts.CancelAsync();
            await tuneTask.ContinueWith(_ => { }, TaskScheduler.Default);

            await persistCts.CancelAsync();
            await persistTask;

            await reportCts.CancelAsync();
            await reportTask.ContinueWith(_ => { }, TaskScheduler.Default);
        }

        TryDeleteFile(statePath);
        progress?.Report(new DownloadProgress(totalBytes, totalBytes, SpeedBytesPerSecond: 0));
    }

    // ── Auto-tune: ramps up connections while throughput is growing ────────────

    private async Task AutoTuneConnectionsAsync(
        SemaphoreSlim gate, long[] bytesCompleted,
        int maxConns, int activeConns, CancellationToken ct)
    {
        long   prevTotal = 0;
        double lastSpeed = 0;

        try
        {
            while (activeConns < maxConns)
            {
                await Task.Delay(_settings.AutoTuneInterval, ct);

                long currentTotal = 0;
                for (int i = 0; i < bytesCompleted.Length; i++)
                    currentTotal += Volatile.Read(ref bytesCompleted[i]);

                double intervalSpeed = (currentTotal - prevTotal) / _settings.AutoTuneInterval.TotalSeconds;

                // First sample has no baseline — always try to grow.
                // After that, grow only when speed is still increasing.
                if (lastSpeed == 0 || intervalSpeed > lastSpeed * (1.0 + AutoTuneGrowthThreshold))
                {
                    gate.Release();
                    activeConns++;
                }

                prevTotal = currentTotal;
                lastSpeed = intervalSpeed;
            }
        }
        catch (OperationCanceledException) { }
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
        var ranges  = CalculateChunks(totalBytes, _settings.MaxConnectionsPerFile, _settings.MinChunkBytes);
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
                // Full jitter: multiply by [0.5, 1.0) so concurrent retrying segments
                // stagger their requests instead of thundering-herding the server.
                double jitter = 0.5 + Random.Shared.NextDouble() * 0.5;
                double rawMs  = _settings.RetryDelay.TotalMilliseconds * (1 << (attempt - 1)) * jitter;
                var    delay  = TimeSpan.FromMilliseconds(
                    Math.Min(rawMs, _settings.RetryDelayMax.TotalMilliseconds));
                await Task.Delay(delay, ct);

                onProgress(0);
            }

            try
            {
                await DownloadSegmentAsync(
                    segmentIndex, resumeFromByte, endByte, url, destinationPath, onProgress, ct);
                return;
            }
            catch (DownloadException ex) when (ex.Reason is not DownloadFailureReason.Cancelled
                                                          and not DownloadFailureReason.IoError)
            {
                // IoError (disk full, write failure) is not a transient network condition;
                // retrying would waste bandwidth and always fail the same way.
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
        using var req = new HttpRequestMessage(HttpMethod.Get, url)
        {
            // Prefer HTTP/2; fall back to HTTP/1.1 transparently.
            Version       = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        req.Headers.Range = new RangeHeaderValue(resumeFromByte, endByte);

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
                // bufferSize:1 suppresses FileStream's internal kernel buffer; the channel
                // already buffers data in pooled arrays so double-buffering wastes ~80 KB
                // of kernel memory per open segment with no throughput benefit.
                await using var file = new FileStream(
                    destinationPath, FileMode.Open, FileAccess.Write,
                    FileShare.ReadWrite, bufferSize: 1, useAsync: true);

                file.Seek(resumeFromByte, SeekOrigin.Begin);
                await PipeSegmentAsync(body, file, _settings.RamBufferChunks, onProgress,
                    _globalLimiter, _perLimiter, ct);
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
                }, _globalLimiter, _perLimiter, ct);

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
        Action<long> onProgress,
        TokenBucketRateLimiter? globalLimiter,
        TokenBucketRateLimiter? perLimiter,
        CancellationToken ct)
    {
        var  buffer       = new byte[BufferSize];
        long bytesWritten = 0;

        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            if (globalLimiter is not null) await globalLimiter.WaitAsync(read, ct);
            if (perLimiter    is not null) await perLimiter.WaitAsync(read, ct);
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            bytesWritten += read;
            onProgress(bytesWritten);
        }
    }

    // ── RAM-buffered pipe (network → channel → disk) ───────────────────────

    /// <summary>
    /// Pipes <paramref name="source"/> to <paramref name="destination"/> through a
    /// bounded channel of pooled byte arrays, decoupling network reads from disk writes.
    ///
    /// MEMORY SAFETY — every rented buffer is returned to <see cref="ArrayPool{T}.Shared"/>:
    ///   • Happy path : by <see cref="DrainChannelAsync"/> after each successful write.
    ///   • Error/cancel: by the catch block after both tasks settle, via a drain loop on
    ///                   any items still sitting in the channel.
    ///
    /// EXCEPTION FIDELITY — if both fill and drain fault, both exceptions are surfaced:
    ///   • Single fault  → original exception re-thrown with original stack trace preserved.
    ///   • Double fault  → <see cref="AggregateException"/> so neither cause is silently lost.
    ///   • Both cancel   → <see cref="OperationCanceledException"/> re-thrown as-is.
    /// </summary>
    private static async Task PipeSegmentAsync(
        Stream source, Stream destination,
        int channelSlots, Action<long> onProgress,
        TokenBucketRateLimiter? globalLimiter, TokenBucketRateLimiter? perLimiter,
        CancellationToken ct)
    {
        var channel = Channel.CreateBounded<(byte[] Buffer, int Count)>(
            new BoundedChannelOptions(channelSlots)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode     = BoundedChannelFullMode.Wait,
            });

        using var pipeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var fillTask  = FillChannelAsync(source, channel.Writer, pipeCts.Token);
        var drainTask = DrainChannelAsync(channel.Reader, destination, onProgress,
            globalLimiter, perLimiter, pipeCts.Token);

        try
        {
            await Task.WhenAll(fillTask, drainTask);
            // Success: channel is fully drained, no pooled buffers remain in it.
        }
        catch
        {
            // Cancel the surviving side and wait for it to exit before touching the
            // channel — prevents use-after-return races on pooled buffers.
            await pipeCts.CancelAsync();
            await Task.WhenAll(
                fillTask .ContinueWith(_ => { }, TaskScheduler.Default),
                drainTask.ContinueWith(_ => { }, TaskScheduler.Default));

            // Return any pooled buffers still in the channel (only reachable on error).
            while (channel.Reader.TryRead(out var leftover))
                ArrayPool<byte>.Shared.Return(leftover.Buffer);

            // Surface all real exceptions; don't silently discard a concurrent fault.
            var faults = new[] { fillTask, drainTask }
                .Where(t => t.IsFaulted)
                .SelectMany(t => t.Exception!.InnerExceptions)
                .ToList();

            if (faults.Count == 1) ExceptionDispatchInfo.Capture(faults[0]).Throw();
            if (faults.Count > 1)  throw new AggregateException(faults);
            throw; // both cancelled — re-throw the OperationCanceledException
        }
    }

    private static async Task FillChannelAsync(
        Stream source,
        ChannelWriter<(byte[] Buffer, int Count)> writer,
        CancellationToken ct)
    {
        try
        {
            while (true)
            {
                byte[] buf = ArrayPool<byte>.Shared.Rent(BufferSize);
                try
                {
                    // Inner try owns buf from rent through channel enqueue.
                    // If ReadAsync or WriteAsync throws (including cancellation while
                    // the bounded channel is full), we return buf before propagating.
                    int read = await source.ReadAsync(buf.AsMemory(0, BufferSize), ct);
                    if (read == 0) { ArrayPool<byte>.Shared.Return(buf); break; }
                    await writer.WriteAsync((buf, read), ct);
                }
                catch
                {
                    ArrayPool<byte>.Shared.Return(buf);
                    throw;
                }
            }
            writer.TryComplete();
        }
        catch (Exception ex)
        {
            // Fault the channel so DrainChannelAsync unblocks and sees the error.
            writer.TryComplete(ex);
            throw;
        }
    }

    private static async Task DrainChannelAsync(
        ChannelReader<(byte[] Buffer, int Count)> reader,
        Stream destination,
        Action<long> onProgress,
        TokenBucketRateLimiter? globalLimiter,
        TokenBucketRateLimiter? perLimiter,
        CancellationToken ct)
    {
        long bytesWritten = 0;
        await foreach (var (buf, count) in reader.ReadAllAsync(ct))
        {
            if (globalLimiter is not null) await globalLimiter.WaitAsync(count, ct);
            if (perLimiter    is not null) await perLimiter.WaitAsync(count, ct);
            try   { await destination.WriteAsync(buf.AsMemory(0, count), ct); }
            finally { ArrayPool<byte>.Shared.Return(buf); }

            bytesWritten += count;
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

    // ── Chunk calculation ──────────────────────────────────────────────────

    /// <summary>
    /// Divides <paramref name="totalBytes"/> into over-partitioned chunks.
    /// Using ChunkGranularity × maxConns target chunks means a fast worker that
    /// finishes its first chunk early can dequeue more — equivalent to work-stealing.
    /// The count is also floored at maxConns so every connection always has work on
    /// small files where minChunkBytes would otherwise yield fewer chunks than workers.
    /// </summary>
    private static (long Start, long End)[] CalculateChunks(
        long totalBytes, int maxConns, long minChunkBytes)
    {
        long targetChunkSize = Math.Max(minChunkBytes,
            totalBytes / ((long)maxConns * ChunkGranularity));
        int count = Math.Max(maxConns,
            (int)Math.Ceiling((double)totalBytes / targetChunkSize));

        long chunkSize = totalBytes / count;
        var  chunks    = new (long Start, long End)[count];
        for (int i = 0; i < count; i++)
        {
            long start = i * chunkSize;
            long end   = (i == count - 1) ? totalBytes - 1 : start + chunkSize - 1;
            chunks[i]  = (start, end);
        }
        return chunks;
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
