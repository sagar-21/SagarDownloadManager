using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using DM.Core.VideoStreaming;
using Wpf.Ui.Controls;

namespace DM.App.Views.Dialogs;

/// <summary>
/// View-model for one row in the playlist selection list.
/// Implements INotifyPropertyChanged so SelectAll/DeselectAll updates checkboxes via binding.
/// </summary>
public sealed class PlaylistItemVm : INotifyPropertyChanged
{
    private bool _isChecked = true;

    public PlaylistEntry Entry { get; init; } = null!;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    public string IndexDisplay => $"{Entry.Index}.";
    public string DurationText => Entry.DurationDisplay;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class PlaylistSelectDialog : FluentWindow
{
    private readonly List<PlaylistItemVm> _items;

    public PlaylistEntry[] SelectedEntries { get; private set; } = [];

    public PlaylistSelectDialog(string playlistTitle, PlaylistEntry[] entries)
    {
        InitializeComponent();

        _items = entries.Select(e => new PlaylistItemVm { Entry = e, IsChecked = true }).ToList();
        ItemsList.ItemsSource = _items;

        HeaderText.Text  = $"{entries.Length} video{(entries.Length == 1 ? "" : "s")} found";
        SubtitleText.Text = string.IsNullOrEmpty(playlistTitle) ? "Playlist" : playlistTitle;

        UpdateCount();
    }

    private void UpdateCount()
    {
        var count = _items.Count(i => i.IsChecked);
        CountText.Text        = $"{count} of {_items.Count} selected";
        DownloadBtn.Content   = count == 0 ? "Download" : $"Download {count}";
        DownloadBtn.IsEnabled = count > 0;
    }

    private void Checkbox_Changed(object sender, RoutedEventArgs e) => UpdateCount();

    private void ItemRow_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Clicking the row background (not directly on the checkbox) also toggles selection.
        // Guard: if the original source is the CheckBox itself, its own handler already fires.
        if (e.OriginalSource is System.Windows.Controls.CheckBox) return;

        if ((sender as FrameworkElement)?.DataContext is PlaylistItemVm vm)
        {
            vm.IsChecked = !vm.IsChecked;
            UpdateCount();
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var vm in _items) vm.IsChecked = true;
        UpdateCount();
    }

    private void DeselectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var vm in _items) vm.IsChecked = false;
        UpdateCount();
    }

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        SelectedEntries = _items.Where(i => i.IsChecked).Select(i => i.Entry).ToArray();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
