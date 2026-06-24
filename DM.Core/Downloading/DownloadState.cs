using System.Text.Json.Serialization;

namespace DM.Core.Downloading;

/// <summary>
/// Persisted to a <c>.dmstate</c> JSON file alongside the download.
/// Loaded on resume to continue each segment from where it stopped.
/// </summary>
public sealed class DownloadState
{
    public string Url             { get; set; } = "";
    public string DestinationPath { get; set; } = "";
    public long   TotalBytes      { get; set; }
    public SegmentState[] Segments { get; set; } = [];
}

public sealed class SegmentState
{
    public int  Index          { get; set; }
    public long StartByte      { get; set; }
    public long EndByte        { get; set; }
    public long BytesCompleted { get; set; }   // updated every few seconds while running

    /// <summary>True once the segment has received every byte in its range.</summary>
    [JsonIgnore]
    public bool IsComplete => BytesCompleted >= EndByte - StartByte + 1;

    /// <summary>
    /// The byte offset to resume from.  This becomes both the file-seek position
    /// and the left side of the Range header on the next request.
    ///
    /// Example:  segment covers bytes 0–2621439.
    ///   Fresh:   BytesCompleted=0      → ResumeFromByte=0       → Range: bytes=0-2621439
    ///   Partial: BytesCompleted=524288 → ResumeFromByte=524288  → Range: bytes=524288-2621439
    ///
    /// The server returns only the missing suffix; the prefix already on disk is untouched.
    /// </summary>
    [JsonIgnore]
    public long ResumeFromByte => StartByte + BytesCompleted;
}
