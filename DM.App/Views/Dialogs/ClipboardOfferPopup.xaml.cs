using System.IO;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DM.App.Views.Dialogs;

public partial class ClipboardOfferPopup : Window
{
    private const double AutoCloseSecs = 8.0;

    private readonly string  _url;
    private DispatcherTimer? _timer;
    private double   _remaining = AutoCloseSecs;

    public event Action<string>? DownloadRequested;

    public ClipboardOfferPopup(string url)
    {
        InitializeComponent();
        _url = url;

        // Populate labels
        TitleText.Text = InferFileName(url);
        UrlText.Text   = TrimUrl(url);

        // Position bottom-right of the work area (IDM-style)
        var area = SystemParameters.WorkArea;
        Left = area.Right  - Width  - 16;
        Top  = area.Bottom - Height - 16;

        Loaded += (_, _) =>
        {
            // Slide in
            var sb = (Storyboard)Resources["SlideIn"];
            Storyboard.SetTarget(sb, Root);
            sb.Begin();

            // Countdown
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _timer.Tick += OnTick;
            _timer.Start();
        };
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _remaining -= 0.05;
        CountdownBar.Value = Math.Max(0, _remaining / AutoCloseSecs);
        if (_remaining <= 0) FadeClose();
    }

    private void DownloadBtn_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        DownloadRequested?.Invoke(_url);
        Close();
    }

    private void DismissBtn_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        FadeClose();
    }

    private void FadeClose()
    {
        _timer?.Stop();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) => Close();
        Root.BeginAnimation(OpacityProperty, fade);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string InferFileName(string url)
    {
        try
        {
            var name = Path.GetFileName(new Uri(url).AbsolutePath);
            if (!string.IsNullOrWhiteSpace(name) && name.Contains('.'))
                return name;
        }
        catch { }
        // Fall back to host
        try { return new Uri(url).Host; } catch { }
        return "Downloadable link detected";
    }

    private static string TrimUrl(string url)
    {
        const int max = 60;
        return url.Length > max ? url[..max] + "…" : url;
    }
}
