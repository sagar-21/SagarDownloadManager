using System.Net;
using DM.Core.Downloading;

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
