using System.Collections.Generic;

namespace EmoteSplitter.Splitting;

// Configuration saves one as Split.
// Each split's set field is internal, so Json.NET leaves them out of the saved file.
public sealed record SplitOptions
{
    // The limit for the line with its header, OOC, and markers.
    // 500 is the chat box's MaxByte
    internal int MaxBytes { get; set; } = 500;

    internal int SafetyMargin { get; set; }

    public bool PreferSentenceBreaks { get; set; } = true;

    public string ContinuationPrefix { get; set; } = string.Empty;

    public string ContinuationSuffix { get; set; } = string.Empty;

    public string FinalMarker { get; set; } = string.Empty;

    public List<ChunkMarker> Markers { get; set; } = [];

    internal bool IsOoc { get; set; }

    // Just the tag, the splitter adds the space.
    internal string OocOpen { get; set; } = string.Empty;

    internal string OocClose { get; set; } = string.Empty;
}
