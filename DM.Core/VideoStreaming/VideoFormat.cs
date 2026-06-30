namespace DM.Core.VideoStreaming;

public sealed class VideoFormat
{
    public string  Id         { get; init; } = "";
    public string  Extension  { get; init; } = "";
    public int?    Height     { get; init; }
    public int?    Fps        { get; init; }
    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }
    public long?   Filesize   { get; init; }
    public bool    HasVideo   { get; init; }
    public bool    HasAudio   { get; init; }
    public bool    HasDrm     { get; init; }

    // Convenience entry for "let yt-dlp pick the best available quality".
    public static VideoFormat BestQuality { get; } = new()
    {
        Id        = "bestvideo+bestaudio/best",
        Extension = "",
        HasVideo  = true,
        HasAudio  = true,
    };

    public string Label
    {
        get
        {
            if (this == BestQuality) return "Best quality (auto)";

            var parts = new List<string>(4);
            if (!HasVideo)         parts.Add("Audio only");
            else if (Height > 0)   parts.Add($"{Height}p");
            if (Fps > 0)           parts.Add($"{Fps}fps");
            if (!string.IsNullOrEmpty(Extension)) parts.Add(Extension.ToUpperInvariant());
            if (Filesize.HasValue) parts.Add($"(~{Filesize.Value / 1_048_576} MB)");
            if (HasDrm)            parts.Add("[DRM]");
            return parts.Count > 0 ? string.Join(" ", parts) : Id;
        }
    }
}
