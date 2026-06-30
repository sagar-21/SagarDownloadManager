using System.IO;
using System.Windows;
using DM.Core.PostDownload;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace DM.App.Views.Dialogs;

public partial class BatchAddDialog : FluentWindow
{
    private static readonly PerDownloadAction[] ActionMap =
    [
        PerDownloadAction.None,
        PerDownloadAction.ShowNotification,
        PerDownloadAction.OpenFolder,
    ];

    public (string Url, string Dest)[] Entries { get; private set; } = [];

    public BatchAddDialog()
    {
        InitializeComponent();
        FolderBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Downloads";
        Loaded += (_, _) => UrlsBox.Focus();
    }

    private void UrlsBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        int count = GetValidUrls().Length;
        CountLabel.Text = count == 0 ? "" : $"{count} valid URL{(count == 1 ? "" : "s")}";
        OkBtn.Content   = count == 0 ? "Queue Downloads" : $"Queue {count} Download{(count == 1 ? "" : "s")}";
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title              = "Select destination folder",
            InitialDirectory   = FolderBox.Text,
        };
        if (dlg.ShowDialog(this) == true) FolderBox.Text = dlg.FolderName;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)    => Commit();
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Commit()
    {
        var urls   = GetValidUrls();
        if (urls.Length == 0) { UrlsBox.Focus(); return; }

        var folder = FolderBox.Text.Trim();
        if (string.IsNullOrEmpty(folder)) { FolderBox.Focus(); return; }

        var action = ActionMap[Math.Clamp(ActionBox.SelectedIndex, 0, ActionMap.Length - 1)];
        Entries = urls
            .Select(u => (u, Path.Combine(folder, FilenameFromUrl(u))))
            .Select((t, i) => {
                // deduplicate filenames if multiple URLs produce the same filename
                string dest = t.Item2;
                return (t.u, dest);
            })
            .ToArray();

        // Store the chosen action so callers can read it
        _postAction = action;

        DialogResult = true;
        Close();
    }

    private PerDownloadAction _postAction;
    public PerDownloadAction PostAction => _postAction;

    private string[] GetValidUrls() =>
        UrlsBox.Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(u => Uri.TryCreate(u, UriKind.Absolute, out var uri)
                        && (uri.Scheme == "http" || uri.Scheme == "https"))
            .Distinct()
            .ToArray();

    private static string FilenameFromUrl(string url)
    {
        try
        {
            var name = Path.GetFileName(new Uri(url).LocalPath);
            return string.IsNullOrEmpty(name) ? "download" : name;
        }
        catch { return "download"; }
    }
}
