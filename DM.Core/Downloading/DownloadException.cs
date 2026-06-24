namespace DM.Core.Downloading;

public enum DownloadFailureReason
{
    NetworkError,   // DNS failure, connection refused, socket reset
    ServerError,    // 4xx / 5xx HTTP response
    Timeout,        // HttpClient's own timeout fired (not the caller's CancellationToken)
    Cancelled,      // caller-triggered CancellationToken
    IoError,        // disk write failure
}

public sealed class DownloadException : Exception
{
    public DownloadFailureReason Reason { get; }

    /// <summary>Set for <see cref="DownloadFailureReason.ServerError"/>; null otherwise.</summary>
    public int? HttpStatusCode { get; }

    public DownloadException(
        DownloadFailureReason reason,
        string message,
        Exception? inner = null,
        int? statusCode = null)
        : base(message, inner)
    {
        Reason = reason;
        HttpStatusCode = statusCode;
    }
}
