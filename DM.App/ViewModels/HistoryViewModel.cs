using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DM.App.ViewModels;
using DM.Core.History;
using DM.Core.Queue;

namespace DM.App.ViewModels;

public partial class HistoryViewModel : ObservableObject
{
    private readonly DownloadQueueManager _queue;

    [ObservableProperty] private string _searchQuery = "";

    public ObservableCollection<HistoryItemViewModel> Items { get; } = [];
    public bool IsEmpty => Items.Count == 0;

    public HistoryViewModel(DownloadQueueManager queue)
    {
        _queue = queue;
        _queue.History.Changed += () =>
            Application.Current?.Dispatcher.InvokeAsync(RefreshItems);
        RefreshItems();
    }

    partial void OnSearchQueryChanged(string value) => RefreshItems();

    private void RefreshItems()
    {
        Items.Clear();
        foreach (var e in _queue.History.Search(SearchQuery))
            Items.Add(new HistoryItemViewModel(e));
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void ReDownload(HistoryItemViewModel? item)
    {
        if (item is null) return;
        _queue.Enqueue(new QueueEntry
        {
            Url             = item.Url,
            DestinationPath = item.DestinationPath,
        });
    }

    [RelayCommand]
    private void Remove(HistoryItemViewModel? item)
    {
        if (item is null) return;
        _queue.History.Remove(item.Id);
        Items.Remove(item);
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void ClearHistory()
    {
        _queue.History.Clear();
        Items.Clear();
        OnPropertyChanged(nameof(IsEmpty));
    }
}

public sealed class HistoryItemViewModel
{
    public Guid    Id              { get; }
    public string  Url             { get; }
    public string  FileName        { get; }
    public string  DestinationPath { get; }
    public long    FileSizeBytes   { get; }
    public DateTime CompletedAt   { get; }
    public string? ChecksumSummary { get; }
    public bool    ChecksumOk      { get; }
    public bool    HasChecksum     => !string.IsNullOrEmpty(ChecksumSummary);

    public string SizeText    => DownloadItemViewModel.FormatBytes(FileSizeBytes);
    public string TimeAgoText => FormatTimeAgo(CompletedAt);

    public HistoryItemViewModel(HistoryEntry e)
    {
        Id              = e.Id;
        Url             = e.Url;
        FileName        = e.FileName;
        DestinationPath = e.DestinationPath;
        FileSizeBytes   = e.FileSizeBytes;
        CompletedAt     = e.CompletedAt;
        ChecksumSummary = e.ChecksumSummary;
        ChecksumOk      = e.ChecksumOk;
    }

    private static string FormatTimeAgo(DateTime dt)
    {
        var diff = DateTime.Now - dt;
        if (diff.TotalMinutes < 1) return "just now";
        if (diff.TotalHours   < 1) return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalDays    < 1) return $"{(int)diff.TotalHours}h ago";
        if (diff.TotalDays   < 30) return $"{(int)diff.TotalDays}d ago";
        return dt.ToString("MMM d, yyyy");
    }
}
