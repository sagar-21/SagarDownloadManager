using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DM.App.Services;
using DM.App.Views.Dialogs;
using DM.Core.Models;
using DM.Core.Queue;
using DM.Core.Settings;
using Wpf.Ui.Appearance;

namespace DM.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AppSettingsService      _settingsService;
    private readonly DownloadQueueManager    _queueManager;
    private readonly Dictionary<Guid, DownloadItemViewModel> _entryIndex = [];

    // Polls active download entries every 200 ms so the UI always reflects the
    // latest bytes/speed even if an EntryChanged event is missed or delayed.
    private readonly DispatcherTimer _pollTimer;

    public DownloadQueueManager    QueueManager      => _queueManager;
    public AppSettingsService      SettingsService   => _settingsService;
    public SettingsViewModel       SettingsVm        { get; }
    public ClipboardMonitorService ClipboardMonitor  { get; } = new();
    public LocalConnectorServer    Connector         { get; } = new();

    [ObservableProperty] private bool   _isLoading   = true;
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private bool   _isDarkTheme = true;

    public ObservableCollection<DownloadItemViewModel> Downloads { get; } = new();
    public DownloadQueueViewModel QueueVm   { get; }
    public HistoryViewModel       HistoryVm { get; }

    // Fired on a thread-pool thread when the extension requests a download WITHOUT a quality.
    // MainWindow subscribes and dispatches to UI to show AddDownloadDialog.
    public event Action<string>? BrowserDownloadRequested;

    public bool HasDownloads => !IsLoading && Downloads.Count > 0;
    public bool IsEmpty      => !IsLoading && Downloads.Count == 0;

    partial void OnIsLoadingChanged(bool value) => RefreshVisibility();

    private void RefreshVisibility()
    {
        OnPropertyChanged(nameof(HasDownloads));
        OnPropertyChanged(nameof(IsEmpty));
    }

    public MainViewModel()
    {
        _settingsService = new AppSettingsService();
        _settingsService.Load();

        _isDarkTheme  = _settingsService.Current.Theme != AppTheme.Light;
        _queueManager = new DownloadQueueManager(_settingsService);
        SettingsVm    = new SettingsViewModel(_settingsService);
        QueueVm       = new DownloadQueueViewModel(_queueManager);
        HistoryVm     = new HistoryViewModel(_queueManager);

        ApplicationThemeManager.Apply(
            _isDarkTheme ? ApplicationTheme.Dark : ApplicationTheme.Light);

        Connector.DownloadRequested += req =>
        {
            if (!string.IsNullOrWhiteSpace(req.Quality))
                EnqueueFromExtension(req.Url, req.Quality);   // has quality → direct enqueue, no dialog
            else
                BrowserDownloadRequested?.Invoke(req.Url);    // no quality → show dialog
        };
        StartConnectorIfEnabled(_settingsService.Current);

        _settingsService.Changed += s =>
        {
            var dark = s.Theme != AppTheme.Light;
            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (dark == _isDarkTheme) return;
                IsDarkTheme = dark;
                ApplicationThemeManager.Apply(dark ? ApplicationTheme.Dark : ApplicationTheme.Light);
            });

            var needRunning = s.LocalServerEnabled;
            var portChanged = s.LocalServerPort != Connector.Port;
            if (needRunning != Connector.IsRunning || (needRunning && portChanged))
            {
                Connector.Stop();
                if (needRunning) StartConnectorIfEnabled(s);
            }
        };

        _queueManager.EntryAdded   += OnEntryAdded;
        _queueManager.EntryChanged += OnEntryChanged;
        _queueManager.EntryRemoved += OnEntryRemoved;

        _queueManager.RestoreQueue();

        Downloads.CollectionChanged += (_, _) => RefreshVisibility();

        // 200 ms poll — safety net to keep progress bar / speed / buttons in sync
        // regardless of whether event-based updates arrive on time.
        _pollTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(200),
            DispatcherPriority.Render,
            PollDownloadProgress,
            Application.Current.Dispatcher);

        Task.Delay(1800).ContinueWith(_ =>
            Application.Current?.Dispatcher.Invoke(() => IsLoading = false));
    }

    // ── Polling (200 ms) ───────────────────────────────────────────────────

    private void PollDownloadProgress(object? sender, EventArgs e)
    {
        foreach (var vm in _entryIndex.Values)
        {
            var entry = vm.Entry;
            if (entry is null) continue;

            // Read the entry fields directly (written by background download thread).
            // long/double/enum are read atomically on 64-bit; safe for monitoring.
            var status = entry.Status;
            var total  = entry.TotalBytes;
            var done   = entry.DownloadedBytes;
            var speed  = entry.SpeedBytesPerSecond;
            var error  = entry.ErrorMessage;

            vm.Status              = status;
            vm.TotalBytes          = total;
            vm.DownloadedBytes     = done;
            vm.SpeedBytesPerSecond = speed;
            if (!string.IsNullOrEmpty(error)) vm.ErrorMessage = error;

            // When download completes, update filename to the real file yt-dlp created
            if (status == DownloadStatus.Completed
                && !string.IsNullOrEmpty(entry.DestinationPath))
            {
                var fn = Path.GetFileName(entry.DestinationPath);
                if (!string.IsNullOrEmpty(fn) && fn != "video")
                {
                    vm.FileName        = fn;
                    vm.DestinationPath = entry.DestinationPath;
                }
            }
        }
    }

    // ── Queue event handlers ────────────────────────────────────────────────

    private void OnEntryAdded(QueueEntry entry)
    {
        // Called from UI thread (Enqueue is always called from UI commands).
        // Dispatcher.Invoke handles the case where it could be called from another thread.
        void Add()
        {
            if (_entryIndex.ContainsKey(entry.Id)) return;
            var vm = new DownloadItemViewModel(_queueManager, entry)
            {
                FileName        = Path.GetFileName(entry.DestinationPath),
                SourceUrl       = entry.Url,
                DestinationPath = entry.DestinationPath,
            };
            SyncFromEntry(vm, entry);
            _entryIndex[entry.Id] = vm;
            Downloads.Add(vm);
        }

        if (Application.Current?.Dispatcher.CheckAccess() == true) Add();
        else Application.Current?.Dispatcher.Invoke(Add);
    }

    private void OnEntryChanged(QueueEntry entry)
    {
        // Snapshot all mutable fields immediately — the entry is mutated on background
        // threads, so a lazy read inside InvokeAsync could race against completion.
        var status     = entry.Status;
        var total      = entry.TotalBytes;
        var downloaded = entry.DownloadedBytes;
        var speed      = entry.SpeedBytesPerSecond;
        var error      = entry.ErrorMessage;
        var priority   = entry.Priority;
        var id         = entry.Id;
        var dest       = entry.DestinationPath;

        void Apply()
        {
            if (!_entryIndex.TryGetValue(id, out var vm)) return;
            vm.Status              = status;
            vm.TotalBytes          = total;
            vm.DownloadedBytes     = downloaded;
            vm.SpeedBytesPerSecond = speed;
            vm.ErrorMessage        = error ?? "";
            vm.Priority            = priority;
            if (!string.IsNullOrEmpty(dest))
            {
                vm.DestinationPath = dest;
                var fn = Path.GetFileName(dest);
                if (!string.IsNullOrEmpty(fn) && fn != "video") vm.FileName = fn;
            }
        }

        // Apply directly if already on UI thread; otherwise marshal asynchronously.
        if (Application.Current?.Dispatcher.CheckAccess() == true)
            Apply();
        else
            Application.Current?.Dispatcher.InvokeAsync(Apply, DispatcherPriority.Render);
    }

    private void OnEntryRemoved(Guid id)
    {
        void Remove()
        {
            if (!_entryIndex.Remove(id, out var vm)) return;
            Downloads.Remove(vm);
        }

        if (Application.Current?.Dispatcher.CheckAccess() == true) Remove();
        else Application.Current?.Dispatcher.Invoke(Remove);
    }

    private static void SyncFromEntry(DownloadItemViewModel vm, QueueEntry e)
    {
        vm.Status              = e.Status;
        vm.TotalBytes          = e.TotalBytes;
        vm.DownloadedBytes     = e.DownloadedBytes;
        vm.SpeedBytesPerSecond = e.SpeedBytesPerSecond;
        vm.ErrorMessage        = e.ErrorMessage ?? "";
        vm.Priority            = e.Priority;
    }

    public void EnqueueUrl(string url)
    {
        var folder = _settingsService.Current.DefaultDownloadFolder;
        string fileName;
        try
        {
            fileName = Path.GetFileName(new Uri(url).AbsolutePath);
            if (string.IsNullOrWhiteSpace(fileName) || !fileName.Contains('.'))
                fileName = "download";
        }
        catch { fileName = "download"; }

        _queueManager.Enqueue(new QueueEntry
        {
            Url             = url,
            DestinationPath = Path.Combine(folder, fileName),
        });
    }

    // Called when the browser extension sends a URL + quality preset.
    // Maps the quality label to a yt-dlp format string and enqueues directly.
    public void EnqueueFromExtension(string url, string quality)
    {
        var folder   = _settingsService.Current.DefaultDownloadFolder;
        var formatId = QualityToFormatId(quality);

        _queueManager.Enqueue(new QueueEntry
        {
            Url             = url,
            DestinationPath = Path.Combine(folder, "video"),
            VideoFormatId   = formatId,
        });
    }

    private static string QualityToFormatId(string quality) =>
        quality.ToLowerInvariant() switch
        {
            // Format: bestvideo[height<=N]+bestaudio  — separate streams merged by ffmpeg (YouTube standard)
            //         /best[height<=N]                — fallback: best combined stream still within limit
            //         /best                           — last resort: any best (rarely needed)
            "144p"          => "bestvideo[height<=144]+bestaudio/best[height<=144]/best",
            "240p"          => "bestvideo[height<=240]+bestaudio/best[height<=240]/best",
            "360p"          => "bestvideo[height<=360]+bestaudio/best[height<=360]/best",
            "480p"          => "bestvideo[height<=480]+bestaudio/best[height<=480]/best",
            "720p"          => "bestvideo[height<=720]+bestaudio/best[height<=720]/best",
            "1080p"         => "bestvideo[height<=1080]+bestaudio/best[height<=1080]/best",
            "1440p"         => "bestvideo[height<=1440]+bestaudio/best[height<=1440]/best",
            "2160p" or "4k" => "bestvideo[height<=2160]+bestaudio/best[height<=2160]/best",
            "mp3"           => DM.Core.VideoStreaming.YtDlpWrapper.Mp3FormatId,
            _               => "bestvideo+bestaudio/best",   // "best" or unknown
        };

    private void StartConnectorIfEnabled(DM.Core.Settings.AppSettings s)
    {
        if (!s.LocalServerEnabled) return;
        try { Connector.Start(s.LocalServerPort); }
        catch { /* port in use — silently skip */ }
    }

    public void Dispose()
    {
        _pollTimer.Stop();
        _queueManager.Dispose();
        Connector.Dispose();
    }

    [RelayCommand]
    private Task AddDownload()
    {
        var dialog = new AddDownloadDialog(_settingsService) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return Task.CompletedTask;

        foreach (var entry in dialog.ResultEntries)
            _queueManager.Enqueue(entry);
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task BatchAdd()
    {
        var dialog = new BatchAddDialog { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return Task.CompletedTask;

        foreach (var (url, dest) in dialog.Entries)
        {
            _queueManager.Enqueue(new QueueEntry
            {
                Url             = url,
                DestinationPath = dest,
                PostAction      = dialog.PostAction,
            });
        }
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        IsDarkTheme = !IsDarkTheme;
        ApplicationThemeManager.Apply(
            IsDarkTheme ? ApplicationTheme.Dark : ApplicationTheme.Light);
        SettingsVm.ThemeIndex = IsDarkTheme ? 0 : 1;
    }
}
