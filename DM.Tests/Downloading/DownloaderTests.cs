using System.Net;
using System.Text.Json;
using DM.Core.Downloading;
using DM.Core.Settings;

namespace DM.Tests.Downloading;

public sealed class DownloaderTests
{
    private static readonly JsonSerializerOptions CamelCaseJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented        = true,
    };

    // ── Happy path ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DownloadAsync_WritesCorrectBytesAndReportsProgress()
    {
        // 300 KB of fake content with a known Content-Length header so we can
        // verify both file integrity and the Percentage calculation.
        const int size = 300_000;
        var fakeBytes = new byte[size];
        Random.Shared.NextBytes(fakeBytes);

        using var client = MakeClient((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(fakeBytes) { Headers = { ContentLength = size } }
        });

        var dest = TempFilePath();
        var reports = new List<DownloadProgress>();

        try
        {
            await new Downloader(client).DownloadAsync(
                "https://fake/file.bin", dest,
                // SyncProgress calls the action directly on the reporting thread,
                // avoiding the async dispatch that Progress<T> does via SyncContext.
                new SyncProgress<DownloadProgress>(reports.Add));

            Assert.True(File.Exists(dest));
            Assert.Equal(size, new FileInfo(dest).Length);

            // Must have at least the final completion report.
            Assert.NotEmpty(reports);

            var last = reports[^1];
            Assert.Equal(size, last.BytesReceived);
            Assert.Equal(size, last.TotalBytes);
            Assert.True(last.Percentage is >= 99.99 and <= 100.01,
                $"Expected ~100%, got {last.Percentage:F2}%");
        }
        finally { TryDelete(dest); }
    }

    [Fact]
    public async Task DownloadAsync_UnknownSize_StillWritesFile()
    {
        // Some servers (chunked transfer) omit Content-Length.
        // TotalBytes should be −1 and Percentage should be NaN — no crash.
        // ByteArrayContent auto-computes ContentLength, so we use NoLengthContent instead.
        const int size = 50_000;
        var fakeBytes = new byte[size];

        using var client = MakeClient((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new NoLengthContent(fakeBytes)
        });

        var dest = TempFilePath();
        DownloadProgress? finalReport = null;

        try
        {
            await new Downloader(client).DownloadAsync(
                "https://fake/file.bin", dest,
                new SyncProgress<DownloadProgress>(p => finalReport = p));

            Assert.Equal(size, new FileInfo(dest).Length);
            Assert.NotNull(finalReport);
            Assert.Equal(-1L, finalReport!.Value.TotalBytes);
            Assert.True(double.IsNaN(finalReport.Value.Percentage));
        }
        finally { TryDelete(dest); }
    }

    // ── Multi-segment ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public async Task DownloadAsync_MultiSegment_WritesCorrectBytesAtEveryOffset(int connections)
    {
        // 4 MB — well above the 1 MB threshold that triggers multi-segment.
        const int size = 4 * 1024 * 1024;
        var expected = new byte[size];
        Random.Shared.NextBytes(expected);

        using var client = MakeClient((request, _) =>
        {
            // HEAD → advertise range support + total size.
            if (request.Method == HttpMethod.Head)
            {
                var h = new HttpResponseMessage(HttpStatusCode.OK);
                h.Headers.AcceptRanges.Add("bytes");
                h.Content = new ByteArrayContent(Array.Empty<byte>());
                h.Content.Headers.ContentLength = size;
                return h;
            }

            // GET with Range header → serve exactly the requested slice.
            // This is what a real CDN does: reads Range: bytes=<from>-<to>
            // and returns HTTP 206 with exactly (to - from + 1) bytes.
            var range = request.Headers.Range!.Ranges.Single();
            int from  = (int)range.From!.Value;
            int to    = (int)range.To!.Value;

            return new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                // expected[from..(to+1)] gives bytes [from, to] inclusive.
                Content = new ByteArrayContent(expected[from..(to + 1)])
            };
        });

        var settings = new EngineSettings { MaxConnectionsPerFile = connections };
        var dest = TempFilePath();

        try
        {
            var reports = new List<DownloadProgress>();
            await new Downloader(client, settings).DownloadAsync(
                "https://fake/big.bin", dest,
                new SyncProgress<DownloadProgress>(reports.Add));

            Assert.Equal(size, new FileInfo(dest).Length);
            // The critical assertion: every byte lands at the right offset.
            Assert.Equal(expected, await File.ReadAllBytesAsync(dest));

            // Final report must be 100%.
            Assert.NotEmpty(reports);
            var last = reports[^1];
            Assert.Equal(size, last.BytesReceived);
        }
        finally { TryDelete(dest); }
    }

    [Fact]
    public async Task DownloadAsync_MultiSegment_FallsBackToSingleStream_WhenServerDeclinesRanges()
    {
        const int size = 4 * 1024 * 1024;
        var expected = new byte[size];
        Random.Shared.NextBytes(expected);

        // Server returns HEAD 200 but WITHOUT Accept-Ranges: bytes.
        using var client = MakeClient((request, _) =>
        {
            if (request.Method == HttpMethod.Head)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Array.Empty<byte>()) { Headers = { ContentLength = size } }
                };

            // Single-stream GET returns the whole file.
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(expected) { Headers = { ContentLength = size } }
            };
        });

        var settings = new EngineSettings { MaxConnectionsPerFile = 8 };
        var dest = TempFilePath();

        try
        {
            await new Downloader(client, settings).DownloadAsync("https://fake/big.bin", dest);
            Assert.Equal(expected, await File.ReadAllBytesAsync(dest));
        }
        finally { TryDelete(dest); }
    }

    [Fact]
    public async Task DownloadAsync_MultiSegment_RetriesFailedSegmentAndCompletesSuccessfully()
    {
        // 2 MB, 2 segments → each segment is 1 MB.
        const int size = 2 * 1024 * 1024;
        var expected = new byte[size];
        Random.Shared.NextBytes(expected);

        // The very first GET request (whichever segment fires it) returns 503.
        // All subsequent GETs succeed — so one segment retries once and recovers.
        int getCount = 0;

        using var client = MakeClient((request, _) =>
        {
            if (request.Method == HttpMethod.Head)
            {
                var h = new HttpResponseMessage(HttpStatusCode.OK);
                h.Headers.AcceptRanges.Add("bytes");
                h.Content = new ByteArrayContent(Array.Empty<byte>());
                h.Content.Headers.ContentLength = size;
                return h;
            }

            if (Interlocked.Increment(ref getCount) == 1)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

            var range = request.Headers.Range!.Ranges.Single();
            int from  = (int)range.From!.Value;
            int to    = (int)range.To!.Value;
            return new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(expected[from..(to + 1)])
            };
        });

        var settings = new EngineSettings
        {
            MaxConnectionsPerFile = 2,
            RetryCount            = 2,
            RetryDelay            = TimeSpan.FromMilliseconds(10) // keep test fast
        };
        var dest = TempFilePath();

        try
        {
            await new Downloader(client, settings).DownloadAsync("https://fake/big.bin", dest);
            Assert.Equal(size, new FileInfo(dest).Length);
            Assert.Equal(expected, await File.ReadAllBytesAsync(dest));
            // At least 3 GETs: 1 failed + 2 retried-or-original succeeds.
            Assert.True(getCount >= 3, $"Expected ≥3 GET calls, got {getCount}");
        }
        finally { TryDelete(dest); }
    }

    [Fact]
    public async Task DownloadAsync_MultiSegment_ThrowsWhenAllRetriesExhausted()
    {
        const int size = 2 * 1024 * 1024;

        using var client = MakeClient((request, _) =>
        {
            if (request.Method == HttpMethod.Head)
            {
                var h = new HttpResponseMessage(HttpStatusCode.OK);
                h.Headers.AcceptRanges.Add("bytes");
                h.Content = new ByteArrayContent(Array.Empty<byte>());
                h.Content.Headers.ContentLength = size;
                return h;
            }
            // All GETs fail permanently.
            return new HttpResponseMessage(HttpStatusCode.BadGateway);
        });

        var settings = new EngineSettings
        {
            MaxConnectionsPerFile = 2,
            RetryCount            = 1,
            RetryDelay            = TimeSpan.FromMilliseconds(10)
        };
        var dest = TempFilePath();

        try
        {
            var ex = await Assert.ThrowsAsync<DownloadException>(
                () => new Downloader(client, settings).DownloadAsync("https://fake/big.bin", dest));

            Assert.Equal(DownloadFailureReason.NetworkError, ex.Reason);
        }
        finally { TryDelete(dest); }
    }

    // ── Pause / resume ─────────────────────────────────────────────────────

    [Fact]
    public async Task DownloadAsync_MultiSegment_DeletesStateFile_OnSuccess()
    {
        const int size = 2 * 1024 * 1024;
        var data = new byte[size];

        using var client = MakeRangeCapableClient(data);
        var settings  = new EngineSettings { MaxConnectionsPerFile = 2 };
        var dest      = TempFilePath();
        var statePath = Downloader.GetStatePath(dest);

        try
        {
            await new Downloader(client, settings).DownloadAsync("https://fake/big.bin", dest);
            Assert.False(File.Exists(statePath),
                "State file should be deleted after successful completion");
        }
        finally { TryDelete(dest); TryDelete(statePath); }
    }

    [Fact]
    public async Task Pause_PersistsStateFile_WithCorrectMetadata()
    {
        // The BlockingStream never yields data, so cancellation fires before any
        // bytes are transferred — but the state file is written at session start.
        const int size = 4 * 1024 * 1024;

        using var client = MakeClient((request, _) =>
        {
            if (request.Method == HttpMethod.Head)
                return RangeHeadResponse(size);

            return new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new StreamContent(new BlockingStream())
            };
        });

        var settings  = new EngineSettings { MaxConnectionsPerFile = 2, RetryCount = 0 };
        var dest      = TempFilePath();
        var statePath = Downloader.GetStatePath(dest);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        try
        {
            var ex = await Assert.ThrowsAsync<DownloadException>(
                () => new Downloader(client, settings).DownloadAsync(
                    "https://fake/big.bin", dest, ct: cts.Token));

            Assert.Equal(DownloadFailureReason.Cancelled, ex.Reason);
            Assert.True(File.Exists(statePath), "State file must survive a pause");

            var stateJson = await File.ReadAllTextAsync(statePath);
            var state = JsonSerializer.Deserialize<DownloadState>(stateJson, CamelCaseJson)!;

            Assert.Equal("https://fake/big.bin", state.Url);
            Assert.Equal(size, state.TotalBytes);
            Assert.Equal(2, state.Segments.Length);
        }
        finally { TryDelete(dest); TryDelete(statePath); }
    }

    [Fact]
    public async Task Resume_SkipsAlreadyDownloadedBytes_ViaRangeHeaders()
    {
        // 4 MB file, 2 segments (2 MB each).
        // Simulate a previous session that completed the first half of each segment.
        const int size     = 4 * 1024 * 1024;
        const int half     = size / 2;    // segment boundary
        const int resume0  = half / 2;    // seg 0: first 1 MB already written
        const int resume1  = half / 4;    // seg 1: first 512 KB already written

        var expected = new byte[size];
        Random.Shared.NextBytes(expected);

        var capturedRanges = new List<(long From, long To)>();

        using var client = MakeClient((request, _) =>
        {
            if (request.Method == HttpMethod.Head)
                return RangeHeadResponse(size);

            var r    = request.Headers.Range!.Ranges.Single();
            long from = r.From!.Value;
            long to   = r.To!.Value;
            lock (capturedRanges) capturedRanges.Add((from, to));

            return new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(expected[(int)from..(int)(to + 1)])
            };
        });

        var settings  = new EngineSettings { MaxConnectionsPerFile = 2, RetryCount = 0 };
        var dest      = TempFilePath();
        var statePath = Downloader.GetStatePath(dest);

        try
        {
            // ── Build partial state from previous session ───────────────────
            // Write the bytes that were "already" downloaded.
            await using (var f = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                f.SetLength(size);
                f.Seek(0,    SeekOrigin.Begin); await f.WriteAsync(expected.AsMemory(0,    resume0));
                f.Seek(half, SeekOrigin.Begin); await f.WriteAsync(expected.AsMemory(half, resume1));
            }

            // Write the matching .dmstate file.
            var savedState = new DownloadState
            {
                Url             = "https://fake/big.bin",
                DestinationPath = dest,
                TotalBytes      = size,
                Segments        =
                [
                    new SegmentState { Index = 0, StartByte = 0,    EndByte = half - 1, BytesCompleted = resume0 },
                    new SegmentState { Index = 1, StartByte = half, EndByte = size - 1, BytesCompleted = resume1 }
                ]
            };
            File.WriteAllText(statePath, JsonSerializer.Serialize(savedState, CamelCaseJson));

            // ── Resume ──────────────────────────────────────────────────────
            await new Downloader(client, settings).DownloadAsync("https://fake/big.bin", dest);

            // ── Verify Range headers start from resume points ────────────────
            // Segment 0 must have requested starting at resume0, not 0.
            Assert.Contains(capturedRanges, r => r.From == resume0);
            // Segment 1 must have requested starting at half + resume1, not half.
            Assert.Contains(capturedRanges, r => r.From == half + resume1);

            // Neither segment should have re-requested from byte 0 or the segment start.
            Assert.DoesNotContain(capturedRanges, r => r.From == 0);
            Assert.DoesNotContain(capturedRanges, r => r.From == half);

            // Final file is byte-perfect.
            Assert.Equal(expected, await File.ReadAllBytesAsync(dest));

            // State file is cleaned up.
            Assert.False(File.Exists(statePath));
        }
        finally { TryDelete(dest); TryDelete(statePath); }
    }

    // ── HTTP/2, connection pooling, RAM buffer ─────────────────────────────

    [Fact]
    public void CreateOptimizedHttpClient_SetsHttp2DefaultsAndPoolSize()
    {
        var settings = new EngineSettings { MaxConnectionsPerFile = 4 };
        using var client = Downloader.CreateOptimizedHttpClient(settings);

        Assert.Equal(HttpVersion.Version20,                client.DefaultRequestVersion);
        Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, client.DefaultVersionPolicy);
    }

    [Fact]
    public async Task Http2_SegmentRequestsAdvertiseVersionPreference()
    {
        const int size = 2 * 1024 * 1024;
        var data = new byte[size];
        var capturedVersions = new System.Collections.Concurrent.ConcurrentBag<Version>();

        using var client = MakeClient((request, _) =>
        {
            capturedVersions.Add(request.Version);
            if (request.Method == HttpMethod.Head) return RangeHeadResponse(size);
            var r    = request.Headers.Range!.Ranges.Single();
            int from = (int)r.From!.Value, to = (int)r.To!.Value;
            return new HttpResponseMessage(HttpStatusCode.PartialContent)
            { Content = new ByteArrayContent(data[from..(to + 1)]) };
        });

        var settings = new EngineSettings { MaxConnectionsPerFile = 2, AutoTuneConnections = false };
        var dest = TempFilePath();

        try
        {
            await new Downloader(client, settings).DownloadAsync("https://fake/big.bin", dest);
            // HEAD probe + two segment GETs must all request HTTP/2.
            Assert.All(capturedVersions, v => Assert.Equal(HttpVersion.Version20, v));
        }
        finally { TryDelete(dest); }
    }

    [Theory]
    [InlineData(1)]   // minimal channel: back-pressure on every chunk
    [InlineData(16)]  // large channel: absorbs bursts
    public async Task RamBuffer_VariousChannelSizes_WritesCorrectOutput(int ramBufferChunks)
    {
        const int size = 2 * 1024 * 1024;
        var expected = new byte[size];
        Random.Shared.NextBytes(expected);

        using var client = MakeRangeCapableClient(expected);
        var settings = new EngineSettings
        {
            MaxConnectionsPerFile = 2,
            AutoTuneConnections   = false,
            RamBufferChunks       = ramBufferChunks,
        };
        var dest = TempFilePath();

        try
        {
            await new Downloader(client, settings).DownloadAsync("https://fake/big.bin", dest);
            Assert.Equal(expected, await File.ReadAllBytesAsync(dest));
        }
        finally { TryDelete(dest); }
    }

    [Fact]
    public async Task RamBuffer_CancellationDuringPipe_NoPooledBufferLeak()
    {
        // Verifies that cancelling while the channel has in-flight buffers
        // does not deadlock and does not throw an unhandled exception
        // (ArrayPool return is done in the finally cleanup path).
        const int size = 4 * 1024 * 1024;

        using var client = MakeClient((request, _) =>
        {
            if (request.Method == HttpMethod.Head) return RangeHeadResponse(size);
            return new HttpResponseMessage(HttpStatusCode.PartialContent)
            { Content = new StreamContent(new BlockingStream()) };
        });

        var settings = new EngineSettings
        {
            MaxConnectionsPerFile = 2,
            RetryCount            = 0,
            RamBufferChunks       = 4,
        };
        var dest = TempFilePath();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        try
        {
            var ex = await Assert.ThrowsAsync<DownloadException>(
                () => new Downloader(client, settings).DownloadAsync(
                    "https://fake/big.bin", dest, ct: cts.Token));
            Assert.Equal(DownloadFailureReason.Cancelled, ex.Reason);
        }
        finally { TryDelete(dest); TryDelete(Downloader.GetStatePath(dest)); }
    }

    // ── Bug-fix regressions ────────────────────────────────────────────────

    [Fact]
    public async Task Resume_DestinationFileDeleted_RedownloadsEntireFile_NotCorrupt()
    {
        // State file records partial progress but the destination was deleted.
        // Before the fix, segments with nonzero BytesCompleted would issue Range
        // requests starting mid-segment; the "already done" prefix stayed as zeros
        // → silent file corruption.  After the fix, BytesCompleted is reset to 0
        // when the file is absent, so every segment downloads from its start byte.
        const int size = 4 * 1024 * 1024;
        const int half = size / 2;
        var expected = new byte[size];
        Random.Shared.NextBytes(expected);

        using var client = MakeRangeCapableClient(expected);
        var settings  = new EngineSettings { MaxConnectionsPerFile = 2, RetryCount = 0 };
        var dest      = TempFilePath();
        var statePath = Downloader.GetStatePath(dest);

        try
        {
            // Write a state file that claims partial progress but leave NO destination file.
            var savedState = new DownloadState
            {
                Url             = "https://fake/big.bin",
                DestinationPath = dest,
                TotalBytes      = size,
                Segments        =
                [
                    new SegmentState { Index = 0, StartByte = 0,    EndByte = half - 1, BytesCompleted = half / 2 },
                    new SegmentState { Index = 1, StartByte = half, EndByte = size - 1, BytesCompleted = half / 4 },
                ]
            };
            File.WriteAllText(statePath, JsonSerializer.Serialize(savedState, CamelCaseJson));

            Assert.False(File.Exists(dest));

            await new Downloader(client, settings).DownloadAsync("https://fake/big.bin", dest);

            Assert.Equal(expected, await File.ReadAllBytesAsync(dest));
            Assert.False(File.Exists(statePath));
        }
        finally { TryDelete(dest); TryDelete(statePath); }
    }

    [Fact]
    public async Task IoError_DuringSegmentWrite_IsNotRetried()
    {
        // Verifies that DownloadException(IoError) is excluded from the retry loop.
        // The destination file is pre-allocated and held open with FileShare.None,
        // so the segment's FileStream.Open throws IOException (sharing violation)
        // at exactly the right code path inside DownloadSegmentAsync — not during
        // the pre-allocation step that runs before any workers start.
        const int size = 2 * 1024 * 1024;
        var data = new byte[size];

        int getCount = 0;
        using var client = MakeClient((request, _) =>
        {
            if (request.Method == HttpMethod.Head) return RangeHeadResponse(size);
            Interlocked.Increment(ref getCount);
            var r    = request.Headers.Range!.Ranges.Single();
            int from = (int)r.From!.Value, to = (int)r.To!.Value;
            return new HttpResponseMessage(HttpStatusCode.PartialContent)
            { Content = new ByteArrayContent(data[from..(to + 1)]) };
        });

        var settings = new EngineSettings
        {
            MaxConnectionsPerFile = 2,
            AutoTuneConnections   = false,
            RetryCount            = 5,   // without fix: up to 6 × 2 = 12 GETs
            RetryDelay            = TimeSpan.FromMilliseconds(1),
        };

        var dest      = TempFilePath();
        var statePath = Downloader.GetStatePath(dest);

        try
        {
            // Pre-allocate the destination so the engine skips its own pre-allocation
            // (File.Exists == true), then keep it locked exclusively. The engine's
            // segment writers open with FileShare.ReadWrite — a sharing violation fires.
            // exclusiveLock is disposed (and the file released) before the finally runs.
            await using var exclusiveLock = new FileStream(
                dest, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            exclusiveLock.SetLength(size);

            var ex = await Assert.ThrowsAsync<DownloadException>(
                () => new Downloader(client, settings).DownloadAsync("https://fake/big.bin", dest));

            Assert.Equal(DownloadFailureReason.IoError, ex.Reason);
            // With fix: 1 GET per segment, no retries → getCount ≤ 2.
            // Without fix: up to 6 attempts × 2 segments = 12 GETs.
            Assert.True(getCount <= 2, $"IoError must not be retried; got {getCount} GET(s)");
        }
        finally
        {
            TryDelete(dest);
            TryDelete(statePath);
        }
    }

    // ── Work-stealing, auto-tune, adaptive retry ──────────────────────────

    [Fact]
    public async Task WorkStealing_OverPartitionedChunks_AllChunksDownloadedCorrectly()
    {
        // 4 MB file with only 2 connections, but MinChunkBytes = 256 KB.
        // CalculateChunks: targetChunkSize = max(256KB, 4MB/(2×4)) = max(256KB, 512KB) = 512KB
        //                  count = max(2, ceil(4MB/512KB)) = 8
        // Each worker dequeues 4 chunks on average — work-stealing via queue.
        const int size = 4 * 1024 * 1024;
        var expected = new byte[size];
        Random.Shared.NextBytes(expected);

        int rangeRequests = 0;
        using var client = MakeClient((request, _) =>
        {
            if (request.Method == HttpMethod.Head) return RangeHeadResponse(size);
            Interlocked.Increment(ref rangeRequests);
            var r    = request.Headers.Range!.Ranges.Single();
            int from = (int)r.From!.Value, to = (int)r.To!.Value;
            return new HttpResponseMessage(HttpStatusCode.PartialContent)
            { Content = new ByteArrayContent(expected[from..(to + 1)]) };
        });

        var settings = new EngineSettings
        {
            MaxConnectionsPerFile = 2,
            AutoTuneConnections   = false,
            MinChunkBytes         = 256 * 1024,
        };
        var dest = TempFilePath();

        try
        {
            await new Downloader(client, settings).DownloadAsync("https://fake/big.bin", dest);
            Assert.Equal(expected, await File.ReadAllBytesAsync(dest));
            // 8 chunks → 8 Range requests; more than the 2-connection count proves
            // each worker downloaded multiple chunks from the shared queue.
            Assert.Equal(8, rangeRequests);
        }
        finally { TryDelete(dest); }
    }

    [Fact]
    public async Task AutoTune_CompletesCorrectly_WhenStartingWithFewerConnections()
    {
        // Start with 1 connection, allow tuner to ramp up to 4.
        // Fast tuneInterval ensures the gate releases before the download finishes.
        const int size = 4 * 1024 * 1024;
        var expected = new byte[size];
        Random.Shared.NextBytes(expected);

        using var client = MakeRangeCapableClient(expected);
        var settings = new EngineSettings
        {
            MaxConnectionsPerFile     = 4,
            InitialConnectionsPerFile = 1,
            AutoTuneConnections       = true,
            AutoTuneInterval          = TimeSpan.FromMilliseconds(20),
            MinChunkBytes             = 512 * 1024,
        };
        var dest = TempFilePath();

        try
        {
            await new Downloader(client, settings).DownloadAsync("https://fake/big.bin", dest);
            Assert.Equal(expected, await File.ReadAllBytesAsync(dest));
        }
        finally { TryDelete(dest); }
    }

    [Fact]
    public async Task AdaptiveRetry_RespectsRetryDelayMax_AndCompletesWithJitter()
    {
        const int size = 2 * 1024 * 1024;
        var expected = new byte[size];
        Random.Shared.NextBytes(expected);

        int getCount = 0;
        using var client = MakeClient((request, _) =>
        {
            if (request.Method == HttpMethod.Head) return RangeHeadResponse(size);
            // First 2 GETs fail; subsequent succeed.
            if (Interlocked.Increment(ref getCount) <= 2)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            var r    = request.Headers.Range!.Ranges.Single();
            int from = (int)r.From!.Value, to = (int)r.To!.Value;
            return new HttpResponseMessage(HttpStatusCode.PartialContent)
            { Content = new ByteArrayContent(expected[from..(to + 1)]) };
        });

        var settings = new EngineSettings
        {
            MaxConnectionsPerFile = 2,
            RetryCount            = 3,
            RetryDelay            = TimeSpan.FromMilliseconds(5),
            RetryDelayMax         = TimeSpan.FromMilliseconds(20), // cap well below exponential ceiling
        };
        var dest = TempFilePath();

        try
        {
            await new Downloader(client, settings).DownloadAsync("https://fake/big.bin", dest);
            Assert.Equal(expected, await File.ReadAllBytesAsync(dest));
        }
        finally { TryDelete(dest); }
    }

    // ── Error cases ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(404)]
    [InlineData(403)]
    [InlineData(503)]
    public async Task DownloadAsync_ThrowsDownloadException_OnNon2xxResponse(int code)
    {
        using var client = MakeClient((_, _) =>
            new HttpResponseMessage((HttpStatusCode)code));

        var ex = await Assert.ThrowsAsync<DownloadException>(
            () => new Downloader(client).DownloadAsync("https://fake/file.bin", "irrelevant.bin"));

        Assert.Equal(DownloadFailureReason.ServerError, ex.Reason);
        Assert.Equal(code, ex.HttpStatusCode);
    }

    [Fact]
    public async Task DownloadAsync_ThrowsDownloadException_OnCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync(); // already cancelled before the request starts

        using var client = MakeClient((_, ct) =>
        {
            // Simulate the handler respecting the token (real HttpClient does this too).
            ct.ThrowIfCancellationRequested();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var ex = await Assert.ThrowsAsync<DownloadException>(
            () => new Downloader(client).DownloadAsync(
                "https://fake/file.bin", "irrelevant.bin", ct: cts.Token));

        Assert.Equal(DownloadFailureReason.Cancelled, ex.Reason);
    }

    [Fact]
    public async Task DownloadAsync_ThrowsDownloadException_OnNetworkError()
    {
        using var client = MakeClient((_, _) =>
            throw new HttpRequestException("Simulated network failure"));

        var ex = await Assert.ThrowsAsync<DownloadException>(
            () => new Downloader(client).DownloadAsync("https://fake/file.bin", "irrelevant.bin"));

        Assert.Equal(DownloadFailureReason.NetworkError, ex.Reason);
    }

    // ── Shared helpers ─────────────────────────────────────────────────────

    private static HttpClient MakeClient(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
        => new(new FakeHandler(handler));

    /// <summary>
    /// Builds a client that handles HEAD + range GETs for a byte array, returning
    /// correct 206 slices.  Used by tests that just need a working range server.
    /// </summary>
    private static HttpClient MakeRangeCapableClient(byte[] data)
        => MakeClient((request, _) =>
        {
            if (request.Method == HttpMethod.Head)
                return RangeHeadResponse(data.Length);

            var r    = request.Headers.Range!.Ranges.Single();
            int from = (int)r.From!.Value;
            int to   = (int)r.To!.Value;
            return new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(data[from..(to + 1)])
            };
        });

    private static HttpResponseMessage RangeHeadResponse(long contentLength)
    {
        var h = new HttpResponseMessage(HttpStatusCode.OK);
        h.Headers.AcceptRanges.Add("bytes");
        h.Content = new ByteArrayContent(Array.Empty<byte>());
        h.Content.Headers.ContentLength = contentLength;
        return h;
    }

    private static string TempFilePath() =>
        Path.Combine(Path.GetTempPath(), $"dm_test_{Guid.NewGuid():N}.bin");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best-effort cleanup */ }
    }

    /// <summary>
    /// Lightweight HttpMessageHandler backed by a lambda.
    /// Lets us return any canned response without spinning up a real server.
    /// </summary>
    private sealed class FakeHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> fn)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
                return Task.FromCanceled<HttpResponseMessage>(ct);

            return Task.FromResult(fn(request, ct));
        }
    }

    /// <summary>
    /// HttpContent that deliberately omits Content-Length, simulating a
    /// chunked-encoded or streaming server response.
    /// ByteArrayContent always sets Content-Length, so we need this wrapper.
    /// </summary>
    private sealed class NoLengthContent(byte[] data) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(data).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false; // returning false suppresses the Content-Length header
        }
    }

    /// <summary>
    /// A stream whose ReadAsync never returns — it blocks until the CancellationToken
    /// is cancelled.  Simulates a stalled connection so we can test pause behaviour
    /// without a real slow server.
    /// </summary>
    private sealed class BlockingStream : Stream
    {
        public override bool CanRead  => true;
        public override bool CanSeek  => false;
        public override bool CanWrite => false;
        public override long Length   => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void  Flush() { }
        public override int   Read(byte[] buffer, int offset, int count) => 0;
        public override long  Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void  SetLength(long value)                 => throw new NotSupportedException();
        public override void  Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
    }

    /// <summary>
    /// Synchronous IProgress&lt;T&gt; implementation.
    /// Unlike the standard Progress&lt;T&gt;, which posts the callback to the
    /// captured SynchronizationContext (potentially after the test method returns),
    /// this one calls the action directly on the reporting thread — safe and
    /// deterministic in unit tests.
    /// </summary>
    private sealed class SyncProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}
