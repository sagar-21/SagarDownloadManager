using System.Net;
using DM.Core.Downloading;
using DM.Core.Settings;

namespace DM.Tests.Downloading;

public sealed class DownloaderTests
{
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
