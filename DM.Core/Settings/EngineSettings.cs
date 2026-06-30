namespace DM.Core.Settings;

// All engine tunables live here — never hardcode values inside DownloadEngine.
// Construct once from user preferences and inject into the engine.
public sealed class EngineSettings
{
    public int MaxConnectionsPerFile { get; init; } = 8;
    public int MaxConcurrentDownloads { get; init; } = 3;
    public int RetryCount { get; init; } = 3;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan RetryDelayMax { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Connections opened at session start. Auto-tune ramps up to <see cref="MaxConnectionsPerFile"/>.</summary>
    public int InitialConnectionsPerFile { get; init; } = 2;
    /// <summary>Minimum byte range per chunk. Prevents excessive fragmentation on large files.</summary>
    public long MinChunkBytes { get; init; } = 2L * 1024 * 1024;
    /// <summary>When true, spawns additional connections while throughput is still growing.</summary>
    public bool AutoTuneConnections { get; init; } = true;
    /// <summary>How often the auto-tuner samples throughput and decides to add a connection.</summary>
    public TimeSpan AutoTuneInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Bounded channel capacity per segment (in 80 KB buffer slots).
    /// Network reads fill the channel; disk writes drain it.
    /// Default 8 slots = 640 KB per segment of in-flight RAM, capping total
    /// buffered memory at RamBufferChunks × 80 KB × MaxConnectionsPerFile.
    /// </summary>
    public int RamBufferChunks { get; init; } = 8;

    public long SpeedLimitBytesPerSecond { get; init; } = 0; // 0 = unlimited
    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public string TempDirectory { get; init; } = Path.Combine(Path.GetTempPath(), "DM");
}
