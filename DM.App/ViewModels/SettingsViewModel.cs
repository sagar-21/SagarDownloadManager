using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DM.Core.Models;
using DM.Core.PostDownload;
using DM.Core.Queue;
using DM.Core.Settings;

namespace DM.App.ViewModels;

// ═════════════════════════════════════════════════════════════════════════════
// SettingsViewModel — drives the tabbed Settings page.
// All observable properties map 1:1 to AppSettings fields; changes commit
// immediately to AppSettingsService (which persists and fires Changed).
// ═════════════════════════════════════════════════════════════════════════════

public partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettingsService _service;
    private bool _loading;
    private bool _suppressExternalReload;

    // ── Active tab ────────────────────────────────────────────────────────────
    [ObservableProperty] private int _activeTab = 0;

    // ── GENERAL ───────────────────────────────────────────────────────────────
    [ObservableProperty] private string _defaultDownloadFolder = "";
    [ObservableProperty] private bool   _clipboardMonitor = true;
    [ObservableProperty] private bool   _startInTray      = false;
    [ObservableProperty] private bool   _resumeOnRestart  = true;
    [ObservableProperty] private bool   _localServerEnabled = true;
    [ObservableProperty] private int    _localServerPort    = 6336;

    // ── DOWNLOADS ─────────────────────────────────────────────────────────────
    [ObservableProperty] private int    _maxConcurrentDownloads = 3;
    [ObservableProperty] private bool   _connectionModeIsAuto   = true;

    /// <summary>Inverse of ConnectionModeIsAuto — for RadioButton two-way binding.</summary>
    public bool ConnectionModeIsManual
    {
        get => !ConnectionModeIsAuto;
        set => ConnectionModeIsAuto = !value;
    }

    [ObservableProperty] private int    _manualConnectionCount  = 8;
    [ObservableProperty] private int    _autoMinConnections     = 8;
    [ObservableProperty] private int    _autoMaxConnections     = 32;
    [ObservableProperty] private int    _defaultPriorityIndex   = 1;  // Normal
    [ObservableProperty] private int    _retryMaxRetries        = 3;
    [ObservableProperty] private int    _retryInitialDelaySecs  = 5;
    [ObservableProperty] private double _retryBackoffMultiplier = 2.0;

    // ── SPEED & SCHEDULE ──────────────────────────────────────────────────────
    [ObservableProperty] private bool   _globalSpeedLimitEnabled = false;
    [ObservableProperty] private double _globalSpeedLimitKBps    = 1024;
    [ObservableProperty] private double _perDownloadSpeedLimitKBps = 0;
    [ObservableProperty] private bool   _schedulerEnabled        = false;
    [ObservableProperty] private string _schedulerStartTime      = "08:00";
    [ObservableProperty] private string _schedulerStopTime       = "22:00";
    [ObservableProperty] private int    _schedulerRecurrenceIndex = 0;
    public ObservableCollection<SpeedRuleViewModel> SpeedRules { get; } = [];

    // ── VIDEO ─────────────────────────────────────────────────────────────────
    [ObservableProperty] private int  _defaultVideoQualityIndex = 3;  // P1080OrBest
    [ObservableProperty] private int  _videoFormatIndex         = 0;  // mp4
    [ObservableProperty] private bool _embedSubtitles           = false;
    [ObservableProperty] private bool _embedThumbnail           = false;
    [ObservableProperty] private string _cookiesFilePath = "";
    [ObservableProperty] private int  _playerClientIndex = 0;         // Smart (android_vr,tv_embedded,ios)
    [ObservableProperty] private bool _cookiesExpanded   = false;
    [ObservableProperty] private bool _geoBypass         = false;

    public bool HasCookiesFile => !string.IsNullOrEmpty(CookiesFilePath);

    // ── FILE MANAGEMENT ───────────────────────────────────────────────────────
    [ObservableProperty] private bool _categorizationEnabled = true;
    public ObservableCollection<CategorizationRuleViewModel> CategorizationRules { get; } = [];

    // ── POST-DOWNLOAD ─────────────────────────────────────────────────────────
    [ObservableProperty] private bool _alwaysNotify           = true;
    [ObservableProperty] private int  _queueFinishActionIndex = 1;  // ShowNotification
    [ObservableProperty] private bool _autoVerifyChecksum     = false;

    // ── APPEARANCE ────────────────────────────────────────────────────────────
    [ObservableProperty] private int    _themeIndex      = 0;   // Dark
    [ObservableProperty] private string _accentColorHex  = "";

    // ── Construction ─────────────────────────────────────────────────────────

    public SettingsViewModel(AppSettingsService service)
    {
        _service = service;
        LoadFrom(service.Current);
        service.Changed += OnServiceChanged;
    }

    private void OnServiceChanged(AppSettings s)
    {
        if (_suppressExternalReload) return;
        Application.Current?.Dispatcher.Invoke(() => LoadFrom(s));
    }

    // ── Commit helpers ───────────────────────────────────────────────────────

    private void CommitSettings()
    {
        if (_loading) return;
        _suppressExternalReload = true;
        try   { _service.Apply(BuildSettings()); }
        finally { _suppressExternalReload = false; }
    }

    // ── Partial change hooks (every property calls CommitSettings) ────────────

    partial void OnDefaultDownloadFolderChanged(string v)  => CommitSettings();
    partial void OnClipboardMonitorChanged(bool v)         => CommitSettings();
    partial void OnStartInTrayChanged(bool v)              => CommitSettings();
    partial void OnResumeOnRestartChanged(bool v)          => CommitSettings();
    partial void OnLocalServerEnabledChanged(bool v)       => CommitSettings();
    partial void OnLocalServerPortChanged(int v)           => CommitSettings();

    partial void OnMaxConcurrentDownloadsChanged(int v)    => CommitSettings();
    partial void OnConnectionModeIsAutoChanged(bool v)
    {
        OnPropertyChanged(nameof(ConnectionModeIsManual));
        CommitSettings();
    }
    partial void OnManualConnectionCountChanged(int v)     => CommitSettings();
    partial void OnAutoMinConnectionsChanged(int v)        => CommitSettings();
    partial void OnAutoMaxConnectionsChanged(int v)        => CommitSettings();
    partial void OnDefaultPriorityIndexChanged(int v)      => CommitSettings();
    partial void OnRetryMaxRetriesChanged(int v)           => CommitSettings();
    partial void OnRetryInitialDelaySecsChanged(int v)     => CommitSettings();
    partial void OnRetryBackoffMultiplierChanged(double v) => CommitSettings();

    partial void OnGlobalSpeedLimitEnabledChanged(bool v)  => CommitSettings();
    partial void OnGlobalSpeedLimitKBpsChanged(double v)   => CommitSettings();
    partial void OnPerDownloadSpeedLimitKBpsChanged(double v) => CommitSettings();
    partial void OnSchedulerEnabledChanged(bool v)         => CommitSettings();
    partial void OnSchedulerStartTimeChanged(string v)     => CommitSettings();
    partial void OnSchedulerStopTimeChanged(string v)      => CommitSettings();
    partial void OnSchedulerRecurrenceIndexChanged(int v)  => CommitSettings();

    partial void OnDefaultVideoQualityIndexChanged(int v)  => CommitSettings();
    partial void OnVideoFormatIndexChanged(int v)          => CommitSettings();
    partial void OnEmbedSubtitlesChanged(bool v)           => CommitSettings();
    partial void OnEmbedThumbnailChanged(bool v)           => CommitSettings();
    partial void OnCookiesFilePathChanged(string v)
    {
        OnPropertyChanged(nameof(HasCookiesFile));
        if (!string.IsNullOrEmpty(v)) CookiesExpanded = true;
        CommitSettings();
    }
    partial void OnPlayerClientIndexChanged(int v)         => CommitSettings();
    partial void OnGeoBypassChanged(bool v)                => CommitSettings();

    partial void OnCategorizationEnabledChanged(bool v)    => CommitSettings();

    partial void OnAlwaysNotifyChanged(bool v)             => CommitSettings();
    partial void OnQueueFinishActionIndexChanged(int v)    => CommitSettings();
    partial void OnAutoVerifyChecksumChanged(bool v)       => CommitSettings();

    partial void OnThemeIndexChanged(int v)                => CommitSettings();

    // ── Commands ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private void AddSpeedRule()
    {
        var rule = new SpeedRule
            { From = new TimeOnly(9, 0), To = new TimeOnly(17, 0), LimitBytesPerSecond = 512 * 1024 };
        SpeedRules.Add(new SpeedRuleViewModel(rule));
        CommitSettings();
    }

    [RelayCommand]
    private void RemoveSpeedRule(SpeedRuleViewModel? r)
    {
        if (r is null) return;
        SpeedRules.Remove(r);
        CommitSettings();
    }

    [RelayCommand]
    private void AddCategorizationRule()
    {
        var rule = new CategorizationRule
            { CategoryName = "New Category", TargetFolder = "New Category", Extensions = ["ext"] };
        CategorizationRules.Add(new CategorizationRuleViewModel(rule, CommitSettings));
        CommitSettings();
    }

    [RelayCommand]
    private void RemoveCategorizationRule(CategorizationRuleViewModel? r)
    {
        if (r is null) return;
        CategorizationRules.Remove(r);
        CommitSettings();
    }

    [RelayCommand]
    private void ResetSection(SettingsSection section) => _service.ResetSection(section);

    [RelayCommand]
    private void ResetAll() => _service.Reset();

    // ── Load from AppSettings ─────────────────────────────────────────────────

    private void LoadFrom(AppSettings s)
    {
        _loading = true;
        try
        {
            // General
            DefaultDownloadFolder = s.DefaultDownloadFolder;
            ClipboardMonitor      = s.ClipboardMonitor;
            StartInTray           = s.StartInTray;
            ResumeOnRestart       = s.ResumeOnRestart;
            LocalServerEnabled    = s.LocalServerEnabled;
            LocalServerPort       = s.LocalServerPort;

            // Downloads
            MaxConcurrentDownloads = s.MaxConcurrentDownloads;
            ConnectionModeIsAuto   = s.ConnectionMode == ConnectionMode.Auto;
            ManualConnectionCount  = s.ManualConnectionCount;
            AutoMinConnections     = s.AutoMinConnections;
            AutoMaxConnections     = s.AutoMaxConnections;
            DefaultPriorityIndex   = (int)s.DefaultPriority;
            RetryMaxRetries        = s.RetryPolicy.MaxRetries;
            RetryInitialDelaySecs  = s.RetryPolicy.InitialDelaySecs;
            RetryBackoffMultiplier = s.RetryPolicy.BackoffMultiplier;

            // Speed & Schedule
            GlobalSpeedLimitEnabled      = s.GlobalSpeedLimitBytesPerSecond > 0;
            GlobalSpeedLimitKBps         = s.GlobalSpeedLimitBytesPerSecond > 0
                ? s.GlobalSpeedLimitBytesPerSecond / 1024.0 : 1024;
            PerDownloadSpeedLimitKBps    = s.PerDownloadSpeedLimitBytesPerSecond / 1024.0;
            SchedulerEnabled             = s.BandwidthSchedule.Enabled;
            SchedulerStartTime           = s.BandwidthSchedule.StartTime.ToString("HH:mm");
            SchedulerStopTime            = s.BandwidthSchedule.StopTime.ToString("HH:mm");
            SchedulerRecurrenceIndex     = (int)s.BandwidthSchedule.Recurrence;
            SpeedRules.Clear();
            foreach (var r in s.SpeedRules)
                SpeedRules.Add(new SpeedRuleViewModel(r));

            // Video
            DefaultVideoQualityIndex = (int)s.DefaultVideoQuality;
            VideoFormatIndex         = VideoFormatToIndex(s.VideoFormatPreference);
            EmbedSubtitles           = s.EmbedSubtitles;
            EmbedThumbnail           = s.EmbedThumbnail;
            CookiesFilePath          = s.CookiesFilePath;
            PlayerClientIndex        = PlayerClientToIndex(s.YtdlpPlayerClient);
            CookiesExpanded          = !string.IsNullOrEmpty(s.CookiesFilePath);
            GeoBypass                = s.GeoBypass;

            // File management
            CategorizationEnabled = s.Categorization.Enabled;
            CategorizationRules.Clear();
            foreach (var r in s.Categorization.Rules)
                CategorizationRules.Add(new CategorizationRuleViewModel(r, CommitSettings));

            // Post-download
            AlwaysNotify           = s.PostDownload.AlwaysNotify;
            QueueFinishActionIndex = (int)s.PostDownload.QueueFinishAction;
            AutoVerifyChecksum     = s.AutoVerifyChecksum;

            // Appearance
            ThemeIndex     = (int)s.Theme;
            AccentColorHex = s.AccentColorHex ?? "";
        }
        finally { _loading = false; }
    }

    // ── Build AppSettings from observables ────────────────────────────────────

    private AppSettings BuildSettings()
    {
        TimeOnly.TryParse(SchedulerStartTime, out var schStart);
        TimeOnly.TryParse(SchedulerStopTime,  out var schStop);
        if (schStart == default) schStart = new TimeOnly(8,  0);
        if (schStop  == default) schStop  = new TimeOnly(22, 0);

        return new AppSettings
        {
            DefaultDownloadFolder = DefaultDownloadFolder,
            ClipboardMonitor      = ClipboardMonitor,
            StartInTray           = StartInTray,
            ResumeOnRestart       = ResumeOnRestart,
            LocalServerEnabled    = LocalServerEnabled,
            LocalServerPort       = LocalServerPort,

            MaxConcurrentDownloads = MaxConcurrentDownloads,
            ConnectionMode         = ConnectionModeIsAuto ? ConnectionMode.Auto : ConnectionMode.Manual,
            ManualConnectionCount  = ManualConnectionCount,
            AutoMinConnections     = AutoMinConnections,
            AutoMaxConnections     = AutoMaxConnections,
            DefaultPriority        = (DownloadPriority)Math.Clamp(DefaultPriorityIndex, 0, 2),
            RetryPolicy = new RetryPolicy
            {
                MaxRetries        = RetryMaxRetries,
                InitialDelaySecs  = RetryInitialDelaySecs,
                BackoffMultiplier = RetryBackoffMultiplier,
                MaxDelaySecs      = _service.Current.RetryPolicy.MaxDelaySecs,
            },

            GlobalSpeedLimitBytesPerSecond      = GlobalSpeedLimitEnabled
                ? (long)(GlobalSpeedLimitKBps * 1024) : 0,
            PerDownloadSpeedLimitBytesPerSecond = (long)(PerDownloadSpeedLimitKBps * 1024),
            BandwidthSchedule = new SchedulerSettings
            {
                Enabled    = SchedulerEnabled,
                StartTime  = schStart,
                StopTime   = schStop,
                Recurrence = (RecurrenceMode)Math.Clamp(SchedulerRecurrenceIndex, 0, 2),
                ActiveDays = _service.Current.BandwidthSchedule.ActiveDays,
            },
            SpeedRules = SpeedRules.Select(r => r.Rule).ToList(),

            DefaultVideoQuality   = (VideoQualityPreset)Math.Clamp(DefaultVideoQualityIndex, 0, 7),
            VideoFormatPreference = IndexToVideoFormat(VideoFormatIndex),
            EmbedSubtitles        = EmbedSubtitles,
            EmbedThumbnail        = EmbedThumbnail,
            CookiesFilePath       = CookiesFilePath,
            YtdlpPlayerClient     = IndexToPlayerClient(PlayerClientIndex),
            GeoBypass             = GeoBypass,

            Categorization = new CategorizationSettings
            {
                Enabled = CategorizationEnabled,
                Rules   = CategorizationRules.Select(r => r.ToRule()).ToList(),
            },
            PostDownload = new PostDownloadActionSettings
            {
                AlwaysNotify      = AlwaysNotify,
                QueueFinishAction = (QueueFinishAction)Math.Clamp(QueueFinishActionIndex, 0, 3),
            },
            AutoVerifyChecksum = AutoVerifyChecksum,

            Theme          = (AppTheme)Math.Clamp(ThemeIndex, 0, 2),
            AccentColorHex = string.IsNullOrEmpty(AccentColorHex) ? null : AccentColorHex,
        };
    }

    // ── Player client helpers ─────────────────────────────────────────────────

    private static readonly string[] PlayerClients =
    [
        "android_vr,tv_embedded,ios",  // 0 — Smart (recommended default)
        "android_vr",                  // 1
        "tv_embedded",                 // 2
        "ios",                         // 3
        "web",                         // 4
        "android",                     // 5
    ];

    private static int PlayerClientToIndex(string client)
    {
        var norm = client?.Trim().ToLowerInvariant() ?? "";
        var i    = Array.IndexOf(PlayerClients, norm);
        return i < 0 ? 0 : i;
    }

    private static string IndexToPlayerClient(int i)
        => i >= 0 && i < PlayerClients.Length ? PlayerClients[i] : PlayerClients[0];

    // ── Video format helpers ──────────────────────────────────────────────────

    private static readonly string[] VideoFormats = ["mp4", "mkv", "webm", "best"];

    private static int VideoFormatToIndex(string fmt)
    {
        var i = Array.IndexOf(VideoFormats, fmt?.ToLowerInvariant() ?? "mp4");
        return i < 0 ? 0 : i;
    }

    private static string IndexToVideoFormat(int i)
        => i >= 0 && i < VideoFormats.Length ? VideoFormats[i] : "mp4";

}

// ═════════════════════════════════════════════════════════════════════════════
// SpeedRuleViewModel — row in the time-based speed rules list.
// ═════════════════════════════════════════════════════════════════════════════

public partial class SpeedRuleViewModel : ObservableObject
{
    public SpeedRule Rule { get; }

    [ObservableProperty] private string _from;
    [ObservableProperty] private string _to;
    [ObservableProperty] private double _limitKBps;

    partial void OnFromChanged(string v)      { if (TimeOnly.TryParse(v, out var t)) Rule.From = t; }
    partial void OnToChanged(string v)        { if (TimeOnly.TryParse(v, out var t)) Rule.To   = t; }
    partial void OnLimitKBpsChanged(double v) { Rule.LimitBytesPerSecond = (long)(v * 1024); }

    public SpeedRuleViewModel(SpeedRule rule)
    {
        Rule      = rule;
        _from     = rule.From.ToString("HH:mm");
        _to       = rule.To.ToString("HH:mm");
        _limitKBps = rule.LimitBytesPerSecond / 1024.0;
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// CategorizationRuleViewModel — row in the file-categorization rules list.
// ═════════════════════════════════════════════════════════════════════════════

public partial class CategorizationRuleViewModel : ObservableObject
{
    private readonly Action _onChanged;

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _extensionsText;
    [ObservableProperty] private string _targetFolder;
    [ObservableProperty] private bool   _enabled;

    public CategorizationRuleViewModel(CategorizationRule rule, Action onChanged)
    {
        _onChanged      = onChanged;
        _name           = rule.CategoryName;
        _extensionsText = string.Join(", ", rule.Extensions);
        _targetFolder   = rule.TargetFolder;
        _enabled        = rule.Enabled;
    }

    partial void OnNameChanged(string v)           => _onChanged();
    partial void OnExtensionsTextChanged(string v) => _onChanged();
    partial void OnTargetFolderChanged(string v)   => _onChanged();
    partial void OnEnabledChanged(bool v)          => _onChanged();

    public CategorizationRule ToRule() => new()
    {
        CategoryName = Name,
        TargetFolder = TargetFolder,
        Extensions   = [.. ExtensionsText
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.TrimStart('.').ToLowerInvariant())
            .Where(e => e.Length > 0)],
        Enabled = Enabled,
    };
}
