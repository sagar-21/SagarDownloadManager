using System.Diagnostics;

namespace DM.Core.Downloading;

/// <summary>
/// Downloads a single URL to a file, streaming the response body so that
/// arbitrarily large files never have to fit in memory.
///
/// Design notes:
///   - HttpClient is injected, not created here.  HttpClient is meant to be
///     long-lived and shared; the caller (DownloadEngine) owns and configures it
///     (timeout, headers, proxy, etc.) via EngineSettings.
///   - ResponseHeadersRead tells HttpClient to return as soon as headers arrive
///     so we can start streaming instead of buffering the whole body first.
///   - Progress is reported on the same thread that calls Report(), so the UI
///     must use Dispatcher.Invoke / IProgress<T> correctly (covered when we wire
///     it to the ViewModel).
/// </summary>
public sealed class Downloader
{
    // 80 KB per read — large enough to saturate a gigabit link, small enough
    // to stay well inside LOH allocation territory (< 85 KB threshold).
    private const int BufferSize = 81_920;
    private const double ProgressIntervalSeconds = 0.25; // ~4 reports/sec

    private readonly HttpClient _http;

    public Downloader(HttpClient http) => _http = http;

    public async Task DownloadAsync(
        string url,
        string destinationPath,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // HttpClient's internal timeout token fired — the caller did NOT cancel.
            throw new DownloadException(DownloadFailureReason.Timeout,
                $"Request to '{url}' timed out.", ex);
        }
        catch (OperationCanceledException ex)
        {
            throw new DownloadException(DownloadFailureReason.Cancelled,
                "Download was cancelled.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new DownloadException(DownloadFailureReason.NetworkError,
                $"Network error reaching '{url}': {ex.Message}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new DownloadException(
                    DownloadFailureReason.ServerError,
                    $"Server returned {(int)response.StatusCode} {response.ReasonPhrase} for '{url}'.",
                    statusCode: (int)response.StatusCode);

            long totalBytes = response.Content.Headers.ContentLength ?? -1L;

            try
            {
                await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                await using var fileStream = new FileStream(
                    destinationPath, FileMode.Create, FileAccess.Write,
                    FileShare.None, BufferSize, useAsync: true);

                await StreamToFileAsync(contentStream, fileStream, totalBytes, progress, ct);
            }
            catch (OperationCanceledException ex)
            {
                throw new DownloadException(DownloadFailureReason.Cancelled,
                    "Download was cancelled.", ex);
            }
            catch (IOException ex)
            {
                throw new DownloadException(DownloadFailureReason.IoError,
                    $"Failed writing to '{destinationPath}': {ex.Message}", ex);
            }
        }
    }

    private static async Task StreamToFileAsync(
        Stream source,
        Stream destination,
        long totalBytes,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        long bytesReceived = 0;
        var sw = Stopwatch.StartNew();
        var lastReportAt = TimeSpan.Zero;
        var bytesAtLastReport = 0L;

        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            bytesReceived += read;

            if (progress is not null)
            {
                var now = sw.Elapsed;
                var deltaSeconds = (now - lastReportAt).TotalSeconds;
                if (deltaSeconds >= ProgressIntervalSeconds)
                {
                    var speed = (bytesReceived - bytesAtLastReport) / deltaSeconds;
                    progress.Report(new DownloadProgress(bytesReceived, totalBytes, speed));
                    lastReportAt = now;
                    bytesAtLastReport = bytesReceived;
                }
            }
        }

        // Final report: speed = 0 signals the transfer is complete.
        progress?.Report(new DownloadProgress(bytesReceived, totalBytes, 0));
    }
}
