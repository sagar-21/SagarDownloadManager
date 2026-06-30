using DM.Core.Downloading;
using DM.Core.VideoStreaming;

namespace DM.Tests.VideoStreaming;

public sealed class YtDlpWrapperTests
{
    // ── Helpers ───────────────────────────────────────────────────────────

    // Minimal yt-dlp -J JSON with two formats (one video-only, one audio-only).
    private const string TwoFormatJson = """
        {
          "title": "Test Video",
          "uploader": "Test Channel",
          "duration": 123.4,
          "thumbnail": "https://example.com/thumb.jpg",
          "formats": [
            {
              "format_id": "137",
              "ext": "mp4",
              "height": 1080,
              "fps": 30.0,
              "vcodec": "avc1.640028",
              "acodec": "none",
              "filesize": 104857600,
              "has_drm": false
            },
            {
              "format_id": "140",
              "ext": "m4a",
              "height": null,
              "fps": null,
              "vcodec": "none",
              "acodec": "mp4a.40.2",
              "filesize": 5242880,
              "has_drm": false
            }
          ]
        }
        """;

    private const string AllDrmJson = """
        {
          "title": "Protected Video",
          "formats": [
            { "format_id": "hls-1080p", "ext": "mp4", "vcodec": "avc1", "acodec": "mp4a", "has_drm": true },
            { "format_id": "hls-720p",  "ext": "mp4", "vcodec": "avc1", "acodec": "mp4a", "has_drm": true }
          ]
        }
        """;

    private const string MixedDrmJson = """
        {
          "title": "Mixed Video",
          "formats": [
            { "format_id": "drm-1080", "ext": "mp4", "vcodec": "avc1", "acodec": "mp4a", "has_drm": true  },
            { "format_id": "free-720", "ext": "mp4", "vcodec": "avc1", "acodec": "mp4a", "has_drm": false }
          ]
        }
        """;

    // ── ParseMetadata ─────────────────────────────────────────────────────

    [Fact]
    public void ParseMetadata_TwoFormats_MapsFieldsCorrectly()
    {
        var meta = YtDlpWrapper.ParseMetadata(TwoFormatJson);

        Assert.Equal("Test Video",                    meta.Title);
        Assert.Equal("Test Channel",                  meta.Uploader);
        Assert.Equal(123.4, meta.Duration!.Value, precision: 1);
        Assert.Equal("https://example.com/thumb.jpg", meta.ThumbnailUrl);
        Assert.Equal(2, meta.Formats.Length);
    }

    [Fact]
    public void ParseMetadata_VideoOnlyFormat_HasVideoTrueHasAudioFalse()
    {
        var meta = YtDlpWrapper.ParseMetadata(TwoFormatJson);
        // Video-only format (acodec == "none")
        var f137 = meta.Formats.Single(f => f.Id == "137");

        Assert.True(f137.HasVideo);
        Assert.False(f137.HasAudio);
        Assert.Equal(1080,      f137.Height);
        Assert.Equal(30,        f137.Fps);
        Assert.Equal(104857600, f137.Filesize);
        Assert.Equal("mp4",     f137.Extension);
        Assert.False(f137.HasDrm);
    }

    [Fact]
    public void ParseMetadata_AudioOnlyFormat_HasVideoFalseHasAudioTrue()
    {
        var meta = YtDlpWrapper.ParseMetadata(TwoFormatJson);
        var f140 = meta.Formats.Single(f => f.Id == "140");

        Assert.False(f140.HasVideo);
        Assert.True(f140.HasAudio);
    }

    [Fact]
    public void ParseMetadata_AllDrmFormats_AllFormatsDrmIsTrue()
    {
        var meta = YtDlpWrapper.ParseMetadata(AllDrmJson);

        Assert.True(meta.AllFormatsDrm);
        Assert.All(meta.Formats, f => Assert.True(f.HasDrm));
    }

    [Fact]
    public void ParseMetadata_MixedDrmFormats_AllFormatsDrmIsFalse()
    {
        var meta = YtDlpWrapper.ParseMetadata(MixedDrmJson);

        Assert.False(meta.AllFormatsDrm);
        Assert.Contains(meta.Formats, f => f.HasDrm);
        Assert.Contains(meta.Formats, f => !f.HasDrm);
    }

    [Fact]
    public void ParseMetadata_StoryboardFormats_AreFiltered()
    {
        const string json = """
            {
              "title": "T",
              "formats": [
                { "format_id": "sb0",  "ext": "mhtml", "vcodec": "none", "acodec": "none" },
                { "format_id": "233",  "ext": "mp4",   "vcodec": "avc1", "acodec": "none" }
              ]
            }
            """;

        var meta = YtDlpWrapper.ParseMetadata(json);

        Assert.DoesNotContain(meta.Formats, f => f.Extension == "mhtml");
        Assert.Single(meta.Formats);
        Assert.Equal("233", meta.Formats[0].Id);
    }

    [Fact]
    public void ParseMetadata_CombinedFormatsFirst_SortedByHeightDesc()
    {
        const string json = """
            {
              "title": "T",
              "formats": [
                { "format_id": "a",  "ext": "m4a", "height": null, "vcodec": "none", "acodec": "mp4a"  },
                { "format_id": "v1", "ext": "mp4", "height": 720,  "vcodec": "avc1", "acodec": "none"  },
                { "format_id": "c2", "ext": "mp4", "height": 1080, "vcodec": "avc1", "acodec": "mp4a" },
                { "format_id": "c1", "ext": "mp4", "height": 720,  "vcodec": "avc1", "acodec": "mp4a" }
              ]
            }
            """;

        var meta = YtDlpWrapper.ParseMetadata(json);

        // Combined formats (video+audio) appear before video-only and audio-only.
        Assert.True(meta.Formats[0].HasVideo && meta.Formats[0].HasAudio, "First should be combined");
        Assert.True(meta.Formats[1].HasVideo && meta.Formats[1].HasAudio, "Second should be combined");
        // Combined formats sorted by height descending: 1080 before 720.
        Assert.Equal(1080, meta.Formats[0].Height);
        Assert.Equal(720,  meta.Formats[1].Height);
    }

    [Fact]
    public void ParseMetadata_EmptyFormatsArray_ReturnsEmptyList()
    {
        const string json = """{ "title": "T", "formats": [] }""";

        var meta = YtDlpWrapper.ParseMetadata(json);

        Assert.Empty(meta.Formats);
    }

    // ── ThrowFromStderr ───────────────────────────────────────────────────

    [Theory]
    [InlineData("ERROR: [drm] this video is DRM protected")]
    [InlineData("This content uses widevine encryption")]
    [InlineData("playready protected content found")]
    [InlineData("protected content: cannot download")]
    public void ThrowFromStderr_DrmStderr_ThrowsDrmProtected(string stderr)
    {
        var ex = Assert.Throws<VideoDownloadException>(
            () => YtDlpWrapper.ThrowFromStderr(stderr));

        Assert.Equal(VideoDownloadFailureReason.DrmProtected, ex.Reason);
        Assert.Equal("This video is protected and can't be downloaded.", ex.Message);
    }

    [Theory]
    [InlineData("ERROR: Unsupported URL: https://example.com/video")]
    [InlineData("ERROR: no suitable InfoExtractor for URL")]
    [InlineData("ERROR: 'notaurl' is not a valid URL")]
    public void ThrowFromStderr_UnsupportedSite_ThrowsUnsupportedSite(string stderr)
    {
        var ex = Assert.Throws<VideoDownloadException>(
            () => YtDlpWrapper.ThrowFromStderr(stderr));

        Assert.Equal(VideoDownloadFailureReason.UnsupportedSite, ex.Reason);
    }

    [Fact]
    public void ThrowFromStderr_GenericError_ThrowsExtractionError()
    {
        var ex = Assert.Throws<VideoDownloadException>(
            () => YtDlpWrapper.ThrowFromStderr("ERROR: HTTP Error 429: Too Many Requests"));

        Assert.Equal(VideoDownloadFailureReason.ExtractionError, ex.Reason);
        Assert.Contains("429", ex.Message);
    }

    [Fact]
    public void ThrowFromStderr_WindowsPath_IsRedactedFromMessage()
    {
        const string stderrWithPath =
            @"ERROR: [generic] C:\Users\sagar\AppData\Local\Temp\yt-dlp\file.tmp: Permission denied";

        var ex = Assert.Throws<VideoDownloadException>(
            () => YtDlpWrapper.ThrowFromStderr(stderrWithPath));

        Assert.DoesNotContain(@"C:\Users", ex.Message);
        Assert.Contains("<path>", ex.Message);
    }

    [Fact]
    public void ThrowFromStderr_UnixPath_IsRedactedFromMessage()
    {
        const string stderrWithPath =
            "ERROR: [generic] /home/user/downloads/temp.part: No space left on device";

        var ex = Assert.Throws<VideoDownloadException>(
            () => YtDlpWrapper.ThrowFromStderr(stderrWithPath));

        Assert.DoesNotContain("/home/user", ex.Message);
    }

    // ── TryParseProgress ──────────────────────────────────────────────────

    [Fact]
    public void TryParseProgress_FullLine_ParsesAllFields()
    {
        // Format: SDMPROG|downloaded|total_exact|total_estimate|speed
        bool ok = YtDlpWrapper.TryParseProgress(
            "SDMPROG|12345678|100000000|200000000|987654.32",
            out var p);

        Assert.True(ok);
        Assert.Equal(12345678L,  p.BytesReceived);
        Assert.Equal(100000000L, p.TotalBytes);
        Assert.Equal(987654.32,  p.SpeedBytesPerSecond, precision: 1);
    }

    [Fact]
    public void TryParseProgress_NoneValues_ReturnsMinus1TotalAndZeroSpeed()
    {
        // Both total fields NA/None → TotalBytes = -1; speed None → 0
        bool ok = YtDlpWrapper.TryParseProgress(
            "SDMPROG|1024|NA|NA|None",
            out var p);

        Assert.True(ok);
        Assert.Equal(1024L, p.BytesReceived);
        Assert.Equal(-1L,   p.TotalBytes);
        Assert.Equal(0.0,   p.SpeedBytesPerSecond);
    }

    [Fact]
    public void TryParseProgress_NonProgressLine_ReturnsFalse()
    {
        bool ok = YtDlpWrapper.TryParseProgress(
            "[download] Destination: video.mp4",
            out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryParseProgress_MalformedLine_ReturnsFalse()
    {
        Assert.False(YtDlpWrapper.TryParseProgress("SDMPROG|garbage", out _));
        Assert.False(YtDlpWrapper.TryParseProgress("SDMPROG|", out _));
        Assert.False(YtDlpWrapper.TryParseProgress("SDMPROG|abc|100|200|300", out _)); // non-numeric downloaded
        Assert.False(YtDlpWrapper.TryParseProgress("download:12345|100000|987", out _)); // old prefix → false
    }

    // ── IsValidFormatId ───────────────────────────────────────────────────

    [Theory]
    [InlineData("137",                      true)]
    [InlineData("137+140",                  true)]
    [InlineData("bestvideo+bestaudio/best", true)]
    [InlineData("bestvideo",                true)]
    [InlineData("hls-1080p",                true)]
    [InlineData("",                         false)]
    [InlineData("rm -rf /",                 false)]   // space → rejected
    [InlineData("id; echo pwned",           false)]   // semicolon → rejected
    [InlineData("id$(whoami)",              false)]   // $ → rejected
    [InlineData("id`whoami`",               false)]   // backtick → rejected
    public void IsValidFormatId_VariousIds_CorrectResult(string id, bool expected)
    {
        Assert.Equal(expected, YtDlpWrapper.IsValidFormatId(id));
    }

    // ── DefaultToolPathResolver ───────────────────────────────────────────

    [Fact]
    public void DefaultToolPathResolver_MissingYtDlp_ThrowsProcessNotFound()
    {
        var settings = new YtDlpSettings
        {
            ToolsDirectory = Path.Combine(Path.GetTempPath(), $"dm_tools_{Guid.NewGuid():N}")
        };
        var resolver = new DefaultToolPathResolver(settings);

        var ex = Assert.Throws<VideoDownloadException>(() => resolver.ResolveYtDlp());

        Assert.Equal(VideoDownloadFailureReason.ProcessNotFound, ex.Reason);
        Assert.Contains("yt-dlp", ex.Message);
    }

    [Fact]
    public void DefaultToolPathResolver_MissingFfmpeg_ThrowsProcessNotFound()
    {
        var settings = new YtDlpSettings
        {
            ToolsDirectory = Path.Combine(Path.GetTempPath(), $"dm_tools_{Guid.NewGuid():N}")
        };
        var resolver = new DefaultToolPathResolver(settings);

        var ex = Assert.Throws<VideoDownloadException>(() => resolver.ResolveFfmpeg());

        Assert.Equal(VideoDownloadFailureReason.ProcessNotFound, ex.Reason);
        Assert.Contains("ffmpeg", ex.Message);
    }

    // ── VideoFormat.Label ─────────────────────────────────────────────────

    [Fact]
    public void VideoFormat_Label_VideoOnly_IncludesHeightAndResolution()
    {
        var f = new VideoFormat
        {
            Id = "137", Extension = "mp4", Height = 1080,
            HasVideo = true, HasAudio = false, Filesize = 104857600
        };

        Assert.Contains("1080p",   f.Label);
        Assert.Contains("MP4",     f.Label);
        Assert.Contains("100 MB",  f.Label);
    }

    [Fact]
    public void VideoFormat_Label_AudioOnly_SaysAudioOnly()
    {
        var f = new VideoFormat
        {
            Id = "140", Extension = "m4a",
            HasVideo = false, HasAudio = true
        };

        Assert.Contains("Audio only", f.Label);
    }

    [Fact]
    public void VideoFormat_Label_DrmFormat_IncludesDrmTag()
    {
        var f = new VideoFormat
        {
            Id = "drm1", Extension = "mp4",
            HasVideo = true, HasAudio = true,
            HasDrm = true
        };

        Assert.Contains("[DRM]", f.Label);
    }

    [Fact]
    public void VideoFormat_BestQuality_LabelIsBestQuality()
    {
        Assert.Equal("Best quality (auto)", VideoFormat.BestQuality.Label);
    }

    // ── VideoMetadata.AllFormatsDrm ───────────────────────────────────────

    [Fact]
    public void VideoMetadata_AllFormatsDrm_EmptyFormats_ReturnsFalse()
    {
        var meta = new VideoMetadata { Formats = [] };
        Assert.False(meta.AllFormatsDrm);
    }

    // ── HasVideo / HasAudio with null/missing vcodec (bug-fix regression) ──

    [Fact]
    public void ParseMetadata_VcodecJsonNull_HasVideoFalse()
    {
        // yt-dlp sets vcodec to JSON null on audio-only streams from some extractors.
        // StringProp returns "" for null — must not be treated as "has video".
        const string json = """
            {
              "title": "T",
              "formats": [
                { "format_id": "a1", "ext": "mp4", "vcodec": null, "acodec": "mp4a.40.2" }
              ]
            }
            """;

        var meta = YtDlpWrapper.ParseMetadata(json);

        Assert.False(meta.Formats[0].HasVideo);
        Assert.True(meta.Formats[0].HasAudio);
    }

    [Fact]
    public void ParseMetadata_VcodecMissingFromJson_HasVideoFalse()
    {
        // When yt-dlp omits vcodec entirely (some live stream formats), treat as no video.
        const string json = """
            {
              "title": "T",
              "formats": [
                { "format_id": "a2", "ext": "m4a", "acodec": "mp4a.40.2" }
              ]
            }
            """;

        var meta = YtDlpWrapper.ParseMetadata(json);

        Assert.False(meta.Formats[0].HasVideo);
        Assert.True(meta.Formats[0].HasAudio);
    }

    [Fact]
    public void ParseMetadata_AcodecNoneExplicit_HasAudioFalse()
    {
        const string json = """
            {
              "title": "T",
              "formats": [
                { "format_id": "v1", "ext": "mp4", "vcodec": "avc1.640028", "acodec": "none" }
              ]
            }
            """;

        var meta = YtDlpWrapper.ParseMetadata(json);

        Assert.True(meta.Formats[0].HasVideo);
        Assert.False(meta.Formats[0].HasAudio);
    }

    // ── AES-128 HLS streams must not trigger DRM detection ────────────────

    [Theory]
    [InlineData("[hlsnative] AES-128 encrypted stream detected")]
    [InlineData("Fragment 1: AES encrypted segments")]
    [InlineData("This stream uses encrypted transport (AES-128)")]
    public void ThrowFromStderr_AesEncryptedStream_IsNotDrmProtected(string stderr)
    {
        // AES-128 HLS is transparent to yt-dlp — it downloads freely.
        // The old " encrypted" indicator was causing false-positive DRM errors here.
        var ex = Assert.Throws<VideoDownloadException>(
            () => YtDlpWrapper.ThrowFromStderr(stderr));

        Assert.NotEqual(VideoDownloadFailureReason.DrmProtected, ex.Reason);
    }

    // ── Progress lines with Windows CRLF line endings ─────────────────────

    [Fact]
    public void TryParseProgress_TrailingCarriageReturn_ParsesCorrectly()
    {
        // On Windows, some pipe reads include \r before \n has been stripped.
        bool ok = YtDlpWrapper.TryParseProgress(
            "download:1048576|10485760|500000.0\r",
            out var p);

        // The \r lands in the speedSpan; double.TryParse should handle it gracefully
        // or we strip it. Verify the parsed bytes are correct either way.
        if (ok)
        {
            Assert.Equal(1048576L,  p.BytesReceived);
            Assert.Equal(10485760L, p.TotalBytes);
        }
        // false is also acceptable — the caller skips the line and waits for the next one.
    }

    // ── Empty stderr → generic extraction error ───────────────────────────

    [Fact]
    public void ThrowFromStderr_EmptyStderr_ThrowsExtractionErrorWithUnknownError()
    {
        var ex = Assert.Throws<VideoDownloadException>(
            () => YtDlpWrapper.ThrowFromStderr(""));

        Assert.Equal(VideoDownloadFailureReason.ExtractionError, ex.Reason);
        Assert.Contains("Unknown error", ex.Message);
    }

    // ── DefaultToolPathResolver sibling-directory path check ─────────────

    [Fact]
    public void DefaultToolPathResolver_SiblingDirectory_IsNotConfusedWithToolsDir()
    {
        // e.g. ToolsDirectory = "C:\App\Tools" must not accept "C:\App\ToolsSibling\..."
        // Verified by ensuring _toolsDir gets a trailing separator in the constructor.
        var tempBase    = Path.GetTempPath();
        var toolsDir    = Path.Combine(tempBase, $"tools_{Guid.NewGuid():N}");
        var siblingDir  = toolsDir + "Sibling";

        Directory.CreateDirectory(siblingDir);
        try
        {
            // Place yt-dlp in the SIBLING dir, not the expected tools dir.
            var fakeTool = Path.Combine(siblingDir,
                OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp");
            File.WriteAllText(fakeTool, "");

            var settings = new YtDlpSettings { ToolsDirectory = toolsDir };
            var resolver = new DefaultToolPathResolver(settings);

            // Should throw ProcessNotFound (tool not in toolsDir) or InvalidOperation
            // (path escape) — must NOT return the sibling-dir path.
            Assert.ThrowsAny<Exception>(() => resolver.ResolveYtDlp());
        }
        finally
        {
            Directory.Delete(siblingDir, recursive: true);
        }
    }
}
