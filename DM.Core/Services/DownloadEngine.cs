using DM.Core.Models;
using DM.Core.Settings;

namespace DM.Core.Services;

// Placeholder implementation — all behaviour is driven by the injected EngineSettings.
public sealed class DownloadEngine : IDownloadEngine
{
    private readonly EngineSettings _settings;
    private readonly List<DownloadTask> _tasks = [];

    public DownloadEngine(EngineSettings settings)
    {
        _settings = settings;
    }

    public Task<DownloadTask> EnqueueAsync(string url, string destinationPath, CancellationToken ct = default)
    {
        var task = new DownloadTask { Url = url, DestinationPath = destinationPath };
        _tasks.Add(task);
        return Task.FromResult(task);
    }

    public Task PauseAsync(Guid taskId, CancellationToken ct = default) => Task.CompletedTask;
    public Task ResumeAsync(Guid taskId, CancellationToken ct = default) => Task.CompletedTask;
    public Task CancelAsync(Guid taskId, CancellationToken ct = default) => Task.CompletedTask;

    public IReadOnlyList<DownloadTask> GetAllTasks() => _tasks.AsReadOnly();
}
