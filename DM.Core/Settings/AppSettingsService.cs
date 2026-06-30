using System.Text.Json;
using System.Text.Json.Serialization;

namespace DM.Core.Settings;

/// <summary>
/// Loads, validates, persists, and broadcasts changes to <see cref="AppSettings"/>.
/// All callers share one instance; changes applied here immediately propagate via
/// the <see cref="Changed"/> event.
/// </summary>
public sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented              = true,
        PropertyNamingPolicy       = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition     = JsonIgnoreCondition.WhenWritingNull,
        Converters                 = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DownloadManager", "settings.json");

    private readonly object _lock = new();

    /// <summary>Current effective settings. Never null; always validated.</summary>
    public AppSettings Current { get; private set; } = new();

    /// <summary>Fired on the calling thread after <see cref="Apply"/> validates and saves.</summary>
    public event Action<AppSettings>? Changed;

    // ── Load ──────────────────────────────────────────────────────────────────

    public void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                SaveSync();   // write defaults so the file exists for the user to inspect
                return;
            }

            var text   = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(text, JsonOpts);
            if (loaded is not null)
            {
                Validate(loaded);
                lock (_lock) { Current = loaded; }
            }
        }
        catch
        {
            // Malformed JSON → keep in-memory defaults; overwrite on next Apply()
        }
    }

    // ── Apply / Reset ─────────────────────────────────────────────────────────

    /// <summary>Validates, makes current, fires <see cref="Changed"/>, and saves asynchronously.</summary>
    public void Apply(AppSettings settings)
    {
        Validate(settings);
        lock (_lock) { Current = settings; }
        Changed?.Invoke(settings);
        _ = SaveAsync(settings);
    }

    /// <summary>Resets every setting to defaults.</summary>
    public void Reset() => Apply(new AppSettings());

    /// <summary>Resets only the named section to defaults; all others are preserved.</summary>
    public void ResetSection(SettingsSection section)
    {
        var d = new AppSettings();   // defaults
        var s = Clone(Current);

        switch (section)
        {
            case SettingsSection.General:
                s.DefaultDownloadFolder = d.DefaultDownloadFolder;
                s.ClipboardMonitor      = d.ClipboardMonitor;
                s.StartInTray           = d.StartInTray;
                s.ResumeOnRestart       = d.ResumeOnRestart;
                s.LocalServerEnabled    = d.LocalServerEnabled;
                s.LocalServerPort       = d.LocalServerPort;
                break;
            case SettingsSection.Downloads:
                s.MaxConcurrentDownloads = d.MaxConcurrentDownloads;
                s.ConnectionMode         = d.ConnectionMode;
                s.ManualConnectionCount  = d.ManualConnectionCount;
                s.AutoMinConnections     = d.AutoMinConnections;
                s.AutoMaxConnections     = d.AutoMaxConnections;
                s.DefaultPriority        = d.DefaultPriority;
                s.RetryPolicy            = new();
                break;
            case SettingsSection.SpeedAndSchedule:
                s.GlobalSpeedLimitBytesPerSecond      = 0;
                s.PerDownloadSpeedLimitBytesPerSecond = 0;
                s.BandwidthSchedule = new();
                s.SpeedRules        = [];
                break;
            case SettingsSection.Video:
                s.DefaultVideoQuality   = d.DefaultVideoQuality;
                s.VideoFormatPreference = d.VideoFormatPreference;
                s.EmbedSubtitles        = d.EmbedSubtitles;
                s.EmbedThumbnail        = d.EmbedThumbnail;
                s.CookiesFilePath       = d.CookiesFilePath;
                break;
            case SettingsSection.FileManagement:
                s.Categorization = new();
                break;
            case SettingsSection.PostDownload:
                s.PostDownload       = new();
                s.AutoVerifyChecksum = d.AutoVerifyChecksum;
                break;
            case SettingsSection.Appearance:
                s.Theme          = d.Theme;
                s.AccentColorHex = null;
                break;
        }

        Apply(s);
    }

    // ── Validation ────────────────────────────────────────────────────────────

    private static void Validate(AppSettings s)
    {
        s.MaxConcurrentDownloads         = Math.Clamp(s.MaxConcurrentDownloads, 1, 20);
        s.ManualConnectionCount          = Math.Clamp(s.ManualConnectionCount, 1, 64);
        s.AutoMinConnections             = Math.Clamp(s.AutoMinConnections, 1, 64);
        s.AutoMaxConnections             = Math.Clamp(s.AutoMaxConnections, s.AutoMinConnections, 64);
        s.GlobalSpeedLimitBytesPerSecond      = Math.Max(0, s.GlobalSpeedLimitBytesPerSecond);
        s.PerDownloadSpeedLimitBytesPerSecond = Math.Max(0, s.PerDownloadSpeedLimitBytesPerSecond);

        s.RetryPolicy ??= new();
        s.RetryPolicy.MaxRetries        = Math.Clamp(s.RetryPolicy.MaxRetries, 0, 20);
        s.RetryPolicy.InitialDelaySecs  = Math.Clamp(s.RetryPolicy.InitialDelaySecs, 1, 300);
        s.RetryPolicy.BackoffMultiplier = Math.Clamp(s.RetryPolicy.BackoffMultiplier, 1.0, 10.0);
        s.RetryPolicy.MaxDelaySecs      = Math.Clamp(s.RetryPolicy.MaxDelaySecs, 5, 3600);

        s.LocalServerPort = Math.Clamp(s.LocalServerPort, 1024, 65535);

        if (string.IsNullOrWhiteSpace(s.DefaultDownloadFolder))
            s.DefaultDownloadFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        if (string.IsNullOrWhiteSpace(s.VideoFormatPreference))
            s.VideoFormatPreference = "mp4";

        s.BandwidthSchedule ??= new();
        s.SpeedRules        ??= [];
        s.Categorization    ??= new();
        s.PostDownload      ??= new();
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    private void SaveSync()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, JsonOpts));
        }
        catch { }
    }

    private static async Task SaveAsync(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            await File.WriteAllTextAsync(FilePath, JsonSerializer.Serialize(s, JsonOpts));
        }
        catch { }
    }

    private static AppSettings Clone(AppSettings s)
    {
        var json = JsonSerializer.Serialize(s, JsonOpts);
        return JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new();
    }
}

// ── Section enum ──────────────────────────────────────────────────────────────

public enum SettingsSection
{
    All,
    General,
    Downloads,
    SpeedAndSchedule,
    Video,
    FileManagement,
    PostDownload,
    Appearance,
}
