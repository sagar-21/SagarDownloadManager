using System.IO;
using System.Windows;
using DM.App.ViewModels;
using DM.Core.Downloading;
using DM.Core.Models;
using DM.Core.Settings;

namespace DM.App.Services;

/// <summary>
/// Bridges <see cref="DownloadItemViewModel"/> to <see cref="Downloader"/>.
/// All Progress&lt;T&gt; instances are created on the calling (UI) thread, so their
/// callbacks are automatically marshalled back to the UI thread by SynchronizationContext.
/// </summary>
public sealed class DownloadService
{
    private static readonly HttpClient _http = Downloader.CreateOptimizedHttpClient();
    private static readonly EngineSettings _settings = new();

    // ── Start / Resume ─────────────────────────────────────────────────────

    public async Task StartAsync(DownloadItemViewModel item)
    {
        item.Status = DownloadStatus.Downloading;
        item.SpeedBytesPerSecond = 0;

        var cts = new CancellationTokenSource();
        item.Cts = cts;

        // Capture UI SynchronizationContext so callbacks run on the UI thread.
        var progress = new Progress<DownloadProgress>(p =>
        {
            item.DownloadedBytes = p.BytesReceived;
            if (p.TotalBytes > 0) item.TotalBytes = p.TotalBytes;
            item.SpeedBytesPerSecond = p.SpeedBytesPerSecond;
        });

        try
        {
            var dir = Path.GetDirectoryName(item.DestinationPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            await Task.Run(() =>
                new Downloader(_http, _settings)
                    .DownloadAsync(item.SourceUrl, item.DestinationPath, progress, cts.Token),
                cts.Token);

            // Runs on UI thread (Progress<T> captured the WPF dispatcher context).
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                item.Status = DownloadStatus.Completed;
                item.SpeedBytesPerSecond = 0;
                if (item.TotalBytes > 0) item.DownloadedBytes = item.TotalBytes;
                try { Downloader.DeleteState(item.DestinationPath); } catch { }
            });
        }
        catch (OperationCanceledException)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                // State file is preserved for resume; only set Cancelled if not Paused.
                if (item.Status != DownloadStatus.Paused)
                    item.Status = DownloadStatus.Cancelled;
                item.SpeedBytesPerSecond = 0;
            });
        }
        catch (Exception ex)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                item.Status = DownloadStatus.Failed;
                item.ErrorMessage = ex.Message;
                item.SpeedBytesPerSecond = 0;
            });
        }
        finally
        {
            item.Cts = null;
        }
    }

    public Task ResumeAsync(DownloadItemViewModel item) => StartAsync(item);

    // ── Pause ───────────────────────────────────────────────────────────────

    public void Pause(DownloadItemViewModel item)
    {
        // Set Paused BEFORE cancelling so the catch-block in StartAsync skips Cancelled.
        item.Status = DownloadStatus.Paused;
        item.SpeedBytesPerSecond = 0;
        item.Cts?.Cancel();
        // .dmstate file is preserved by Downloader on cancellation → resume works.
    }

    // ── Cancel ──────────────────────────────────────────────────────────────

    public void Cancel(DownloadItemViewModel item)
    {
        item.Status = DownloadStatus.Cancelled;
        item.SpeedBytesPerSecond = 0;
        item.Cts?.Cancel();
        try { Downloader.DeleteState(item.DestinationPath); } catch { }
    }
}
