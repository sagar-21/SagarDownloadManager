using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DM.Core.PostDownload;
using DM.Core.Queue;
using DM.Core.Settings;
using DM.Core.VideoStreaming;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace DM.App.Views.Dialogs;

public partial class AddDownloadDialog : FluentWindow
{
    // Streaming/social sites that yt-dlp can handle
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

    // Direct-file extensions — skip yt-dlp probe for these
    private static readonly string[] DirectFileExtensions =
    [
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".ts",
        ".mp3", ".flac", ".wav", ".ogg", ".m4a", ".aac", ".opus",
        ".zip", ".7z", ".rar", ".exe", ".msi", ".apk", ".dmg",
        ".pdf", ".epub", ".iso", ".torrent", ".tar", ".gz", ".bz2",
    ];

    private static bool IsVideoUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.ToLowerInvariant();
        return Array.Exists(VideoHosts, h => host == h || host.EndsWith("." + h));
    }

    private static bool IsDirectFileUrl(string url)
    {
        try
        {
            var path = new Uri(url).AbsolutePath.ToLowerInvariant();
            return DirectFileExtensions.Any(ext => path.EndsWith(ext, StringComparison.Ordinal));
        }
        catch { return false; }
    }

    private static readonly PerDownloadAction[] ActionMap =
    [
        PerDownloadAction.None,
        PerDownloadAction.ShowNotification,
        PerDownloadAction.OpenFile,
        PerDownloadAction.OpenFolder,
        PerDownloadAction.VerifyChecksum,
    ];

    // Legacy single-entry properties (populated from ResultEntries[0] for single downloads)
    public string?           Url              { get; private set; }
    public string?           DestinationPath  { get; private set; }
    public PerDownloadAction PostAction       { get; private set; }
    public string?           ExpectedChecksum { get; private set; }
    public string?           VideoFormatId    { get; private set; }

    /// <summary>All entries to enqueue — 1 for a regular download, N for a playlist selection.</summary>
    public IReadOnlyList<QueueEntry> ResultEntries { get; private set; } = [];

    private readonly VideoDownloadService _videoSvc    = new();
    private readonly AppSettingsService?  _settingsSvc;
    private CancellationTokenSource?      _fetchCts;
    private PlaylistOrVideo?              _probeResult;

    public AddDownloadDialog(AppSettingsService? svc = null, string? initialUrl = null)
    {
        InitializeComponent();
        _settingsSvc   = svc;
        FolderBox.Text = svc?.Current.DefaultDownloadFolder
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Downloads";
        LoadFallbackFormats();

        Loaded += (_, _) =>
        {
            if (!string.IsNullOrEmpty(initialUrl))
            {
                UrlBox.Text = initialUrl;
                UrlBox.CaretIndex = initialUrl.Length;
            }
            UrlBox.Focus();
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        _fetchCts?.Cancel();
        _fetchCts?.Dispose();
        base.OnClosed(e);
    }

    // ── URL probe ────────────────────────────────────────────────────────────

    private void UrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (VideoFormatPanel is null) return;

        var url = UrlBox.Text.Trim();

        _fetchCts?.Cancel();
        _fetchCts    = new CancellationTokenSource();
        _probeResult = null;

        VideoFormatPanel.Visibility = Visibility.Collapsed;
        PlaylistPanel.Visibility    = Visibility.Collapsed;

        if (!Uri.TryCreate(url, UriKind.Absolute, out _)) return;
        if (IsDirectFileUrl(url)) return;   // skip yt-dlp for direct file links
        if (!IsVideoUrl(url)) return;

        // Show a lightweight "detecting" state in the playlist panel while we probe
        PlaylistPanel.Visibility      = Visibility.Visible;
        PlaylistLoadingBar.Visibility = Visibility.Visible;
        PlaylistInfoText.Text         = "Detecting content…";

        _ = ProbeUrlAsync(url, _fetchCts.Token);
    }

    private async Task ProbeUrlAsync(string url, CancellationToken ct)
    {
        // Debounce — avoids firing yt-dlp on every keypress
        try   { await Task.Delay(700, ct); }
        catch (OperationCanceledException) { return; }

        try
        {
            var cookies      = _settingsSvc?.Current.CookiesFilePath ?? "";
            var playerClient = _settingsSvc?.Current.YtdlpPlayerClient ?? "android_vr,tv_embedded,ios";
            var geoBypass    = _settingsSvc?.Current.GeoBypass ?? false;

            _probeResult = await _videoSvc.GetPlaylistOrVideoAsync(url, ct, cookies, playerClient, geoBypass);
            if (ct.IsCancellationRequested) return;

            if (_probeResult.IsPlaylist)
            {
                PlaylistLoadingBar.Visibility = Visibility.Collapsed;
                PlaylistPanel.Visibility      = Visibility.Visible;
                VideoFormatPanel.Visibility   = Visibility.Collapsed;
                var name = string.IsNullOrEmpty(_probeResult.PlaylistTitle)
                    ? "Playlist" : _probeResult.PlaylistTitle;
                PlaylistInfoText.Text = $"▶  {_probeResult.Entries.Length} videos  ·  {name}";
            }
            else if (_probeResult.Video is not null)
            {
                PlaylistPanel.Visibility      = Visibility.Collapsed;
                VideoFormatPanel.Visibility   = Visibility.Visible;
                PopulateFromMetadata(_probeResult.Video);
                VideoTitleText.Text = string.IsNullOrEmpty(_probeResult.Video.Title)
                    ? "Format loaded" : _probeResult.Video.Title;
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            // Show fallback format panel so the user can still attempt the download
            PlaylistPanel.Visibility      = Visibility.Collapsed;
            VideoFormatPanel.Visibility   = Visibility.Visible;
            var firstLine = ex.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                              .FirstOrDefault() ?? ex.Message;
            VideoTitleText.Text = $"Could not fetch info — {firstLine}";
            LoadFallbackFormats();
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                PlaylistLoadingBar.Visibility = Visibility.Collapsed;
                FormatLoadingBar.Visibility   = Visibility.Collapsed;
                VideoFormatCombo.IsEnabled    = true;
            }
        }
    }

    // ── Format helpers ───────────────────────────────────────────────────────

    private void PopulateFromMetadata(VideoMetadata meta)
    {
        VideoFormatCombo.Items.Clear();

        AddItem("★  Best Quality (auto)", VideoFormat.BestQuality.Id);

        var combined = meta.Formats
            .Where(f => f.HasVideo && f.HasAudio && !f.HasDrm)
            .ToList();
        if (combined.Count > 0)
        {
            AddHeader("── Video + Audio ──");
            foreach (var f in combined)
                AddItem(BuildLabel(f), f.Id);
        }

        var videoOnly = meta.Formats
            .Where(f => f.HasVideo && !f.HasAudio && !f.HasDrm)
            .ToList();
        if (videoOnly.Count > 0)
        {
            AddHeader("── Video + Best Audio ──");
            foreach (var f in videoOnly)
                AddItem(BuildLabel(f) + " + best audio", $"{f.Id}+bestaudio/best");
        }

        var audioOnly = meta.Formats
            .Where(f => !f.HasVideo && f.HasAudio && !f.HasDrm)
            .ToList();
        if (audioOnly.Count > 0)
        {
            AddHeader("── Audio Only ──");
            foreach (var f in audioOnly)
                AddItem(BuildLabel(f), f.Id);
        }

        AddItem("♪  Audio only (MP3)", YtDlpWrapper.Mp3FormatId);

        VideoFormatCombo.SelectedIndex = 0;
    }

    private static string BuildLabel(VideoFormat f)
    {
        var parts = new List<string>(5);
        if (!f.HasVideo)                        parts.Add("Audio only");
        else if (f.Height > 0)                  parts.Add($"{f.Height}p");
        if (f.Fps > 0)                          parts.Add($"{f.Fps} fps");
        if (!string.IsNullOrEmpty(f.Extension)) parts.Add(f.Extension.ToUpperInvariant());
        if (!string.IsNullOrEmpty(f.VideoCodec) && f.HasVideo)
            parts.Add(f.VideoCodec.Split('.')[0]);
        if (f.Filesize.HasValue)                parts.Add($"~{f.Filesize.Value / 1_048_576} MB");
        return parts.Count > 0 ? string.Join(" · ", parts) : f.Id;
    }

    private void LoadFallbackFormats()
    {
        VideoFormatCombo.Items.Clear();
        AddItem("★  Best Quality (auto)",  "bestvideo+bestaudio/best");
        AddItem("1080p MP4",               "bestvideo[height<=1080][ext=mp4]+bestaudio[ext=m4a]/best[height<=1080]");
        AddItem("720p MP4",                "bestvideo[height<=720][ext=mp4]+bestaudio[ext=m4a]/best[height<=720]");
        AddItem("480p MP4",                "bestvideo[height<=480][ext=mp4]+bestaudio[ext=m4a]/best[height<=480]");
        AddItem("360p MP4",                "bestvideo[height<=360][ext=mp4]+bestaudio[ext=m4a]/best[height<=360]");
        AddItem("♪  Audio only (MP3)",     YtDlpWrapper.Mp3FormatId);
        VideoFormatCombo.SelectedIndex = 0;
    }

    private void AddItem(string label, string formatId)
        => VideoFormatCombo.Items.Add(new ComboBoxItem { Content = label, Tag = formatId });

    private void AddHeader(string text)
        => VideoFormatCombo.Items.Add(new ComboBoxItem
        {
            Content = text, IsEnabled = false, FontSize = 10, Opacity = 0.45,
        });

    // ── Other event handlers ─────────────────────────────────────────────────

    public DownloadOverrides? GetOverrides()
    {
        var ov = new DownloadOverrides();
        bool any = false;

        if (OverrideConnectionsCheck.IsChecked == true
            && int.TryParse(ConnectionCountBox.Text, out int cc) && cc > 0)
        {
            ov.ConnectionCount = cc;
            any = true;
        }
        if (OverrideSpeedCheck.IsChecked == true
            && double.TryParse(SpeedLimitBox.Text, out double sp) && sp >= 0)
        {
            ov.SpeedLimitBytesPerSecond = (long)(sp * 1024);
            any = true;
        }
        if (OverrideQualityCheck.IsChecked == true && VideoQualityCombo.SelectedIndex >= 0)
        {
            ov.VideoQuality = (VideoQualityPreset)VideoQualityCombo.SelectedIndex;
            any = true;
        }
        return any ? ov : null;
    }

    private void ActionBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ChecksumPanel is null) return;
        ChecksumPanel.Visibility = ActionBox.SelectedIndex == 4
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var initial = FolderBox.Text.Trim();
            if (!Directory.Exists(initial))
                initial = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                          + @"\Downloads";

            var dlg = new OpenFolderDialog
            {
                Title            = "Select destination folder",
                InitialDirectory = initial,
            };
            if (dlg.ShowDialog(this) == true && !string.IsNullOrEmpty(dlg.FolderName))
                FolderBox.Text = dlg.FolderName;
        }
        catch { /* dialog unavailable on this OS/version — silently ignore */ }
    }

    private void OverrideCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (ConnectionCountBox is null) return;
        if (sender == OverrideConnectionsCheck)
            ConnectionCountBox.IsEnabled = OverrideConnectionsCheck.IsChecked == true;
        else if (sender == OverrideSpeedCheck)
            SpeedLimitBox.IsEnabled = OverrideSpeedCheck.IsChecked == true;
        else if (sender == OverrideQualityCheck)
            VideoQualityCombo.IsEnabled = OverrideQualityCheck.IsChecked == true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)     => Commit();
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)  Commit();
        if (e.Key == Key.Escape) Close();
    }

    // ── Commit ───────────────────────────────────────────────────────────────

    private void Commit()
    {
        var url    = UrlBox.Text.Trim();
        var folder = FolderBox.Text.Trim();

        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
        { UrlBox.Focus(); return; }
        if (string.IsNullOrEmpty(folder)) { FolderBox.Focus(); return; }

        var action = ActionMap[Math.Clamp(ActionBox.SelectedIndex, 0, ActionMap.Length - 1)];
        var checksum = action.HasFlag(PerDownloadAction.VerifyChecksum)
            ? (string.IsNullOrEmpty(ChecksumBox.Text.Trim()) ? null : ChecksumBox.Text.Trim())
            : null;

        if (_probeResult?.IsPlaylist == true)
        {
            // Open the playlist selection popup
            var selectDlg = new PlaylistSelectDialog(_probeResult.PlaylistTitle, _probeResult.Entries)
                { Owner = this };
            if (selectDlg.ShowDialog() != true) return;

            var selected = selectDlg.SelectedEntries;
            if (selected.Length == 0) return;

            var formatId = PlaylistQualityToFormatId(PlaylistQualityCombo.SelectedIndex);

            ResultEntries = [.. selected.Select(e => new QueueEntry
            {
                Url             = e.Url,
                DestinationPath = Path.Combine(folder, "video"),
                PostAction      = action,
                VideoFormatId   = formatId,
            })];
        }
        else
        {
            var fileName = IsVideoUrl(url) ? "video" : GetFileNameFromUrl(url);

            string? formatId = IsVideoUrl(url) && VideoFormatCombo.SelectedItem is ComboBoxItem item
                ? item.Tag as string
                : null;

            var entry = new QueueEntry
            {
                Url              = url,
                DestinationPath  = Path.Combine(folder, fileName),
                PostAction       = action,
                ExpectedChecksum = checksum,
                VideoFormatId    = formatId,
                Overrides        = GetOverrides(),
            };

            ResultEntries    = [entry];
            // Keep legacy compat props for callers that still read them directly
            Url              = entry.Url;
            DestinationPath  = entry.DestinationPath;
            PostAction       = entry.PostAction;
            ExpectedChecksum = entry.ExpectedChecksum;
            VideoFormatId    = entry.VideoFormatId;
        }

        DialogResult = true;
        Close();
    }

    private static string GetFileNameFromUrl(string url)
    {
        try
        {
            var name = Path.GetFileName(new Uri(url).LocalPath);
            if (!string.IsNullOrEmpty(name)) return name;
        }
        catch { }
        return "download";
    }

    private static string PlaylistQualityToFormatId(int idx) => idx switch
    {
        1 => "bestvideo[height<=1080]+bestaudio/best[height<=1080]",
        2 => "bestvideo[height<=720]+bestaudio/best[height<=720]",
        3 => "bestvideo[height<=480]+bestaudio/best[height<=480]",
        4 => YtDlpWrapper.Mp3FormatId,
        _ => VideoFormat.BestQuality.Id,
    };
}
