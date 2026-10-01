using System;
using System.Globalization;

namespace TildeTools.Modules.EmoteSplitter.Splitting;

public enum MarkerSlot
{
    // /say HERE (( text ))
    BeforeOoc,

    // /say (( HERE text ))
    BeforeBody,

    // /say (( text HERE ))
    AfterBody,

    // /say (( text )) HERE
    AfterOoc,
}

public enum MarkerRepeat
{
    All,
    AllExceptFirst,
    AllExceptLast,
    OnlyOnFirst,
    OnlyOnLast,
    EveryNth,
}

[Serializable]
public sealed class ChunkMarker
{
    public string Text { get; set; } = string.Empty;

    // Checked because a hand-edited config's out-of-range value would index past the settings tab's names. Thanks MidoriKami
    public MarkerSlot Slot { get; set => field = Enum.IsDefined(value) ? value : MarkerSlot.AfterOoc; } = MarkerSlot.AfterOoc;

    public MarkerRepeat Repeat { get; set => field = Enum.IsDefined(value) ? value : MarkerRepeat.All; } = MarkerRepeat.All;

    public const int MaxNth = 10;

    public int Nth { get; set => field = Math.Clamp(value, 1, MaxNth); } = 1;

    public int StartAt { get; set => field = Math.Clamp(value, 1, MaxNth); } = 1;

    public bool WhenOoc { get; set; } = true;

    public bool WhenNotOoc { get; set; } = true;

    // index is 1-based
    public bool AppliesAt(int index, int total, bool ooc) =>
        Text.Length > 0 && total > 1 && (ooc ? WhenOoc : WhenNotOoc) && Repeat switch
        {
            MarkerRepeat.All => true,
            MarkerRepeat.AllExceptFirst => index > 1,
            MarkerRepeat.AllExceptLast => index < total,
            MarkerRepeat.OnlyOnFirst => index == 1,
            MarkerRepeat.OnlyOnLast => index == total,
            MarkerRepeat.EveryNth => index >= StartAt && (index - StartAt) % Nth == 0,
            _ => false,
        };

    // WS's placeholders, in either case (#c this part, #m the count, #r how many are left)
    // #r never goes below 0, because SplitBody measures an index past assumedTotal until the count settles.
    public string Render(int index, int total) =>
        Text.Replace("#c", index.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("#m", total.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("#r", Math.Max(0, total - index).ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
}
