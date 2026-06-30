namespace DM.Core.PostDownload;

[Flags]
public enum PerDownloadAction
{
    None             = 0,
    ShowNotification = 1,
    OpenFile         = 2,
    OpenFolder       = 4,
    VerifyChecksum   = 8,
}

public enum QueueFinishAction { None, ShowNotification, Sleep, Shutdown }

public sealed class PostDownloadActionSettings
{
    /// <summary>Show a balloon tip for every completed download.</summary>
    public bool             AlwaysNotify      { get; set; } = true;
    public QueueFinishAction QueueFinishAction { get; set; } = QueueFinishAction.ShowNotification;
}
