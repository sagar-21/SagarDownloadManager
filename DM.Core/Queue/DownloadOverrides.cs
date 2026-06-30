using DM.Core.Models;
using DM.Core.Settings;

namespace DM.Core.Queue;

/// <summary>
/// Per-download settings that override global <see cref="AppSettings"/> defaults.
/// A null field means "use the global default".
/// Resolution rule: effectiveValue = override ?? globalDefault.
/// </summary>
public sealed class DownloadOverrides
{
    /// <summary>Destination folder for this download only. Null = use <see cref="AppSettings.DefaultDownloadFolder"/>.</summary>
    public string?             DownloadFolder           { get; set; }
    /// <summary>Connection count for this download. Null = use connection mode from global settings.</summary>
    public int?                ConnectionCount          { get; set; }
    /// <summary>Speed cap for this download in bytes/s. Null = use global per-download limit. 0 = unlimited.</summary>
    public long?               SpeedLimitBytesPerSecond { get; set; }
    /// <summary>Priority for this download. Null = use <see cref="AppSettings.DefaultPriority"/>.</summary>
    public DownloadPriority?   Priority                 { get; set; }
    /// <summary>Video quality for this download. Null = use <see cref="AppSettings.DefaultVideoQuality"/>.</summary>
    public VideoQualityPreset? VideoQuality             { get; set; }

    public bool IsEmpty =>
        DownloadFolder           == null &&
        ConnectionCount          == null &&
        SpeedLimitBytesPerSecond == null &&
        Priority                 == null &&
        VideoQuality             == null;
}
