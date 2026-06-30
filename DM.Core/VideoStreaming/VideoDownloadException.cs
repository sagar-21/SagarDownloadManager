namespace DM.Core.VideoStreaming;

public enum VideoDownloadFailureReason
{
    DrmProtected,
    UnsupportedSite,
    ExtractionError,
    ProcessNotFound,
    Cancelled,
}

public sealed class VideoDownloadException : Exception
{
    public VideoDownloadFailureReason Reason { get; }

    public VideoDownloadException(
        VideoDownloadFailureReason reason,
        string message,
        Exception? inner = null)
        : base(message, inner)
    {
        Reason = reason;
    }
}
