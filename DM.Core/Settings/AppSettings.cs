using DM.Core.Models;
using DM.Core.PostDownload;
using DM.Core.Queue;

namespace DM.Core.Settings;

/// <summary>
/// Single source of truth for all user-configurable options.
/// Loaded from JSON on startup; defaults are used if the file is missing or corrupt.
/// </summary>
public sealed class AppSettings
{
    // ── General ────────────────────────────────────────────────────────────────
    public string DefaultDownloadFolder { get; set; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
    public bool ClipboardMonitor { get; set; } = true;
    public bool StartInTray      { get; set; } = false;
    public bool ResumeOnRestart  { get; set; } = true;
    // ── Licensing ──────────────────────────────────────────────────────────────
    /// <summary>Base URL of the license validation server. Trailing slash not required.</summary>
    public string LicenseServerUrl { get; set; } = "http://localhost:5000";

    // ── Browser Extension ──────────────────────────────────────────────────────
    /// <summary>Run a localhost HTTP server so the browser extension can push detected media URLs.</summary>
    public bool LocalServerEnabled { get; set; } = true;
    /// <summary>TCP port the localhost connector listens on (1024–65535). Default: 6336.</summary>
    public int  LocalServerPort    { get; set; } = 6336;

    // ── Downloads ──────────────────────────────────────────────────────────────
    public int              MaxConcurrentDownloads { get; set; } = 3;
    public ConnectionMode   ConnectionMode         { get; set; } = ConnectionMode.Auto;
    /// <summary>Used when <see cref="ConnectionMode"/> is <see cref="ConnectionMode.Manual"/>.</summary>
    public int              ManualConnectionCount  { get; set; } = 8;
    /// <summary>Auto-tuner starts here and ramps up to <see cref="AutoMaxConnections"/>.</summary>
    public int              AutoMinConnections     { get; set; } = 8;
    public int              AutoMaxConnections     { get; set; } = 32;
    public DownloadPriority DefaultPriority        { get; set; } = DownloadPriority.Normal;
    public RetryPolicy      RetryPolicy            { get; set; } = new();

    // ── Speed & Schedule ───────────────────────────────────────────────────────
    /// <summary>0 = unlimited.</summary>
    public long              GlobalSpeedLimitBytesPerSecond      { get; set; } = 0;
    /// <summary>Per-download cap; 0 = unlimited.</summary>
    public long              PerDownloadSpeedLimitBytesPerSecond { get; set; } = 0;
    public SchedulerSettings BandwidthSchedule { get; set; } = new();
    public List<SpeedRule>   SpeedRules        { get; set; } = [];

    // ── Tools ──────────────────────────────────────────────────────────────────
    /// <summary>Directory containing yt-dlp.exe and ffmpeg.exe. Empty = use app's own Tools subfolder.</summary>
    public string ToolsDirectory { get; set; } = "";

    // ── Video ──────────────────────────────────────────────────────────────────
    public VideoQualityPreset DefaultVideoQuality   { get; set; } = VideoQualityPreset.P1080OrBest;
    /// <summary>"mp4", "mkv", "webm", or "best" (let yt-dlp choose).</summary>
    public string             VideoFormatPreference { get; set; } = "mp4";
    public bool               EmbedSubtitles        { get; set; } = false;
    public bool               EmbedThumbnail        { get; set; } = false;
    /// <summary>Path to a Netscape-format cookies.txt file passed to yt-dlp --cookies. Empty = no cookies file.</summary>
    public string             CookiesFilePath       { get; set; } = "";
    /// <summary>yt-dlp YouTube player client API. Comma-separated list tried in order. Default covers bot detection + age restriction without cookies.</summary>
    public string             YtdlpPlayerClient     { get; set; } = "android_vr,tv_embedded,ios";
    /// <summary>Pass --geo-bypass to yt-dlp. Fakes X-Forwarded-For header to bypass geographic content restrictions on any site.</summary>
    public bool               GeoBypass             { get; set; } = false;

    // ── File Management ────────────────────────────────────────────────────────
    public CategorizationSettings Categorization { get; set; } = new();

    // ── Post-Download ──────────────────────────────────────────────────────────
    public PostDownloadActionSettings PostDownload      { get; set; } = new();
    public bool                       AutoVerifyChecksum { get; set; } = false;

    // ── Appearance (UI — not used by DM.Core, owned here for single persistence) ──
    public AppTheme Theme          { get; set; } = AppTheme.Dark;
    public string?  AccentColorHex { get; set; }
}

// ── Enums ──────────────────────────────────────────────────────────────────────

public enum ConnectionMode
{
    /// <summary>Start at <see cref="AppSettings.AutoMinConnections"/> and auto-tune up to max.</summary>
    Auto,
    /// <summary>Fixed count given by <see cref="AppSettings.ManualConnectionCount"/>.</summary>
    Manual,
}

public enum VideoQualityPreset
{
    BestAvailable,  // let yt-dlp pick the best
    P2160,          // 4K UHD
    P1440,          // 2K / 1440p
    P1080OrBest,    // 1080p, or best if 1080p unavailable
    P720,
    P480,
    P360,
    AudioOnly,      // audio-only, best quality
}

public enum AppTheme { Dark, Light, FollowSystem }

// ── Sub-models ─────────────────────────────────────────────────────────────────

public sealed class RetryPolicy
{
    public int    MaxRetries        { get; set; } = 3;
    public int    InitialDelaySecs  { get; set; } = 5;
    public double BackoffMultiplier { get; set; } = 2.0;
    public int    MaxDelaySecs      { get; set; } = 120;
}
