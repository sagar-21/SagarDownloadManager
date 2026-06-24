namespace DM.Core.Downloading;

/// <summary>
/// Immutable snapshot of a download's current state.
/// Reported roughly every 250 ms during an active transfer.
/// Using a readonly record struct means zero heap allocations per progress tick.
/// </summary>
public readonly record struct DownloadProgress(
    long BytesReceived,
    long TotalBytes,               // −1 when the server omits Content-Length
    double SpeedBytesPerSecond)
{
    /// <summary>0–100, or <see cref="double.NaN"/> when the total size is unknown.</summary>
    public double Percentage => TotalBytes > 0
        ? (double)BytesReceived / TotalBytes * 100.0
        : double.NaN;

    public bool IsSizeKnown => TotalBytes > 0;
}
