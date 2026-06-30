using System.Text.Json;

namespace DM.Core.History;

public sealed class DownloadHistory
{
    private static readonly JsonSerializerOptions JsonOpts = new()
        { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly string HistoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DownloadManager", "history.json");

    private const int MaxEntries = 1000;

    private readonly object              _lock    = new();
    private readonly List<HistoryEntry> _entries = [];

    public event Action? Changed;

    public DownloadHistory() { Load(); }

    public void Add(HistoryEntry entry)
    {
        lock (_lock)
        {
            _entries.Insert(0, entry);
            if (_entries.Count > MaxEntries)
                _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
        }
        _ = SaveAsync();
        Changed?.Invoke();
    }

    public void Remove(Guid id)
    {
        lock (_lock) { _entries.RemoveAll(e => e.Id == id); }
        _ = SaveAsync();
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_lock) { _entries.Clear(); }
        _ = SaveAsync();
        Changed?.Invoke();
    }

    public HistoryEntry[] Search(string? query)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(query))
                return [.. _entries];
            var q = query.Trim();
            return [.. _entries.Where(e =>
                e.FileName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                e.Url.Contains(q, StringComparison.OrdinalIgnoreCase))];
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(HistoryPath)) return;
            var list = JsonSerializer.Deserialize<List<HistoryEntry>>(
                File.ReadAllText(HistoryPath), JsonOpts);
            if (list is not null) { _entries.Clear(); _entries.AddRange(list); }
        }
        catch { }
    }

    private async Task SaveAsync()
    {
        try
        {
            List<HistoryEntry> snapshot;
            lock (_lock) { snapshot = [.. _entries]; }
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
            await File.WriteAllTextAsync(HistoryPath,
                JsonSerializer.Serialize(snapshot, JsonOpts));
        }
        catch { }
    }
}
