using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using Wpf.Ui.Controls;

namespace DM.App.Views.Dialogs;

public partial class LicensesDialog : FluentWindow
{
    private sealed record LicenseEntry(
        string Name,
        string Version,
        string Spdx,
        string Homepage,
        string LicenseFileUrl,
        string LocalFile,
        bool   IsFfmpeg  = false,
        string? SourceUrl = null);

    private static readonly string LicensesDir =
        Path.Combine(AppContext.BaseDirectory, "licenses");

    private static readonly IReadOnlyList<LicenseEntry> Entries =
    [
        new(".NET Runtime",          "9.0",    "MIT",
            "https://github.com/dotnet/runtime",
            "https://github.com/dotnet/runtime/blob/main/LICENSE.TXT",
            "dotnet-runtime.txt"),

        new("WPF",                   "9.0",    "MIT",
            "https://github.com/dotnet/wpf",
            "https://github.com/dotnet/wpf/blob/main/LICENSE.TXT",
            "wpf.txt"),

        new("CommunityToolkit.Mvvm", "8.4.2",  "MIT",
            "https://github.com/CommunityToolkit/dotnet",
            "https://github.com/CommunityToolkit/dotnet/blob/main/License.md",
            "communitytoolkit-mvvm.txt"),

        new("WPF UI (Wpf.Ui)",      "3.0.5",  "MIT",
            "https://github.com/lepoco/wpfui",
            "https://github.com/lepoco/wpfui/blob/main/LICENSE",
            "wpfui.txt"),

        new("yt-dlp",                "latest", "Unlicense",
            "https://github.com/yt-dlp/yt-dlp",
            "https://github.com/yt-dlp/yt-dlp/blob/master/LICENSE",
            "yt-dlp.txt"),

        new("ffmpeg",                "LGPL build", "LGPL-2.1-or-later",
            "https://ffmpeg.org",
            "https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html",
            "ffmpeg.txt",
            IsFfmpeg:  true,
            SourceUrl: "https://ffmpeg.org/download.html"),
    ];

    public LicensesDialog()
    {
        InitializeComponent();
        ComponentList.ItemsSource  = Entries;
        ComponentList.SelectedIndex = 0;
    }

    private void ComponentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ComponentList.SelectedItem is not LicenseEntry entry) return;

        NameText.Text    = entry.Name;
        VersionText.Text = entry.Version;
        SpdxText.Text    = entry.Spdx;

        HomepageLink.NavigateUri    = new Uri(entry.Homepage);
        HomepageLinkRun.Text        = entry.Homepage;
        LicenseFileLink.NavigateUri = new Uri(entry.LicenseFileUrl);
        LicenseFileLinkRun.Text     = "View on " + new Uri(entry.LicenseFileUrl).Host + " ↗";

        SpdxBadge.Background = entry.Spdx switch
        {
            "MIT"                => new SolidColorBrush(Color.FromArgb(0xCC, 0x1A, 0x7C, 0x3E)),
            "Unlicense"          => new SolidColorBrush(Color.FromArgb(0xCC, 0x4E, 0x52, 0x5A)),
            "LGPL-2.1-or-later" => new SolidColorBrush(Color.FromArgb(0xCC, 0xA0, 0x52, 0x00)),
            _                    => new SolidColorBrush(Color.FromArgb(0xCC, 0x00, 0x5A, 0x9E)),
        };

        FfmpegNotice.Visibility = entry.IsFfmpeg ? Visibility.Visible : Visibility.Collapsed;
        if (entry.IsFfmpeg && entry.SourceUrl is not null)
            FfmpegSourceLink.NavigateUri = new Uri(entry.SourceUrl);

        LoadLicenseText(entry);
    }

    private void LoadLicenseText(LicenseEntry entry)
    {
        var path = Path.Combine(LicensesDir, entry.LocalFile);
        if (!File.Exists(path))
        {
            LicenseBodyText.Text =
                $"License file not found at:\n  licenses\\{entry.LocalFile}\n\n" +
                $"Drop the official license file there. Source:\n  {entry.LicenseFileUrl}";
            return;
        }

        var text = File.ReadAllText(path);
        LicenseBodyText.Text = text.TrimStart().StartsWith("[PLACEHOLDER")
            ? $"License text not yet populated.\n\n" +
              $"Replace  licenses\\{entry.LocalFile}  with the official text from:\n  {entry.LicenseFileUrl}"
            : text;
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
