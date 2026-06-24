using DM.Core.Models;

namespace DM.Core.Services;

public interface IDownloadEngine
{
    Task<DownloadTask> EnqueueAsync(string url, string destinationPath, CancellationToken ct = default);
    Task PauseAsync(Guid taskId, CancellationToken ct = default);
    Task ResumeAsync(Guid taskId, CancellationToken ct = default);
    Task CancelAsync(Guid taskId, CancellationToken ct = default);
    IReadOnlyList<DownloadTask> GetAllTasks();
}
