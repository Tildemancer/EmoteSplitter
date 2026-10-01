using System;
using System.Collections.Generic;

using TildeTools.Modules.EmoteSplitter.Sending;
using TildeTools.Modules.EmoteSplitter.Splitting;

namespace TildeTools.Modules.EmoteSplitter;

[Serializable]
public sealed class EmoteSplitterSettings
{
    // Text positions are 16-bit signed and the game adds offsets to lengths, so stay under 32767. 32k seems like a nice safe number, and below the minimum the plugin is pointless anyway.
    public const int MaxUnlockBytes = 32000;

    public const int MinUnlockBytes = 512;

    // Leaves room for text after the largest margin and a splitting "/r ", which holds back space for the longest /tell.
    public const int MinChunkBytes = MaxSafetyMarginBytes + 3 + ReplyPin.HeaderAllowance + MinTextBytes;

    // The default chatbox's byte limit.
    // At 512, a line of 501 to 512 bytes might get past the submit hook unsplit, so capped at 500 just to be extra safe.
    public const int MaxChunkBytes = 500;

    public const int MaxSafetyMarginBytes = 64;

    public const int MinChunksPerMessage = 2;
    public const int MaxChunksPerMessageCeiling = 60;

    public bool UnlockChatInput { get; set; } = true;

    // Clamped in the setters too, for a hand-edited config that skips the UI. Thank you MidoriKami!
    public int UnlockedMaxBytes { get; set => field = Math.Clamp(value, MinUnlockBytes, MaxUnlockBytes); } = 8000;

    public int MaxBytesPerChunk { get; set => field = Math.Clamp(value, MinChunkBytes, MaxChunkBytes); } = MaxChunkBytes;

    public int SafetyMargin { get; set => field = Math.Clamp(value, 0, MaxSafetyMarginBytes); }

    internal int Budget => MaxBytesPerChunk - SafetyMargin;

    public const int MinTextBytes = 16;

    public bool PreferSentenceBreaks { get; set; } = true;

    public string ContinuationPrefix { get; set; } = string.Empty;

    public string ContinuationSuffix { get; set; } = string.Empty;

    public string FinalMarker { get; set; } = string.Empty;

    public List<ChunkMarker> Markers { get; set; } = [];

    public bool WrapOocPerPart { get; set; } = true;

    public string OocOpen { get; set; } = "((";

    public string OocClose { get; set; } = "))";

    // A macro's FC lines landed 166.7 ms apart, ten frames at 60 fps.
    // I know that technically this probably means higher framerates can post faster and that lower framerates could post slower but normalizing for 60 seems safe.
    // I could also get the client's framerate and calculate this to match to be EVEN SAFER but that seems excessive and pointless, so I'll just leave it for now...
    // If you're reading this and know better feel free to let me know.
    public const int MacroPaceMs = 167;

    public const int DefaultIntervalMs = 1500;

    public int IntervalMs { get; set => field = Math.Clamp(value, SendQueue.MinIntervalMs, SendQueue.MaxIntervalMs); } = DefaultIntervalMs;

    public int FreeIntervalMs { get; set => field = Math.Clamp(value, 0, SendQueue.MaxIntervalMs); } = MacroPaceMs;

    // Configurable post ceiling.
    public int MaxChunksPerMessage { get; set => field = Math.Clamp(value, MinChunksPerMessage, MaxChunksPerMessageCeiling); } = 20;

    public bool RetryOnThrottle { get; set; } = true;

    public bool ShowProgress { get; set; } = true;

    public SplitOptions ToSplitOptions() => new()
    {
        MaxBytes = MaxBytesPerChunk,
        SafetyMargin = SafetyMargin,
        PreferSentenceBreaks = PreferSentenceBreaks,
        ContinuationPrefix = ContinuationPrefix,
        ContinuationSuffix = ContinuationSuffix,
        FinalMarker = FinalMarker,
        Markers = Markers,
    };

    public string DetachOoc(string body, SplitOptions options)
    {
        if (!WrapOocPerPart || OocOpen.Length == 0 || OocClose.Length == 0)
            return body;

        var trimmed = body.Trim();
        if (trimmed.Length <= OocOpen.Length + OocClose.Length)
            return body;

        if (!trimmed.StartsWith(OocOpen, StringComparison.Ordinal) ||
            !trimmed.EndsWith(OocClose, StringComparison.Ordinal))
            return body;

        // Only for one block, lifting "(( a )) |n (( b ))" would give "(( a )) ))" and "(( (( b ))"
        var inner = trimmed[OocOpen.Length..^OocClose.Length];
        if (inner.Contains(OocClose, StringComparison.Ordinal))
            return body;

        options.IsOoc = true;
        options.OocOpen = OocOpen;
        options.OocClose = OocClose;

        return inner.Trim();
    }
}
