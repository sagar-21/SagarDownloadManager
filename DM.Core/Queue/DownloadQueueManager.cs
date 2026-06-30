using System.Text.Json;
using DM.Core.Downloading;
using DM.Core.History;
using DM.Core.Models;
using DM.Core.PostDownload;
using DM.Core.RateLimiting;
using DM.Core.Settings;
using DM.Core.VideoStreaming;

namespace DM.Core.Queue;

/// <summary>
/// Central download orchestrator. Manages concurrency, scheduling, rate limiting,
/// post-download processing (categorization + checksum), and history recording.
///
/// Two construction paths:
///   • new DownloadQueueManager(AppSettingsService) — production; settings driven
///     entirely by the service, which handles persistence.
///   • new DownloadQueueManager() — tests / legacy; loads own QueueSettings from
///     queueSettings.json and supports ApplySettings() for direct control.
/// </summary>
public sealed class DownloadQueueManager : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
        { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly string LegacySettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DownloadManager", "queueSettings.json");

    private static readonly string QueueStatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DownloadManager", "queue.json");

    private static readonly string[] VideoHosts =
    [
        "youtube.com", "youtu.be", "vimeo.com", "dailymotion.com",
        "twitch.tv", "tiktok.com", "twitter.com", "x.com",
        "instagram.com", "facebook.com", "reddit.com",
        "soundcloud.com", "bandcamp.com", "bilibili.com",
        "nicovideo.jp", "streamable.com", "rumble.com", "odysee.com",
        "mixcloud.com", "pinterest.com", "linkedin.com",
        "tumblr.com", "imgur.com", "gfycat.com",
    ];

    private static bool IsVideoUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.ToLowerInvariant();
        return Array.Exists(VideoHosts, h => host == h || host.EndsWith("." + h));
    }

    // Invokes callback synchronously on whatever thread calls Report() — no
    // SynchronizationContext marshaling.  This keeps progress events on the
    // background stderr-reader thread, not the UI thread.
    private sealed class DirectProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private readonly object                  _lock         = new();
    private readonly List<QueueEntry>        _entries      = [];
    private readonly TokenBucketRateLimiter  _globalBucket = new();
    private readonly HttpClient              _http         = Downloader.CreateOptimizedHttpClient();
    private readonly CancellationTokenSource _cts = new();
    private VideoDownloadService _videoService   = new();

    private readonly AppSettingsService? _settingsService;

    private QueueSettings _settings        = new();
    private int           _activeCount;
    private bool          _schedulerAllows  = true;
    private bool          _queueDrainFired;

    // ── Public state ─────────────────────────────────────────────────────────

    public DownloadHistory History  { get; } = new();
    public QueueSettings   Settings => _settings;

    // ── Events ───────────────────────────────────────────────────────────────

    public event Action<QueueEntry>?         EntryAdded;
    public event Action<QueueEntry>?         EntryChanged;
    public event Action<Guid>?               EntryRemoved;
    public event Action<QueueEntry, string>? DownloadCompleted;
    public event Action?                     QueueCompleted;

    // ── Construction ─────────────────────────────────────────────────────────

    /// <summary>Production constructor. Settings are owned by <paramref name="settingsService"/>.</summary>
    public DownloadQueueManager(AppSettingsService settingsService)
    {
        _settingsService = settingsService;
        ApplyFromAppSettings(settingsService.Current);
        settingsService.Changed += s =>
        {
            ApplyFromAppSettings(s);
            TryStartNext();
        };
        _ = RunSchedulerAsync(_cts.Token);
    }

    /// <summary>Legacy / test constructor. Loads from queueSettings.json; supports ApplySettings().</summary>
    public DownloadQueueManager()
    {
        LoadLegacySettings();
        _ = RunSchedulerAsync(_cts.Token);
    }

    // ── Settings ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps <see cref="AppSettings"/> onto the internal <see cref="QueueSettings"/>.
    /// Does not persist (the service owns persistence).
    /// </summary>
    private void ApplyFromAppSettings(AppSettings s)
    {
        lock (_lock)
        {
            _settings = new QueueSettings
            {
                MaxConcurrentDownloads             = s.MaxConcurrentDownloads,
                GlobalSpeedLimitBytesPerSecond     = s.GlobalSpeedLimitBytesPerSecond,
                DefaultPerDownloadLimitBytesPerSec = s.PerDownloadSpeedLimitBytesPerSecond,
                Categorization = s.Categorization,
                Actions        = s.PostDownload,
                Scheduler      = s.BandwidthSchedule,
                SpeedRules     = s.SpeedRules,
            };
            _globalBucket.LimitBytesPerSecond = ComputeEffectiveLimitNow();
        }

        // Rebuild VideoDownloadService if the tools directory changed.
        var toolsDir = string.IsNullOrWhiteSpace(s.ToolsDirectory) ? "" : s.ToolsDirectory;
        var ytdlpSettings = string.IsNullOrWhiteSpace(toolsDir)
            ? new YtDlpSettings()
            : new YtDlpSettings { ToolsDirectory = toolsDir };
        _videoService = new VideoDownloadService(ytdlpSettings);
    }

    /// <summary>Direct settings update — used by the legacy constructor path and tests.</summary>
    public void ApplySettings(QueueSettings settings)
    {
        lock (_lock)
        {
            _settings = settings;
            _globalBucket.LimitBytesPerSecond = ComputeEffectiveLimitNow();
        }
        if (_settingsService is null)
            _ = PersistLegacySettingsAsync(settings);
        TryStartNext();
    }

    // ── Queue API ────────────────────────────────────────────────────────────

    public void Enqueue(QueueEntry entry)
    {
        lock (_lock)
        {
            int max = _entries
                .Where(e => e.Priority == entry.Priority)
                .Select(e => e.SortIndex)
                .DefaultIfEmpty(-1)
                .Max();
            entry.SortIndex  = max + 1;
            _queueDrainFired = false;
            _entries.Add(entry);
        }
        EntryAdded?.Invoke(entry);
        TryStartNext();
        _ = SaveQueueAsync();
    }

    public void PauseEntry(QueueEntry entry)
    {
        entry.Status              = DownloadStatus.Paused;
        entry.SpeedBytesPerSecond = 0;
        entry.ActiveCts?.Cancel();
        EntryChanged?.Invoke(entry);
        _ = SaveQueueAsync();
    }

    public void CancelEntry(QueueEntry entry)
    {
        entry.Status              = DownloadStatus.Cancelled;
        entry.SpeedBytesPerSecond = 0;
        entry.ActiveCts?.Cancel();
        lock (_lock) { _entries.Remove(entry); }
        try { Downloader.DeleteState(entry.DestinationPath); } catch { }
        EntryRemoved?.Invoke(entry.Id);
        _ = SaveQueueAsync();
        CheckQueueDrained();
    }

    public void RequeueEntry(QueueEntry entry)
    {
        entry.Status              = DownloadStatus.Queued;
        entry.ErrorMessage        = null;
        entry.SpeedBytesPerSecond = 0;
        lock (_lock)
        {
            if (!_entries.Contains(entry))
            {
                _queueDrainFired = false;
                _entries.Add(entry);
            }
        }
        EntryChanged?.Invoke(entry);
        TryStartNext();
        _ = SaveQueueAsync();
    }

    public void SetPriority(QueueEntry entry, DownloadPriority priority)
    {
        lock (_lock)
        {
            entry.Priority = priority;
            int max = _entries
                .Where(e => e.Priority == priority && e.Id != entry.Id)
                .Select(e => e.SortIndex)
                .DefaultIfEmpty(-1)
                .Max();
            entry.SortIndex = max + 1;
        }
        EntryChanged?.Invoke(entry);
    }

    public void MoveEntry(QueueEntry entry, int targetIndex)
    {
        lock (_lock)
        {
            var ordered = Sorted();
            int cur = ordered.FindIndex(e => e.Id == entry.Id);
            if (cur < 0) return;
            ordered.RemoveAt(cur);
            targetIndex = Math.Clamp(targetIndex, 0, ordered.Count);
            ordered.Insert(targetIndex, entry);
            for (int i = 0; i < ordered.Count; i++)
                ordered[i].SortIndex = i;
        }
    }

    public void PauseAll()
    {
        List<QueueEntry> active;
        lock (_lock) { active = _entries.Where(e => e.Status == DownloadStatus.Downloading).ToList(); }
        foreach (var e in active) PauseEntry(e);
    }

    public QueueEntry[] GetAll()
    {
        lock (_lock) { return [.. Sorted()]; }
    }

    // ── Concurrency gate ─────────────────────────────────────────────────────

    private void TryStartNext()
    {
        while (true)
        {
            QueueEntry? next;
            lock (_lock)
            {
                if (!_schedulerAllows) return;
                if (_activeCount >= _settings.MaxConcurrentDownloads) return;
                next = Sorted().FirstOrDefault(e => e.Status == DownloadStatus.Queued);
                if (next is null) return;
                _activeCount++;
                next.Status    = DownloadStatus.Downloading;
                next.ActiveCts = new CancellationTokenSource();
            }
            EntryChanged?.Invoke(next);
            _ = RunEntryAsync(next);
        }
    }

    private async Task RunEntryAsync(QueueEntry entry)
    {
        var cts = entry.ActiveCts!;

        // Resolve effective per-download speed limit (override beats global)
        long perLimit;
        lock (_lock)
        {
            perLimit = entry.Overrides?.SpeedLimitBytesPerSecond
                ?? _settings.DefaultPerDownloadLimitBytesPerSec;
        }
        TokenBucketRateLimiter? perBucket = perLimit > 0
            ? new TokenBucketRateLimiter { LimitBytesPerSecond = perLimit }
            : null;

        // DirectProgress fires on the background stderr-reader thread.
        // MainViewModel.OnEntryChanged then marshals the snapshot to the UI thread.
        var progress = new DirectProgress<DownloadProgress>(p =>
        {
            entry.DownloadedBytes     = p.BytesReceived;
            if (p.TotalBytes > 0) entry.TotalBytes = p.TotalBytes;
            entry.SpeedBytesPerSecond = p.SpeedBytesPerSecond;
            EntryChanged?.Invoke(entry);
        });

        try
        {
            var folder = Path.GetDirectoryName(entry.DestinationPath)
                         ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Downloads";
            Directory.CreateDirectory(folder);

            // Try yt-dlp for recognised streaming/social sites; fall through to HTTP on
            // UnsupportedSite so arbitrary direct-file URLs still download correctly.
            var useHttp = !IsVideoUrl(entry.Url);
            if (!useHttp)
            {
                try
                {
                    // yt-dlp output template: title + extension determined at runtime
                    var template = Path.Combine(folder, "%(title)s.%(ext)s");

                    // Snapshot folder before download to detect the new file afterward
                    var before = Directory.Exists(folder)
                        ? Directory.GetFiles(folder).ToHashSet(StringComparer.OrdinalIgnoreCase)
                        : [];

                    var formatId        = entry.VideoFormatId ?? VideoFormat.BestQuality.Id;
                    var cookiesFilePath = _settingsService?.Current.CookiesFilePath ?? "";
                    var playerClient    = _settingsService?.Current.YtdlpPlayerClient ?? "android_vr,tv_embedded,ios";
                    var geoBypass       = _settingsService?.Current.GeoBypass ?? false;
                    await _videoService.DownloadAsync(
                        entry.Url, formatId, template, progress, cts.Token,
                        speedLimitBytesPerSec: perLimit,
                        cookiesFilePath: cookiesFilePath,
                        playerClient: playerClient,
                        geoBypass: geoBypass);

                    // Find the file yt-dlp created
                    var after   = Directory.GetFiles(folder);
                    var newFile = after.FirstOrDefault(f => !before.Contains(f)) ?? folder;

                    entry.Status              = DownloadStatus.Completed;
                    entry.DestinationPath     = newFile;
                    entry.SpeedBytesPerSecond = 0;
                    if (entry.TotalBytes > 0) entry.DownloadedBytes = entry.TotalBytes;

                    if (File.Exists(newFile))
                    {
                        var info = new FileInfo(newFile);
                        if (entry.TotalBytes <= 0) entry.TotalBytes = info.Length;
                        entry.DownloadedBytes = entry.TotalBytes;
                    }

                    History.Add(new HistoryEntry
                    {
                        Url             = entry.Url,
                        FileName        = Path.GetFileName(newFile),
                        DestinationPath = newFile,
                        FileSizeBytes   = entry.TotalBytes,
                    });

                    DownloadCompleted?.Invoke(entry, newFile);
                }
                catch (VideoDownloadException ex)
                    when (ex.Reason == VideoDownloadFailureReason.UnsupportedSite)
                {
                    // Site not recognised by yt-dlp — retry as a plain HTTP download
                    useHttp = true;
                }
            }

            if (useHttp)
            {
                var engineSettings = BuildEngineSettings(entry);
                var downloader     = new Downloader(_http, engineSettings, _globalBucket, perBucket);

                await Task.Run(() =>
                    downloader.DownloadAsync(entry.Url, entry.DestinationPath, progress, cts.Token),
                    cts.Token);

                // Post-processing: categorize + verify checksum
                CategorizationSettings categorization;
                lock (_lock) { categorization = _settings.Categorization; }

                var postResult = await PostDownloadProcessor.ProcessAsync(
                    entry.DestinationPath, categorization, entry.ExpectedChecksum, cts.Token);

                entry.Status              = DownloadStatus.Completed;
                entry.DestinationPath     = postResult.FinalPath;
                entry.ChecksumSummary     = postResult.ChecksumSummary;
                entry.ChecksumOk          = postResult.ChecksumOk;
                entry.SpeedBytesPerSecond = 0;
                if (entry.TotalBytes > 0) entry.DownloadedBytes = entry.TotalBytes;
                try { Downloader.DeleteState(postResult.FinalPath); } catch { }

                History.Add(new HistoryEntry
                {
                    Url             = entry.Url,
                    FileName        = Path.GetFileName(postResult.FinalPath),
                    DestinationPath = postResult.FinalPath,
                    FileSizeBytes   = entry.TotalBytes,
                    ChecksumSummary = postResult.ChecksumSummary,
                    ChecksumOk      = postResult.ChecksumOk,
                    WasCategorized  = postResult.FinalPath != entry.DestinationPath,
                });

                DownloadCompleted?.Invoke(entry, postResult.FinalPath);
            }
        }
        catch (OperationCanceledException)
        {
            if (entry.Status != DownloadStatus.Paused)
                entry.Status = DownloadStatus.Cancelled;
            entry.SpeedBytesPerSecond = 0;
        }
        catch (Exception ex)
        {
            entry.Status              = DownloadStatus.Failed;
            entry.ErrorMessage        = ex.Message;
            entry.SpeedBytesPerSecond = 0;
        }
        finally
        {
            entry.ActiveCts = null;
            lock (_lock) { _activeCount = Math.Max(0, _activeCount - 1); }
            EntryChanged?.Invoke(entry);
            _ = SaveQueueAsync();
            TryStartNext();
            CheckQueueDrained();
        }
    }

    /// <summary>
    /// Builds <see cref="EngineSettings"/> for a single download entry, applying
    /// any per-download overrides on top of the global settings.
    /// </summary>
    private EngineSettings BuildEngineSettings(QueueEntry entry)
    {
        var app = _settingsService?.Current ?? new AppSettings();

        int  maxConns;
        bool autoTune;

        if (entry.Overrides?.ConnectionCount is int overrideCnt)
        {
            maxConns = overrideCnt;
            autoTune = false;
        }
        else if (app.ConnectionMode == ConnectionMode.Auto)
        {
            maxConns = app.AutoMaxConnections;
            autoTune = true;
        }
        else
        {
            maxConns = app.ManualConnectionCount;
            autoTune = false;
        }

        int initial = app.ConnectionMode == ConnectionMode.Auto && entry.Overrides?.ConnectionCount is null
            ? Math.Clamp(app.AutoMinConnections, 1, maxConns)
            : maxConns;

        return new EngineSettings
        {
            MaxConnectionsPerFile     = Math.Clamp(maxConns, 1, 64),
            InitialConnectionsPerFile = initial,
            AutoTuneConnections       = autoTune,
            RetryCount                = app.RetryPolicy.MaxRetries,
            RetryDelay                = TimeSpan.FromSeconds(app.RetryPolicy.InitialDelaySecs),
            RetryDelayMax             = TimeSpan.FromSeconds(app.RetryPolicy.MaxDelaySecs),
        };
    }

    private void CheckQueueDrained()
    {
        bool drained;
        lock (_lock)
        {
            if (_queueDrainFired || _entries.Count == 0) return;
            drained = _activeCount == 0
                && _entries.All(e => e.Status is DownloadStatus.Completed
                                               or DownloadStatus.Failed
                                               or DownloadStatus.Cancelled);
            if (drained) _queueDrainFired = true;
        }
        if (drained) QueueCompleted?.Invoke();
    }

    // ── Scheduler ────────────────────────────────────────────────────────────

    private async Task RunSchedulerAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                bool allows, changed;
                lock (_lock)
                {
                    _globalBucket.LimitBytesPerSecond = ComputeEffectiveLimitNow();
                    allows  = !_settings.Scheduler.Enabled || IsInScheduleWindow();
                    changed = allows != _schedulerAllows;
                    _schedulerAllows = allows;
                }
                if (!changed) continue;
                if (allows) TryStartNext();
                else        PauseAll();
            }
        }
        catch (OperationCanceledException) { }
    }

    private bool IsInScheduleWindow()
    {
        var s   = _settings.Scheduler;
        var now = DateTime.Now;
        var t   = TimeOnly.FromDateTime(now);
        bool dayOk = s.Recurrence != RecurrenceMode.Weekly || s.ActiveDays.Contains(now.DayOfWeek);
        if (!dayOk) return false;
        return s.StartTime < s.StopTime
            ? t >= s.StartTime && t < s.StopTime
            : t >= s.StartTime || t < s.StopTime;
    }

    private long ComputeEffectiveLimitNow()
    {
        var now = TimeOnly.FromDateTime(DateTime.Now);
        var day = DateTime.Now.DayOfWeek;
        foreach (var rule in _settings.SpeedRules)
        {
            if (!rule.ActiveDays.Contains(day)) continue;
            bool inWindow = rule.From < rule.To
                ? now >= rule.From && now < rule.To
                : now >= rule.From || now < rule.To;
            if (inWindow) return rule.LimitBytesPerSecond;
        }
        return _settings.GlobalSpeedLimitBytesPerSecond;
    }

    private List<QueueEntry> Sorted() =>
        [.. _entries.OrderByDescending(e => (int)e.Priority).ThenBy(e => e.SortIndex)];

    // ── Legacy persistence (parameterless constructor only) ───────────────────

    private void LoadLegacySettings()
    {
        try
        {
            if (!File.Exists(LegacySettingsPath)) return;
            var s = JsonSerializer.Deserialize<QueueSettings>(
                File.ReadAllText(LegacySettingsPath), JsonOpts);
            if (s is null) return;
            _settings = s;
            _globalBucket.LimitBytesPerSecond = s.GlobalSpeedLimitBytesPerSecond;
        }
        catch { }
    }

    private static async Task PersistLegacySettingsAsync(QueueSettings s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LegacySettingsPath)!);
            await File.WriteAllTextAsync(LegacySettingsPath, JsonSerializer.Serialize(s, JsonOpts));
        }
        catch { }
    }

    // ── Queue entry persistence ───────────────────────────────────────────────

    /// <summary>
    /// Restores persisted queue entries. Call AFTER subscribing to <see cref="EntryAdded"/>.
    /// </summary>
    public void RestoreQueue() => LoadQueue();

    private void LoadQueue()
    {
        try
        {
            if (!File.Exists(QueueStatePath)) return;
            var dtos = JsonSerializer.Deserialize<List<PersistedQueueEntry>>(
                File.ReadAllText(QueueStatePath), JsonOpts);
            if (dtos is null) return;

            foreach (var dto in dtos)
            {
                var status = dto.Status == DownloadStatus.Downloading
                    ? DownloadStatus.Paused
                    : dto.Status;

                var entry = new QueueEntry
                {
                    Url              = dto.Url,
                    DestinationPath  = dto.DestinationPath,
                    Priority         = dto.Priority,
                    SortIndex        = dto.SortIndex,
                    Status           = status,
                    TotalBytes       = dto.TotalBytes,
                    DownloadedBytes  = dto.DownloadedBytes,
                    ErrorMessage     = dto.ErrorMessage,
                    PostAction       = dto.PostAction,
                    ExpectedChecksum = dto.ExpectedChecksum,
                    VideoFormatId    = dto.VideoFormatId,
                    Overrides        = dto.Overrides,
                };
                _entries.Add(entry);
                EntryAdded?.Invoke(entry);
            }
        }
        catch { }
    }

    private async Task SaveQueueAsync()
    {
        try
        {
            List<QueueEntry> snapshot;
            lock (_lock) { snapshot = [.. _entries]; }

            var dtos = snapshot
                .Where(e => e.Status is not (DownloadStatus.Completed or DownloadStatus.Cancelled))
                .Select(e => new PersistedQueueEntry(
                    e.Id,
                    e.Url,
                    e.DestinationPath,
                    e.Priority,
                    e.SortIndex,
                    e.Status == DownloadStatus.Downloading ? DownloadStatus.Paused : e.Status,
                    e.TotalBytes,
                    e.DownloadedBytes,
                    e.ErrorMessage,
                    e.PostAction,
                    e.ExpectedChecksum,
                    e.VideoFormatId,
                    e.Overrides))
                .ToList();

            Directory.CreateDirectory(Path.GetDirectoryName(QueueStatePath)!);
            await File.WriteAllTextAsync(QueueStatePath,
                JsonSerializer.Serialize(dtos, JsonOpts));
        }
        catch { }
    }

    private sealed record PersistedQueueEntry(
        Guid               Id,
        string             Url,
        string             DestinationPath,
        DownloadPriority   Priority,
        int                SortIndex,
        DownloadStatus     Status,
        long               TotalBytes,
        long               DownloadedBytes,
        string?            ErrorMessage,
        PerDownloadAction  PostAction,
        string?            ExpectedChecksum,
        string?            VideoFormatId,
        DownloadOverrides? Overrides);

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
        _cts.Dispose();
    }
}
