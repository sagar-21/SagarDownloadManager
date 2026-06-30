using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DM.Core.Models;
using DM.Core.Queue;

namespace DM.App.ViewModels;

public partial class DownloadQueueViewModel : ObservableObject
{
    private readonly DownloadQueueManager _queue;

    public DownloadQueueViewModel(DownloadQueueManager queue)
    {
        _queue = queue;
        _queue.EntryAdded   += OnEntryAdded;
        _queue.EntryChanged += OnEntryChanged;
        _queue.EntryRemoved += OnEntryRemoved;
    }

    public ObservableCollection<QueueItemViewModel> Items { get; } = [];

    // ── Commands ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private void PauseAll() => _queue.PauseAll();

    [RelayCommand]
    private void ResumeAll()
    {
        foreach (var item in Items.ToList())
        {
            if (item.Entry.Status == DownloadStatus.Paused)
                _queue.RequeueEntry(item.Entry);
        }
    }

    [RelayCommand]
    private void MoveUp(QueueItemViewModel? item)
    {
        if (item is null) return;
        int idx = Items.IndexOf(item);
        if (idx <= 0) return;
        _queue.MoveEntry(item.Entry, idx - 1);
        Items.Move(idx, idx - 1);
    }

    [RelayCommand]
    private void MoveDown(QueueItemViewModel? item)
    {
        if (item is null) return;
        int idx = Items.IndexOf(item);
        if (idx < 0 || idx >= Items.Count - 1) return;
        _queue.MoveEntry(item.Entry, idx + 1);
        Items.Move(idx, idx + 1);
    }

    [RelayCommand]
    private void SetHigh(QueueItemViewModel? item)   => ChangeP(item, DownloadPriority.High);
    [RelayCommand]
    private void SetNormal(QueueItemViewModel? item) => ChangeP(item, DownloadPriority.Normal);
    [RelayCommand]
    private void SetLow(QueueItemViewModel? item)    => ChangeP(item, DownloadPriority.Low);

    private void ChangeP(QueueItemViewModel? item, DownloadPriority p)
    {
        if (item is null) return;
        _queue.SetPriority(item.Entry, p);
        item.Priority = p;
    }

    // ── Queue event handlers ─────────────────────────────────────────────────

    private void OnEntryAdded(QueueEntry entry)
    {
        if (IsTerminal(entry.Status)) return;
        Dispatch(() => Items.Add(new QueueItemViewModel(entry)));
    }

    private void OnEntryChanged(QueueEntry entry)
    {
        Dispatch(() =>
        {
            var vm = Items.FirstOrDefault(i => i.Entry.Id == entry.Id);
            if (vm is null)
            {
                if (!IsTerminal(entry.Status))
                    Items.Add(new QueueItemViewModel(entry));
                return;
            }
            if (IsTerminal(entry.Status)) Items.Remove(vm);
            else                          vm.Sync(entry);
        });
    }

    private void OnEntryRemoved(Guid id) =>
        Dispatch(() =>
        {
            var vm = Items.FirstOrDefault(i => i.Entry.Id == id);
            if (vm is not null) Items.Remove(vm);
        });

    private static bool IsTerminal(DownloadStatus s) =>
        s is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled;

    private static void Dispatch(Action a) =>
        Application.Current?.Dispatcher.Invoke(a);
}

// ── Per-item VM ──────────────────────────────────────────────────────────────

public partial class QueueItemViewModel : ObservableObject
{
    public QueueEntry Entry { get; }

    [ObservableProperty] private DownloadPriority _priority;
    [ObservableProperty] private DownloadStatus   _status;
    [ObservableProperty] private double           _progress;
    [ObservableProperty] private string           _fileName  = "";
    [ObservableProperty] private string           _speedText = "";
    [ObservableProperty] private string           _sizeText  = "";

    public bool IsDownloading => Status == DownloadStatus.Downloading;
    public bool IsPaused      => Status == DownloadStatus.Paused;

    partial void OnStatusChanged(DownloadStatus v)
    {
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(IsPaused));
    }

    public QueueItemViewModel(QueueEntry entry)
    {
        Entry = entry;
        Sync(entry);
    }

    public void Sync(QueueEntry e)
    {
        Priority  = e.Priority;
        Status    = e.Status;
        Progress  = e.TotalBytes > 0 ? Math.Clamp((double)e.DownloadedBytes / e.TotalBytes, 0, 1) : 0;
        FileName  = Path.GetFileName(e.DestinationPath).TrimEnd();
        if (string.IsNullOrEmpty(FileName)) FileName = e.Url;
        SpeedText = e.SpeedBytesPerSecond > 0
            ? $"{DownloadItemViewModel.FormatBytes((long)e.SpeedBytesPerSecond)}/s" : "";
        SizeText  = e.TotalBytes > 0
            ? $"{DownloadItemViewModel.FormatBytes(e.DownloadedBytes)} / {DownloadItemViewModel.FormatBytes(e.TotalBytes)}"
            : "";
    }
}
