namespace DM.Core.History;

public sealed class HistoryEntry
{
    public Guid     Id              { get; init; } = Guid.NewGuid();
    public string   Url             { get; init; } = "";
    public string   FileName        { get; init; } = "";
    public string   DestinationPath { get; init; } = "";
    public long     FileSizeBytes   { get; init; }
    public DateTime CompletedAt     { get; init; } = DateTime.Now;
    public string?  ChecksumSummary { get; init; }
    public bool     ChecksumOk      { get; init; } = true;
    public bool     WasCategorized  { get; init; }
}
