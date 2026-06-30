using DM.Core.Downloading;

namespace DM.Core.VideoStreaming;

/// <summary>
/// Public facade for video/stream downloads.  Wraps <see cref="YtDlpWrapper"/>
/// and exposes the same progress contract as the HTTP <c>Downloader</c>, so the
/// GUI can treat both download types uniformly.
/// </summary>
public sealed class VideoDownloadService
{
    private readonly YtDlpWrapper _wrapper;

    public VideoDownloadService(YtDlpSettings? settings = null, IToolPathResolver? tools = null)
    {
        var s   = settings ?? new YtDlpSettings();
        _wrapper = new YtDlpWrapper(s, tools ?? new DefaultToolPathResolver(s));
    }

    internal VideoDownloadService(YtDlpWrapper wrapper)
    {
        _wrapper = wrapper;
    }

    /// <summary>
    /// Fetches available formats for <paramref name="url"/>.
    /// The returned list is sorted: combined (video+audio) formats first by resolution,
    /// then video-only, then audio-only.  Prepend <see cref="VideoFormat.BestQuality"/>
    /// to let the user choose automatic best quality.
    ///
    /// Throws <see cref="VideoDownloadException"/>(<see cref="VideoDownloadFailureReason.DrmProtected"/>)
    /// when all formats are DRM-protected.
    /// </summary>
    public Task<VideoMetadata> GetFormatsAsync(string url, CancellationToken ct = default,
        string cookiesFilePath = "", string playerClient = "android_vr", bool geoBypass = false)
        => _wrapper.FetchMetadataAsync(url, ct, cookiesFilePath, playerClient, geoBypass);

    /// <summary>
    /// Probes <paramref name="url"/> with <c>--flat-playlist</c> and returns either a
    /// <see cref="PlaylistOrVideo"/> containing all playlist entries or a single video with
    /// full format metadata.  Use this from the Add-Download dialog so one call handles both cases.
    /// </summary>
    public Task<PlaylistOrVideo> GetPlaylistOrVideoAsync(string url, CancellationToken ct = default,
        string cookiesFilePath = "", string playerClient = "android_vr", bool geoBypass = false)
        => _wrapper.FetchPlaylistOrVideoAsync(url, ct, cookiesFilePath, playerClient, geoBypass);

    /// <summary>
    /// Downloads <paramref name="url"/> in the given format to <paramref name="destPath"/>,
    /// reporting progress through the same <see cref="DownloadProgress"/> struct used by
    /// the HTTP downloader.
    ///
    /// Pass <see cref="VideoFormat.BestQuality"/>.Id to let yt-dlp choose automatically,
    /// or a specific <see cref="VideoFormat.Id"/> obtained from <see cref="GetFormatsAsync"/>.
    /// </summary>
    public async Task DownloadAsync(
        string url,
        string formatId,
        string destPath,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default,
        long speedLimitBytesPerSec = 0,
        string cookiesFilePath = "",
        string playerClient = "android_vr",
        bool geoBypass = false)
    {
        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await _wrapper.DownloadAsync(url, formatId, destPath, progress, ct, speedLimitBytesPerSec, cookiesFilePath, playerClient, geoBypass);
    }
}
