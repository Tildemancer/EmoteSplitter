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

    public const int MaxMarkerLength = 32;

    public string ContinuationPrefix { get; set => field = Capped(value, MaxMarkerLength); } = string.Empty;

    public string ContinuationSuffix { get; set => field = Capped(value, MaxMarkerLength); } = string.Empty;

    public string FinalMarker { get; set => field = Capped(value, MaxMarkerLength); } = string.Empty;

    // Ye olde out-of-range guard against hand-edited configs.
    internal static string Capped(string? text, int length) =>
        text is null ? string.Empty : text.Length > length ? text[..length] : text;

    public List<ChunkMarker> Markers { get; set; } = [];

    internal bool IsOoc { get; set; }

    // Just the tag, the splitter adds the space.
    internal string OocOpen { get; set; } = string.Empty;

    internal string OocClose { get; set; } = string.Empty;
}
