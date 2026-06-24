namespace DM.Core.Settings;

// All engine tunables live here — never hardcode values inside DownloadEngine.
// Construct once from user preferences and inject into the engine.
public sealed class EngineSettings
{
    public int MaxConnectionsPerFile { get; init; } = 8;
    public int MaxConcurrentDownloads { get; init; } = 3;
    public int RetryCount { get; init; } = 3;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);
    public long SpeedLimitBytesPerSecond { get; init; } = 0; // 0 = unlimited
    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public string TempDirectory { get; init; } = Path.Combine(Path.GetTempPath(), "DM");
}
