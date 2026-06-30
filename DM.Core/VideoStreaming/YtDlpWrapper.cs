using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DM.Core.Downloading;

namespace DM.Core.VideoStreaming;

/// <summary>
/// Drives yt-dlp and ffmpeg as child processes.
///
/// SECURITY — no shell injection is possible because all arguments are passed
/// through <see cref="ProcessStartInfo.ArgumentList"/> (an IList&lt;string&gt;),
/// which the OS quotes and escapes without involving a shell.
///
/// TESTABILITY — all parsing/detection logic is exposed as internal static
/// methods so tests can exercise them without spawning real processes.
/// </summary>
public sealed class YtDlpWrapper
{
    // Progress lines from yt-dlp stderr when --progress-template is active.
    // NOTE: In --progress-template "download:TEMPLATE", the "download:" part is a TYPE
    // SELECTOR consumed by yt-dlp — it is NOT included in the output.  We therefore
    // embed our own marker "SDMPROG|" inside the template body so we can identify lines.
    // Format: "SDMPROG|12345678|100000000|200000000|987654.32"
    //         "SDMPROG|12345678|NA|500000000|987654.32"  (exact unknown, estimate available)
    //         "SDMPROG|12345678|NA|NA|None"              (nothing known yet)
    private const string ProgressPrefix = "SDMPROG|";

    // Sentinel for MP3 audio-only extraction. Not a real yt-dlp format ID —
    // handled specially in DownloadAsync to add --extract-audio --audio-format mp3.
    public const string Mp3FormatId = "bestaudio_mp3";

    // Allowlist for format IDs: letters, digits, and the characters used by yt-dlp format
    // selectors (brackets, comparisons, equals, comma fallback).
    // Primary injection defence is ArgumentList; this is defence-in-depth.
    private static readonly Regex SafeFormatId =
        new(@"^[a-zA-Z0-9+\-_.\/\[\]=<>,]+$", RegexOptions.Compiled);

    // DRM-indicator phrases checked against lower-cased yt-dlp stderr output.
    // " encrypted" is intentionally absent: AES-128 HLS segments produce messages
    // like "AES-128 encrypted stream" but yt-dlp downloads them freely.
    // Only Widevine / PlayReady / explicit DRM markers indicate true content lock.
    private static readonly string[] DrmIndicators =
        ["drm", "widevine", "playready", "protected content"];

    private readonly YtDlpSettings     _settings;
    private readonly IToolPathResolver _tools;

    public YtDlpWrapper(YtDlpSettings settings, IToolPathResolver tools)
    {
        _settings = settings;
        _tools    = tools;
    }

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>
    /// Runs <c>yt-dlp -J</c> and returns parsed format metadata.
    /// Throws <see cref="VideoDownloadException"/>(<see cref="VideoDownloadFailureReason.DrmProtected"/>)
    /// when every available format is DRM-protected.
    /// </summary>
    public async Task<VideoMetadata> FetchMetadataAsync(string url, CancellationToken ct = default,
        string cookiesFilePath = "", string playerClient = "android_vr", bool geoBypass = false)
    {
        var ytdlp = _tools.ResolveYtDlp();

        var psi = BuildPsi(ytdlp);
        psi.ArgumentList.Add("-J");
        psi.ArgumentList.Add("--no-playlist");
        psi.ArgumentList.Add("--no-warnings");
        var safeClient = SanitizePlayerClient(playerClient);
        psi.ArgumentList.Add("--extractor-args");
        psi.ArgumentList.Add($"youtube:player_client={safeClient}");
        if (geoBypass) psi.ArgumentList.Add("--geo-bypass");
        if (!string.IsNullOrWhiteSpace(cookiesFilePath) && File.Exists(cookiesFilePath))
        {
            psi.ArgumentList.Add("--cookies");
            psi.ArgumentList.Add(cookiesFilePath);
        }
        psi.ArgumentList.Add(url);

        using var process = new Process { StartInfo = psi };
        try   { process.Start(); }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new VideoDownloadException(VideoDownloadFailureReason.ProcessNotFound,
                $"Failed to start yt-dlp: {ex.Message}", ex);
        }

        // Read both streams concurrently — prevents pipe-buffer deadlock on large JSON.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        // Apply metadata timeout independently of the caller's token.
        using var timeoutCts = new CancellationTokenSource(_settings.MetadataTimeout);
        using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await AwaitQuietly(stdoutTask, stderrTask);

            if (ct.IsCancellationRequested)
                throw new VideoDownloadException(
                    VideoDownloadFailureReason.Cancelled, "Metadata fetch cancelled.");

            throw new VideoDownloadException(
                VideoDownloadFailureReason.ExtractionError,
                $"yt-dlp did not respond within {_settings.MetadataTimeout.TotalSeconds:0}s.");
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;

        if (process.ExitCode != 0)
            ThrowFromStderr(stderr);

        var metadata = ParseMetadata(stdout);

        if (metadata.Formats.Length == 0)
            throw new VideoDownloadException(
                VideoDownloadFailureReason.ExtractionError,
                "No downloadable formats found for this URL.");

        if (metadata.AllFormatsDrm)
            throw new VideoDownloadException(
                VideoDownloadFailureReason.DrmProtected,
                "This video is protected and can't be downloaded.");

        return metadata;
    }

    /// <summary>
    /// Runs <c>yt-dlp -J --flat-playlist</c> and returns either a playlist (with lightweight
    /// per-entry metadata) or a single video with full format metadata.
    /// Use this instead of <see cref="FetchMetadataAsync"/> when the URL might be a playlist.
    /// </summary>
    public async Task<PlaylistOrVideo> FetchPlaylistOrVideoAsync(string url, CancellationToken ct = default,
        string cookiesFilePath = "", string playerClient = "android_vr", bool geoBypass = false)
    {
        var ytdlp = _tools.ResolveYtDlp();

        var psi = BuildPsi(ytdlp);
        psi.ArgumentList.Add("-J");
        psi.ArgumentList.Add("--flat-playlist");
        psi.ArgumentList.Add("--no-warnings");
        var safeClient = SanitizePlayerClient(playerClient);
        psi.ArgumentList.Add("--extractor-args");
        psi.ArgumentList.Add($"youtube:player_client={safeClient}");
        if (geoBypass) psi.ArgumentList.Add("--geo-bypass");
        if (!string.IsNullOrWhiteSpace(cookiesFilePath) && File.Exists(cookiesFilePath))
        {
            psi.ArgumentList.Add("--cookies");
            psi.ArgumentList.Add(cookiesFilePath);
        }
        psi.ArgumentList.Add(url);

        using var process = new Process { StartInfo = psi };
        try   { process.Start(); }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new VideoDownloadException(VideoDownloadFailureReason.ProcessNotFound,
                $"Failed to start yt-dlp: {ex.Message}", ex);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutCts = new CancellationTokenSource(_settings.MetadataTimeout);
        using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await AwaitQuietly(stdoutTask, stderrTask);

            if (ct.IsCancellationRequested)
                throw new VideoDownloadException(
                    VideoDownloadFailureReason.Cancelled, "Metadata fetch cancelled.");

            throw new VideoDownloadException(
                VideoDownloadFailureReason.ExtractionError,
                $"yt-dlp did not respond within {_settings.MetadataTimeout.TotalSeconds:0}s.");
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;

        if (process.ExitCode != 0)
            ThrowFromStderr(stderr);

        return ParsePlaylistOrVideo(stdout);
    }

    /// <summary>Parses the output of <c>yt-dlp -J --flat-playlist</c>.</summary>
    internal static PlaylistOrVideo ParsePlaylistOrVideo(string json)
    {
        using var doc  = JsonDocument.Parse(json);
        var       root = doc.RootElement;

        var type = StringProp(root, "_type");

        if (type == "playlist")
        {
            var title = StringProp(root, "title");
            if (string.IsNullOrEmpty(title)) title = StringProp(root, "id");

            var entries = new List<PlaylistEntry>();
            if (root.TryGetProperty("entries", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                int idx = 1;
                foreach (var e in arr.EnumerateArray())
                {
                    // webpage_url is always a full URL; url may be just an ID for YouTube
                    var entryUrl = StringProp(e, "webpage_url");
                    if (string.IsNullOrEmpty(entryUrl)) entryUrl = StringProp(e, "url");

                    if (!string.IsNullOrEmpty(entryUrl)
                        && !entryUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        entryUrl = $"https://www.youtube.com/watch?v={entryUrl}";

                    double? duration = e.TryGetProperty("duration", out var dp)
                        && dp.ValueKind == JsonValueKind.Number ? dp.GetDouble() : null;
                    var thumbnail = e.TryGetProperty("thumbnail", out var tp) ? tp.GetString() : null;

                    entries.Add(new PlaylistEntry
                    {
                        Index     = idx++,
                        Url       = entryUrl,
                        Title     = StringProp(e, "title"),
                        Duration  = duration,
                        Thumbnail = thumbnail,
                    });
                }
            }

            return new PlaylistOrVideo(
                IsPlaylist:    true,
                PlaylistTitle: title,
                Entries:       [.. entries],
                Video:         null);
        }

        // Single video — parse full metadata (formats, title, etc.)
        var meta = ParseMetadata(json);
        return new PlaylistOrVideo(IsPlaylist: false, PlaylistTitle: "", Entries: [], Video: meta);
    }

    /// <summary>
    /// Downloads the URL in the specified format, reporting progress via
    /// <paramref name="progress"/> using the same <see cref="DownloadProgress"/>
    /// struct as the existing HTTP downloader.
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
        bool isMp3 = formatId == Mp3FormatId;
        if (!isMp3 && !IsValidFormatId(formatId))
            throw new ArgumentException($"Invalid format ID '{formatId}'.", nameof(formatId));

        var ytdlp    = _tools.ResolveYtDlp();
        var ffmpegDir = Path.GetDirectoryName(_tools.ResolveFfmpeg())
                        ?? throw new InvalidOperationException("Cannot determine ffmpeg directory.");

        var psi = BuildPsi(ytdlp);
        if (isMp3)
        {
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("bestaudio/best");
            psi.ArgumentList.Add("-x");
            psi.ArgumentList.Add("--audio-format");
            psi.ArgumentList.Add("mp3");
        }
        else
        {
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(formatId);
        }
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(destPath);
        psi.ArgumentList.Add("--ffmpeg-location");
        psi.ArgumentList.Add(ffmpegDir);
        psi.ArgumentList.Add("--no-playlist");
        psi.ArgumentList.Add("--no-mtime");
        psi.ArgumentList.Add("--continue");
        var safeClient = SanitizePlayerClient(playerClient);
        psi.ArgumentList.Add("--extractor-args");
        psi.ArgumentList.Add($"youtube:player_client={safeClient}");
        if (geoBypass) psi.ArgumentList.Add("--geo-bypass");
        if (!string.IsNullOrWhiteSpace(cookiesFilePath) && File.Exists(cookiesFilePath))
        {
            psi.ArgumentList.Add("--cookies");
            psi.ArgumentList.Add(cookiesFilePath);
        }
        if (speedLimitBytesPerSec > 0)
        {
            psi.ArgumentList.Add("--rate-limit");
            psi.ArgumentList.Add(speedLimitBytesPerSec.ToString());
        }
        psi.ArgumentList.Add("--newline");
        // 4-field progress template (marker|downloaded|total_exact|total_estimate|speed).
        // "download:" is the yt-dlp type selector (stripped from output); "SDMPROG|"
        // is our own marker embedded inside the template so we can identify lines.
        // Variables must use the "progress." namespace prefix (yt-dlp >= 2021.x).
        psi.ArgumentList.Add("--progress-template");
        psi.ArgumentList.Add("download:SDMPROG|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s");
        psi.ArgumentList.Add(url);

        using var process = new Process { StartInfo = psi };
        try   { process.Start(); }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new VideoDownloadException(VideoDownloadFailureReason.ProcessNotFound,
                $"Failed to start yt-dlp: {ex.Message}", ex);
        }

        // yt-dlp writes INFO/[download]/progress-template output to STDOUT.
        // WARNING/ERROR lines go to STDERR.
        // We must drain BOTH streams concurrently to prevent pipe-buffer deadlock.

        // Track the last reported byte counts so the final 100% report is accurate.
        // Written only by stdoutTask; read only after awaiting stdoutTask (happens-before).
        var lastProgress = new long[] { 0L, -1L }; // [0] = downloaded, [1] = total

        // Read stdout line-by-line: progress lines → IProgress<T> (all other lines discarded).
        var stdoutTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
            {
                if (TryParseProgress(line, out var p))
                {
                    lastProgress[0] = p.BytesReceived;
                    lastProgress[1] = p.TotalBytes;
                    progress?.Report(p);
                }
            }
        }, CancellationToken.None);

        // Collect ERROR: lines from stderr for failure reporting.
        var errorLines = new StringBuilder();
        var stderrTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync()) is not null)
            {
                if (line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
                    errorLines.AppendLine(line);
            }
        }, CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await AwaitQuietly(stdoutTask, stderrTask);
            // Re-throw as OperationCanceledException so RunEntryAsync can distinguish
            // pause (entry.Status already == Paused) from a hard cancel/failure.
            ct.ThrowIfCancellationRequested();
            throw; // unreachable, but satisfies the compiler
        }

        await stdoutTask;
        await stderrTask;

        if (process.ExitCode != 0)
            ThrowFromStderr(errorLines.ToString());

        // Final report: speed=0, use last known totals so GUI shows 100% rather than NaN.
        long finalBytes = lastProgress[0];
        long finalTotal = lastProgress[1] > 0 ? lastProgress[1] : finalBytes;
        progress?.Report(new DownloadProgress(finalBytes, finalTotal, SpeedBytesPerSecond: 0));
    }

    // ── Internal helpers (also exercised directly by unit tests) ──────────

    /// <summary>Parses the JSON output of <c>yt-dlp -J</c>.</summary>
    internal static VideoMetadata ParseMetadata(string json)
    {
        using var doc  = JsonDocument.Parse(json);
        var       root = doc.RootElement;

        var title    = StringProp(root, "title");
        var uploader = StringProp(root, "uploader");
        double? duration = root.TryGetProperty("duration", out var dp)
                           && dp.ValueKind == JsonValueKind.Number
            ? dp.GetDouble() : null;
        var thumbnail = root.TryGetProperty("thumbnail", out var tp) ? tp.GetString() : null;

        var formats = new List<VideoFormat>();

        if (root.TryGetProperty("formats", out var fmts) && fmts.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in fmts.EnumerateArray())
            {
                var ext = StringProp(f, "ext");
                // Skip manifest/storyboard pseudo-formats.
                if (ext is "mhtml" or "vtt" or "json3") continue;

                var id      = StringProp(f, "format_id");
                var vcodec  = StringProp(f, "vcodec");
                var acodec  = StringProp(f, "acodec");
                int? height = IntProp(f, "height");
                int? fps    = DoubleProp(f, "fps") is { } d ? (int)d : null;
                long? size  = LongProp(f, "filesize") ?? LongProp(f, "filesize_approx");
                bool hasDrm = BoolProp(f, "has_drm");

                formats.Add(new VideoFormat
                {
                    Id         = id,
                    Extension  = ext,
                    Height     = height,
                    Fps        = fps,
                    VideoCodec = vcodec,
                    AudioCodec = acodec,
                    Filesize   = size,
                    // StringProp returns "" for missing/null JSON — check Length, not null.
                    HasVideo   = vcodec.Length > 0 && vcodec != "none",
                    HasAudio   = acodec.Length > 0 && acodec != "none",
                    HasDrm     = hasDrm,
                });
            }
        }

        // Sort: combined (video+audio) first by height desc, then video-only, then audio-only.
        formats.Sort((a, b) =>
        {
            int sa = (a.HasVideo ? 2 : 0) + (a.HasAudio ? 1 : 0);
            int sb = (b.HasVideo ? 2 : 0) + (b.HasAudio ? 1 : 0);
            if (sa != sb) return sb - sa;
            return (b.Height ?? 0) - (a.Height ?? 0);
        });

        return new VideoMetadata
        {
            Title        = title,
            Uploader     = uploader,
            Duration     = duration,
            ThumbnailUrl = thumbnail,
            Formats      = [.. formats],
        };
    }

    /// <summary>
    /// Parses one line from yt-dlp's stderr progress template.
    /// Line format: <c>download:&lt;bytes&gt;|&lt;total_exact&gt;|&lt;total_estimate&gt;|&lt;speed&gt;</c>
    /// where total fields may be "NA" and speed may be "None".
    /// Returns false for non-progress lines (info messages, merge notifications, etc.).
    /// </summary>
    internal static bool TryParseProgress(string line, out DownloadProgress progress)
    {
        progress = default;
        if (!line.StartsWith(ProgressPrefix, StringComparison.Ordinal))
            return false;

        var span  = line.AsSpan(ProgressPrefix.Length);
        int pipe1 = span.IndexOf('|');
        if (pipe1 < 0) return false;
        int pipe2 = span[(pipe1 + 1)..].IndexOf('|');
        if (pipe2 < 0) return false;
        pipe2 += pipe1 + 1;
        int pipe3 = span[(pipe2 + 1)..].IndexOf('|');
        if (pipe3 < 0) return false;
        pipe3 += pipe2 + 1;

        var downloadedSpan  = span[..pipe1];
        var totalExactSpan  = span[(pipe1 + 1)..pipe2];
        var totalEstSpan    = span[(pipe2 + 1)..pipe3];
        var speedSpan       = span[(pipe3 + 1)..];

        if (!long.TryParse(downloadedSpan, out long downloaded))
            return false;

        // Prefer exact total; fall back to estimate when exact is "NA".
        long total = -1;
        if (!ParseNaField(totalExactSpan, out total) || total <= 0)
            ParseNaField(totalEstSpan, out total);

        double speed = 0;
        if (!speedSpan.Equals("None", StringComparison.Ordinal))
            double.TryParse(speedSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out speed);

        progress = new DownloadProgress(downloaded, total, speed);
        return true;
    }

    private static bool ParseNaField(ReadOnlySpan<char> span, out long value)
    {
        value = -1;
        if (span.Equals("NA", StringComparison.Ordinal) ||
            span.Equals("None", StringComparison.Ordinal) ||
            span.IsEmpty)
            return false;
        return long.TryParse(span, out value);
    }

    /// <summary>
    /// Maps yt-dlp stderr content to the appropriate <see cref="VideoDownloadException"/>.
    /// DRM indicators are checked first so they always produce the user-friendly DRM message.
    /// </summary>
    internal static void ThrowFromStderr(string stderr)
    {
        var lower = stderr.ToLowerInvariant();

        foreach (var indicator in DrmIndicators)
        {
            if (lower.Contains(indicator))
                throw new VideoDownloadException(
                    VideoDownloadFailureReason.DrmProtected,
                    "This video is protected and can't be downloaded.");
        }

        if (lower.Contains("unsupported url")
            || lower.Contains("no suitable infoextractor")
            || lower.Contains("is not a valid url"))
        {
            throw new VideoDownloadException(
                VideoDownloadFailureReason.UnsupportedSite,
                "This site is not supported. Try downloading the file directly.");
        }

        throw new VideoDownloadException(
            VideoDownloadFailureReason.ExtractionError,
            $"yt-dlp failed: {SanitizeStderr(stderr)}");
    }

    /// <summary>
    /// Returns false for strings that could be misused as extra yt-dlp arguments.
    /// The real injection defence is ArgumentList; this is defence-in-depth.
    /// </summary>
    internal static bool IsValidFormatId(string id) =>
        !string.IsNullOrEmpty(id) && SafeFormatId.IsMatch(id);

    private static readonly HashSet<string> KnownPlayerClients =
        ["android_vr", "ios", "tv_embedded", "web", "android"];

    /// <summary>
    /// Validates player client against the known list.
    /// Accepts a comma-separated chain (e.g. "android_vr,tv_embedded,ios") —
    /// yt-dlp tries each in order, which lets a single pass cover both
    /// bot-detection bypass (android_vr) and age-restriction bypass (tv_embedded).
    /// Unknown tokens are silently dropped; falls back to android_vr if nothing is valid.
    /// </summary>
    private static string SanitizePlayerClient(string client)
    {
        if (string.IsNullOrWhiteSpace(client)) return "android_vr";
        var parts = client.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var valid  = parts.Where(KnownPlayerClients.Contains).ToList();
        return valid.Count > 0 ? string.Join(",", valid) : "android_vr";
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private static ProcessStartInfo BuildPsi(string executable) =>
        new(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

    /// <summary>
    /// Strips file-system paths and truncates stderr before surfacing it to the user.
    /// Avoids leaking internal directory structure in error messages.
    /// </summary>
    private static string SanitizeStderr(string stderr)
    {
        // Take only the last ERROR: line, or the last non-empty line.
        var lines = stderr.Split('\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var msg = lines.LastOrDefault(l =>
                      l.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
                  ?? lines.LastOrDefault()
                  ?? "Unknown error";

        if (msg.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
            msg = msg[6..].TrimStart();

        // Redact Windows and Unix absolute paths.
        msg = Regex.Replace(msg, @"[A-Za-z]:\\[^\s]+", "<path>");
        msg = Regex.Replace(msg, @"/(?:[^/\s]+/)+[^\s]*", "<path>");

        return msg.Length > 200 ? msg[..200] : msg;
    }

    private static async Task AwaitQuietly(params Task[] tasks)
    {
        await Task.WhenAll(tasks.Select(t =>
            t.ContinueWith(_ => { }, TaskScheduler.Default)));
    }

    // ── JSON helpers ──────────────────────────────────────────────────────

    private static string  StringProp(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";

    private static bool    BoolProp(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static int?    IntProp(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : null;

    private static long?   LongProp(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt64() : null;

    private static double? DoubleProp(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : null;
}
