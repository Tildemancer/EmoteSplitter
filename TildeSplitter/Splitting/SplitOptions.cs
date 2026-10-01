using System.Collections.Generic;
using System.Runtime.Serialization;

namespace TildeSplitter.Splitting;

// Configuration saves one as Split.
// IgnoreDataMember keeps the fields each split sets out of the saved file.
public sealed record SplitOptions
{
    // The limit for the line with its header, OOC, and markers.
    // 500 is the chat box's MaxByte
    [IgnoreDataMember]
    public int MaxBytes { get; set; } = 500;

    [IgnoreDataMember]
    public int SafetyMargin { get; set; }

    public bool PreferSentenceBreaks { get; set; } = true;

    public string ContinuationPrefix { get; set; } = string.Empty;

    public string ContinuationSuffix { get; set; } = string.Empty;

    public string FinalMarker { get; set; } = string.Empty;

    public List<ChunkMarker> Markers { get; set; } = [];

    [IgnoreDataMember]
    public bool IsOoc { get; set; }

    // Just the tag, the splitter adds the space.
    [IgnoreDataMember]
    public string OocOpen { get; set; } = string.Empty;

    [IgnoreDataMember]
    public string OocClose { get; set; } = string.Empty;
}
