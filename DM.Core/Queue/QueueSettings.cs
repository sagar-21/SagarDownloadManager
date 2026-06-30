using DM.Core.PostDownload;

namespace DM.Core.Queue;

public sealed class QueueSettings
{
    public int  MaxConcurrentDownloads             { get; set; } = 3;
    public long GlobalSpeedLimitBytesPerSecond     { get; set; } = 0;
    public long DefaultPerDownloadLimitBytesPerSec { get; set; } = 0;
    public SchedulerSettings      Scheduler      { get; set; } = new();
    public List<SpeedRule>        SpeedRules     { get; set; } = [];
    public CategorizationSettings Categorization { get; set; } = new();
    public PostDownloadActionSettings Actions    { get; set; } = new();
}

public sealed class SchedulerSettings
{
    public bool           Enabled    { get; set; }
    public TimeOnly       StartTime  { get; set; } = new(8,  0);
    public TimeOnly       StopTime   { get; set; } = new(22, 0);
    public RecurrenceMode Recurrence { get; set; } = RecurrenceMode.Daily;
    public DayOfWeek[]    ActiveDays { get; set; } =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
        DayOfWeek.Thursday, DayOfWeek.Friday,
    ];
}

public enum RecurrenceMode { Daily, Weekly, Once }

public sealed class SpeedRule
{
    public TimeOnly    From                { get; set; }
    public TimeOnly    To                  { get; set; }
    public long        LimitBytesPerSecond { get; set; }
    public DayOfWeek[] ActiveDays          { get; set; } = Enum.GetValues<DayOfWeek>();
}
