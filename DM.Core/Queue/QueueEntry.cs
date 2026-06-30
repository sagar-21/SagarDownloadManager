using DM.Core.Models;
using DM.Core.PostDownload;

namespace DM.Core.Queue;

public sealed class QueueEntry
{
    public Guid   Id              { get; } = Guid.NewGuid();
    public string Url             { get; init; } = "";
    /// <summary>Updated by categorization after a successful download.</summary>
    public string DestinationPath { get; set; } = "";

    public DownloadPriority Priority  { get; set; } = DownloadPriority.Normal;
    public int              SortIndex { get; set; }

    public DownloadStatus Status              { get; set; } = DownloadStatus.Queued;
    public long           TotalBytes          { get; set; }
    public long           DownloadedBytes     { get; set; }
    public double         SpeedBytesPerSecond { get; set; }
    public string?        ErrorMessage        { get; set; }
    public string?        ChecksumSummary     { get; set; }
    public bool           ChecksumOk          { get; set; } = true;

    public PerDownloadAction PostAction       { get; set; } = PerDownloadAction.None;
    public string?           ExpectedChecksum { get; set; }

    /// <summary>yt-dlp format ID for video downloads. Null = best quality (auto).</summary>
    public string?           VideoFormatId    { get; set; }

    /// <summary>Per-download overrides; null fields fall back to global <see cref="DM.Core.Settings.AppSettings"/>.</summary>
    public DownloadOverrides? Overrides { get; set; }

    internal CancellationTokenSource? ActiveCts { get; set; }
}
