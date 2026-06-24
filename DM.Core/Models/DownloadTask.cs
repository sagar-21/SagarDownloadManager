namespace DM.Core.Models;

public enum DownloadStatus { Queued, Downloading, Paused, Completed, Failed, Cancelled }

public sealed class DownloadTask
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Url { get; init; }
    public required string DestinationPath { get; init; }
    public string? FileName { get; set; }
    public long TotalBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}
