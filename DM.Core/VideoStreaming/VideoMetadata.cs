namespace DM.Core.VideoStreaming;

public sealed class VideoMetadata
{
    public string        Title        { get; init; } = "";
    public string        Uploader     { get; init; } = "";
    public double?       Duration     { get; init; }
    public string?       ThumbnailUrl { get; init; }
    public VideoFormat[] Formats      { get; init; } = [];

    // True only when every available format carries DRM — truly undownloadable.
    public bool AllFormatsDrm =>
        Formats.Length > 0 && Formats.All(f => f.HasDrm);
}
