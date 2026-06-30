namespace DM.Core.VideoStreaming;

public sealed class PlaylistEntry
{
    public int     Index     { get; init; }
    public string  Url       { get; init; } = "";
    public string  Title     { get; init; } = "";
    public double? Duration  { get; init; }
    public string? Thumbnail { get; init; }

    public string DurationDisplay
    {
        get
        {
            if (Duration is null or <= 0) return "";
            var ts = TimeSpan.FromSeconds(Duration.Value);
            return ts.TotalHours >= 1
                ? ts.ToString(@"h\:mm\:ss")
                : ts.ToString(@"m\:ss");
        }
    }
}

/// <summary>Result from <see cref="YtDlpWrapper.FetchPlaylistOrVideoAsync"/>.</summary>
public sealed record PlaylistOrVideo(
    bool           IsPlaylist,
    string         PlaylistTitle,
    PlaylistEntry[] Entries,      // populated when IsPlaylist = true
    VideoMetadata? Video);        // populated when IsPlaylist = false
