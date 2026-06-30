namespace DM.Core.VideoStreaming;

public sealed class YtDlpSettings
{
    public string   ToolsDirectory  { get; init; } =
        Path.Combine(AppContext.BaseDirectory, "Tools");
    public TimeSpan MetadataTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
