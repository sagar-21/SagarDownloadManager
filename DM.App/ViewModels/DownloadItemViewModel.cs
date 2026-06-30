using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DM.App.Services;
using DM.Core.Models;
using DM.Core.Queue;

namespace DM.App.ViewModels;

public partial class DownloadItemViewModel : ObservableObject
{
    private readonly DownloadService?      _service;
    private readonly DownloadQueueManager? _queue;

    /// <summary>Active cancellation token; set by DownloadService when not queue-managed.</summary>
    public CancellationTokenSource? Cts { get; set; }

    /// <summary>The corresponding queue entry when managed by DownloadQueueManager.</summary>
    internal QueueEntry? Entry { get; set; }

    public DownloadItemViewModel(DownloadService? service = null) { _service = service; }

    internal DownloadItemViewModel(DownloadQueueManager queue, QueueEntry entry)
    {
        _queue = queue;
        Entry  = entry;
    }

    [ObservableProperty] private string           _fileName            = "";
    [ObservableProperty] private string           _sourceUrl           = "";
    [ObservableProperty] private string           _destinationPath     = "";
    [ObservableProperty] private long             _totalBytes;
    [ObservableProperty] private long             _downloadedBytes;
    [ObservableProperty] private double           _speedBytesPerSecond;
    [ObservableProperty] private DownloadStatus   _status              = DownloadStatus.Queued;
    [ObservableProperty] private string           _errorMessage        = "";
    [ObservableProperty] private DownloadPriority _priority            = DownloadPriority.Normal;

    partial void OnDownloadedBytesChanged(long value)      => RefreshDerived();
    partial void OnTotalBytesChanged(long value)           => RefreshDerived();
    partial void OnSpeedBytesPerSecondChanged(double value) => RefreshDerived();
    partial void OnStatusChanged(DownloadStatus value)     => RefreshDerived();

    private void RefreshDerived()
    {
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(IsIndeterminate));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(EtaText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(CanPauseResume));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanOpen));
        OnPropertyChanged(nameof(CanRetry));
    }

    // ── Computed properties ────────────────────────────────────────────────

    public double Progress =>
        TotalBytes > 0 ? Math.Clamp(DownloadedBytes / (double)TotalBytes, 0, 1) : 0;

    public bool IsIndeterminate => IsDownloading && TotalBytes <= 0;

    public string SizeText
    {
        get
        {
            if (Status == DownloadStatus.Completed)
                return TotalBytes > 0 ? FormatBytes(TotalBytes) : FormatBytes(DownloadedBytes);
            if (TotalBytes > 0)
                return $"{FormatBytes(DownloadedBytes)} / {FormatBytes(TotalBytes)}";
            if (DownloadedBytes > 0)
                return $"{FormatBytes(DownloadedBytes)} downloaded";
            return "";
        }
    }

    public string SpeedText =>
        Status == DownloadStatus.Downloading
            ? (SpeedBytesPerSecond > 0 ? $"{FormatBytes((long)SpeedBytesPerSecond)}/s" : "Connecting…")
            : "";

    public string EtaText
    {
        get
        {
            if (Status != DownloadStatus.Downloading || SpeedBytesPerSecond <= 0 || TotalBytes <= 0)
                return "";
            long remaining = TotalBytes - DownloadedBytes;
            long secs = (long)(remaining / SpeedBytesPerSecond);
            if (secs < 60)   return $"{secs}s";
            if (secs < 3600) return $"{secs / 60}m {secs % 60}s";
            return $"{secs / 3600}h {secs % 3600 / 60}m";
        }
    }

    public string StatusText => Status switch
    {
        DownloadStatus.Queued      => "Queued",
        DownloadStatus.Downloading => "Downloading",
        DownloadStatus.Paused      => "Paused",
        DownloadStatus.Completed   => "Completed",
        DownloadStatus.Failed      => "Failed",
        DownloadStatus.Cancelled   => "Cancelled",
        _                          => Status.ToString(),
    };

    public bool IsDownloading  => Status == DownloadStatus.Downloading;
    public bool IsCompleted    => Status == DownloadStatus.Completed;
    public bool IsFailed       => Status == DownloadStatus.Failed;
    public bool IsPaused       => Status == DownloadStatus.Paused;
    public bool IsActive       => Status is DownloadStatus.Downloading or DownloadStatus.Paused;
    public bool CanPauseResume => Status is DownloadStatus.Downloading or DownloadStatus.Paused;
    public bool CanCancel      => Status is DownloadStatus.Downloading or DownloadStatus.Paused or DownloadStatus.Queued;
    public bool CanOpen        => Status == DownloadStatus.Completed;
    public bool CanRetry       => Status is DownloadStatus.Failed or DownloadStatus.Cancelled;

    // ── Commands ───────────────────────────────────────────────────────────

    [RelayCommand]
    private Task PauseResume()
    {
        if (_queue is not null && Entry is not null)
        {
            if (IsDownloading) { _queue.PauseEntry(Entry); return Task.CompletedTask; }
            if (IsPaused)      { _queue.RequeueEntry(Entry); return Task.CompletedTask; }
            return Task.CompletedTask;
        }
        if (_service is null)
        {
            if (Status == DownloadStatus.Downloading) Status = DownloadStatus.Paused;
            else if (Status == DownloadStatus.Paused) Status = DownloadStatus.Downloading;
            return Task.CompletedTask;
        }
        if (IsDownloading) { _service.Pause(this); return Task.CompletedTask; }
        if (IsPaused)      return _service.ResumeAsync(this);
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void Cancel()
    {
        if (_queue is not null && Entry is not null) { _queue.CancelEntry(Entry); return; }
        if (_service is null) { if (CanCancel) Status = DownloadStatus.Cancelled; return; }
        _service.Cancel(this);
    }

    [RelayCommand]
    private void OpenFile()
    {
        if (string.IsNullOrEmpty(DestinationPath)) return;
        if (File.Exists(DestinationPath))
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(DestinationPath) { UseShellExecute = true });
    }

    [RelayCommand]
    private void ShowInFolder()
    {
        if (!string.IsNullOrEmpty(DestinationPath) && File.Exists(DestinationPath))
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{DestinationPath}\"");
        else if (!string.IsNullOrEmpty(DestinationPath) && Directory.Exists(Path.GetDirectoryName(DestinationPath)))
            System.Diagnostics.Process.Start("explorer.exe", $"\"{Path.GetDirectoryName(DestinationPath)}\"");
    }

    [RelayCommand]
    private Task Retry()
    {
        if (_queue is not null && Entry is not null)
        {
            _queue.RequeueEntry(Entry);
            return Task.CompletedTask;
        }
        if (_service is null)
        {
            DownloadedBytes = 0; ErrorMessage = ""; Status = DownloadStatus.Queued;
            return Task.CompletedTask;
        }
        return _service.ResumeAsync(this);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    public static string FormatBytes(long bytes)  // public: used by QueueItemViewModel
    {
        if (bytes < 1024)               return $"{bytes} B";
        if (bytes < 1024 * 1024)        return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
