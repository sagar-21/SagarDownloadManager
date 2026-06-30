using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DM.App.ViewModels;
using DM.App.Views.Dialogs;
using DM.App.Views.Pages;
using DM.Core.PostDownload;
using DM.Core.Queue;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using WinForms = System.Windows.Forms;
using GdiBitmap = System.Drawing.Bitmap;
using GdiIcon   = System.Drawing.Icon;

namespace DM.App.Views;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _vm;
    private DownloadsPage? _downloadsPage;
    private QueuePage?     _queuePage;
    private HistoryPage?   _historyPage;
    private SettingsPage?  _settingsPage;
    private WinForms.NotifyIcon?  _trayIcon;
    private ClipboardOfferPopup?  _clipPopup;
    private bool _exitRequested;

    public MainWindow()
    {
        InitializeComponent();
        _vm = ((App)Application.Current).ViewModel;
        DataContext = _vm;

        Loaded   += OnWindowLoaded;
        Closing  += OnWindowClosing;
    }

    // ── Startup ────────────────────────────────────────────────────────────

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        NavList.SelectedIndex = 0;
        SetupTrayIcon();
        SubscribeToQueueEvents();
        InitClipboardMonitor();
    }

    private void SubscribeToQueueEvents()
    {
        _vm.QueueManager.DownloadCompleted += OnDownloadCompleted;
        _vm.QueueManager.QueueCompleted    += OnQueueCompleted;
        _vm.BrowserDownloadRequested       += url => Dispatcher.Invoke(() => OpenAddDialogFor(url));
    }

    private void OnDownloadCompleted(QueueEntry entry, string finalPath)
    {
        Dispatcher.Invoke(() =>
        {
            bool notify = _vm.QueueManager.Settings.Actions.AlwaysNotify
                       || entry.PostAction.HasFlag(PerDownloadAction.ShowNotification);

            if (notify)
                _trayIcon?.ShowBalloonTip(3000, "Download Complete",
                    Path.GetFileName(finalPath), WinForms.ToolTipIcon.Info);

            if (!entry.ChecksumOk && !string.IsNullOrEmpty(entry.ChecksumSummary))
                _trayIcon?.ShowBalloonTip(5000, "Checksum Mismatch",
                    $"{Path.GetFileName(finalPath)}: {entry.ChecksumSummary}",
                    WinForms.ToolTipIcon.Warning);

            if (entry.PostAction.HasFlag(PerDownloadAction.OpenFile) && File.Exists(finalPath))
                Process.Start(new ProcessStartInfo(finalPath) { UseShellExecute = true });

            if (entry.PostAction.HasFlag(PerDownloadAction.OpenFolder))
                Process.Start("explorer.exe", $"/select,\"{finalPath}\"");
        });
    }

    private void OnQueueCompleted()
    {
        Dispatcher.Invoke(() =>
        {
            var action = _vm.QueueManager.Settings.Actions.QueueFinishAction;

            if (action == QueueFinishAction.ShowNotification || _vm.QueueManager.Settings.Actions.AlwaysNotify)
                _trayIcon?.ShowBalloonTip(5000, "Queue Complete",
                    "All downloads finished.", WinForms.ToolTipIcon.Info);

            if (action == QueueFinishAction.Shutdown)
                Process.Start("shutdown.exe", "/s /t 60");
            else if (action == QueueFinishAction.Sleep)
                WinForms.Application.SetSuspendState(WinForms.PowerState.Suspend, false, false);
        });
    }

    // ── Navigation ─────────────────────────────────────────────────────────

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is not ListBoxItem item) return;

        switch (item.Tag as string)
        {
            case "downloads":
                _downloadsPage ??= new DownloadsPage(_vm);
                ContentFrame.Navigate(_downloadsPage);
                break;
            case "queue":
                _queuePage ??= new QueuePage(_vm.QueueVm);
                ContentFrame.Navigate(_queuePage);
                break;
            case "history":
                _historyPage ??= new HistoryPage(_vm.HistoryVm);
                ContentFrame.Navigate(_historyPage);
                break;
            case "settings":
                _settingsPage ??= new SettingsPage(_vm);
                ContentFrame.Navigate(_settingsPage);
                break;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => DragMove();

    private void TitleMinBtn_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void TitleCloseBtn_Click(object sender, RoutedEventArgs e)
        => Hide();

    private void ThemeBtn_Click(object sender, RoutedEventArgs e)
    {
        _vm.ToggleThemeCommand.Execute(null);
        ThemeIcon.Symbol = _vm.IsDarkTheme
            ? SymbolRegular.WeatherMoon24
            : SymbolRegular.WeatherSunny24;
        ThemeLabel.Text = _vm.IsDarkTheme ? "Dark theme" : "Light theme";
    }

    // ── System tray ────────────────────────────────────────────────────────

    private void SetupTrayIcon()
    {
        var menu = new WinForms.ContextMenuStrip();
        var openItem = new WinForms.ToolStripMenuItem("Open Download Manager");
        openItem.Click += (_, _) => RestoreWindow();
        var exitItem = new WinForms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => { _exitRequested = true; Close(); };
        menu.Items.Add(openItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new WinForms.NotifyIcon
        {
            Icon             = GenerateTrayIcon(),  // GdiIcon
            Text             = "Download Manager",
            Visible          = true,
            ContextMenuStrip = menu,
        };
        _trayIcon.DoubleClick += (_, _) => RestoreWindow();

        // Dispose the icon even when the process is killed (debugger stop, task manager, etc.)
        // so it doesn't leave a ghost in the tray until the user hovers over the dead area.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        };
    }

    // ── Clipboard monitor ──────────────────────────────────────────────────────

    private void InitClipboardMonitor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var mon  = _vm.ClipboardMonitor;

        mon.UrlDetected += OnClipboardUrl;

        // Start immediately if the setting is on
        if (_vm.SettingsVm.ClipboardMonitor)
            mon.Start(hwnd);

        // React to the toggle in Settings
        _vm.SettingsVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(SettingsViewModel.ClipboardMonitor)) return;
            if (_vm.SettingsVm.ClipboardMonitor) mon.Start(hwnd);
            else                                 mon.Stop();
        };
    }

    private void OnClipboardUrl(string url)
    {
        Dispatcher.Invoke(() =>
        {
            _clipPopup?.Close();

            var popup = new ClipboardOfferPopup(url);
            popup.DownloadRequested += u => OpenAddDialogFor(u);
            popup.Closed += (_, _) => { if (ReferenceEquals(_clipPopup, popup)) _clipPopup = null; };
            _clipPopup = popup;
            popup.Show();
        });
    }

    /// <summary>
    /// Opens AddDownloadDialog with the given URL pre-filled so the user can choose
    /// format, folder, and other options before the download is enqueued.
    /// Called both from the clipboard offer popup and can be reused elsewhere.
    /// </summary>
    private void OpenAddDialogFor(string url)
    {
        _clipPopup?.Close();

        // Bring the main window forward so the modal dialog has a visible owner
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            Show();
            WindowState = WindowState.Normal;
        }
        Activate();

        var dialog = new AddDownloadDialog(_vm.SettingsService, url) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        foreach (var entry in dialog.ResultEntries)
            _vm.QueueManager.Enqueue(entry);

        // Switch to the Downloads tab so the user can see the download start
        NavList.SelectedIndex = 0;
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_exitRequested)
        {
            // Minimize to tray instead of closing
            e.Cancel = true;
            Hide();
            return;
        }
        _trayIcon?.Dispose();
        _vm.ClipboardMonitor.Stop();
        _vm.Connector.Stop();
    }

    private void RestoreWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    // ── Tray icon generation (32×32 WPF→GDI+ via PNG round-trip) ──────────

    private static GdiIcon GenerateTrayIcon()
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            // Blue circle
            dc.DrawEllipse(
                new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 120, 212)),
                null,
                new System.Windows.Point(16, 16), 13, 13);

            // White down-arrow stem
            var stem = new StreamGeometry();
            using (var ctx = stem.Open())
            {
                ctx.BeginFigure(new System.Windows.Point(13.5, 7),  true, true);
                ctx.LineTo(new System.Windows.Point(18.5, 7),  true, false);
                ctx.LineTo(new System.Windows.Point(18.5, 14), true, false);
                ctx.LineTo(new System.Windows.Point(13.5, 14), true, false);
            }
            dc.DrawGeometry(Brushes.White, null, stem);

            // White arrow head
            var head = new StreamGeometry();
            using (var ctx = head.Open())
            {
                ctx.BeginFigure(new System.Windows.Point(16, 23), true, true);
                ctx.LineTo(new System.Windows.Point(9,  14), true, false);
                ctx.LineTo(new System.Windows.Point(23, 14), true, false);
            }
            dc.DrawGeometry(Brushes.White, null, head);
        }

        var rtb = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);

        using var ms = new MemoryStream();
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        enc.Save(ms);
        ms.Position = 0;

        using var bmp = new GdiBitmap(ms);
        return GdiIcon.FromHandle(bmp.GetHicon());
    }
}
