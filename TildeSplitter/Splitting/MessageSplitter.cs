using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace TildeTools.Modules.EmoteSplitter.Splitting;

public sealed class SplitBudgetException(string message) : Exception(message);

public readonly record struct SplitPart(string Line, int BodyStart, int BodyLength, int Pause);

// What a break marker does to the text after it, up to the next marker:
// |n = new part
// |nn = bare (no markers) and outside the count
// |nb = bare but counted after it posts
public enum BreakKind
{
    Part,
    Bare,
    Counted,
}

// Measures in UTF-8 bytes and never cuts inside a visible character like emoji or accented letters.
public static class MessageSplitter
{
    // The player's own break, see FindBreak
    public const string BreakMarker = "|n";

    private const int MaxFixpointPasses = 10;

    private const string SentenceTerminators = ".!?\u2026";

    // Can trail terminators, i.e. "Stop." <--
    private const string SentenceTrailers = "\"'\u2019\u201d\u00bb)]}";

    // Stand-ins for the markers once found, one per BreakKind, in its order.
    private const string Sentinels = "\u001F\u001D\u001E";

    public static IReadOnlyList<SplitPart> SplitWithBodies(string header, string body, SplitOptions options)
    {
        header = header.Trim();
        var (marked, pauses) = MarkBreaks(body);
        var markers = EffectiveMarkers(options);

        // Marker width depends on the part count and vice versa, so iterate to a fixpoint
        var bodies = new List<(string Text, BreakKind Kind, int Pause)>();
        var assumedTotal = 1;
        for (var pass = 0; pass < MaxFixpointPasses; pass++)
        {
            bodies = SplitBody(header, marked, assumedTotal, options, markers, pauses);
            var counted = bodies.Count(b => b.Kind != BreakKind.Bare);
            if (counted <= assumedTotal)
                break;
            assumedTotal = counted;
        }

        // Checked against what's left after the margin, or a /r part could pass here and outgrow 500 once rewritten.
        var limit = options.MaxBytes - options.SafetyMargin;

        // A part's number and total include the |nb items before it, but not the bare ones.
        var parts = bodies.Count(b => b.Kind == BreakKind.Part);
        var index = 0;
        var countedSoFar = 0;

        var lines = new List<SplitPart>(bodies.Count);
        for (var i = 0; i < bodies.Count; i++)
        {
            if (bodies[i].Kind == BreakKind.Part)
                index++;
            else if (bodies[i].Kind == BreakKind.Counted)
                countedSoFar++;

            var (prefix, suffix) = Affixes(header, index + countedSoFar, parts + countedSoFar, options, bodies[i].Kind == BreakKind.Part ? markers : []);
            var line = prefix + bodies[i].Text + suffix;

            var bytes = Encoding.UTF8.GetByteCount(line);
            if (bytes > limit)
                throw new SplitBudgetException(
                    $"Part {i + 1}/{bodies.Count} came out at {bytes} bytes, over the {limit}-byte limit. " +
                    "This usually means the header or continuation marker is too long.");

            lines.Add(new(line, prefix.Length, bodies[i].Text.Length, bodies[i].Pause));
        }

        return lines;
    }

    // Normalizes newlines and swaps each marker for its kind's stand-in, keeping the pauses in order.
    private static (string Body, List<int> Pauses) MarkBreaks(string body)
    {
        body = body.Replace("\r\n", "\n").Replace('\r', '\n');

        // Typed or pasted stand-in characters would break like markers, and run past the end of the pauses below.
        foreach (var sentinel in Sentinels)
            body = body.Replace(sentinel, ' ');

        var pauses = new List<int>();
        for (var mark = FindBreak(body); mark.At >= 0; mark = FindBreak(body, mark.At + 1))
        {
            body = body[..mark.At] + Sentinels[(int)mark.Kind] + body[(mark.At + mark.Length)..];
            pauses.Add(mark.Seconds);
        }

        return (body, pauses);
    }

    private static int Overhead(string header, int index, int total, SplitOptions o, IReadOnlyList<ChunkMarker> markers)
    {
        var (prefix, suffix) = Affixes(header, index, total, o, markers);
        return Encoding.UTF8.GetByteCount(prefix) + Encoding.UTF8.GetByteCount(suffix);
    }

    public static string Preview(string header, string body, int index, int total, SplitOptions options)
    {
        var (prefix, suffix) = Affixes(header.Trim(), index, total, options, EffectiveMarkers(options));
        return prefix + body + suffix;
    }

    private static List<(string Text, BreakKind Kind, int Pause)> SplitBody(
        string header, string text, int assumedTotal, SplitOptions o, IReadOnlyList<ChunkMarker> markers, List<int> pauses)
    {
        var result = new List<(string Text, BreakKind Kind, int Pause)>();
        var kind = BreakKind.Part;
        var (counted, pos, crossed, pause) = (0, 0, 0, 0);

        while (pos < text.Length)
        {
            // A marker sets what the text after it is, up to the next one.
            // Its pause goes on the next part, the longest one, if several, share a gap.
            for (; pos < text.Length && IsSkippable(text[pos]); pos++)
                if (Sentinels.IndexOf(text[pos]) is var found and >= 0)
                    (kind, pause) = ((BreakKind)found, Math.Max(pause, pauses[crossed++]));

            if (pos >= text.Length)
                break;

            // Leave room for whichever it turns out to be.
            // The count can end up at fewer parts than assumed, where a longer last marker would overflow.
            var index = counted + 1;
            var overhead = kind == BreakKind.Part
                ? Math.Max(Overhead(header, index, assumedTotal, o, markers), Overhead(header, index, index, o, markers))
                : Overhead(header, 0, 0, o, []);
            var available = o.MaxBytes - o.SafetyMargin;
            var budget = available - overhead;

            if (budget < 1)
                throw new SplitBudgetException($"No room left for text: the header and markers already use {overhead} of the {available} bytes.");

            var length = TakeChunkLength(text, pos, budget, o);

            // So a part never goes out as a command, like /shrug say
            // With a header, a slash in the body is just text.
            if (header.Length == 0)
                length = PullBackFromLeadingSlash(text, pos, length);

            var piece = text.Substring(pos, length).Trim();

            if (piece.Length > 0)
            {
                result.Add((piece, kind, pause));
                pause = 0;

                if (kind != BreakKind.Bare)
                    counted++;
            }

            pos += length;
        }

        return result;
    }

    private static int TakeChunkLength(string text, int start, int budget, SplitOptions o)
    {
        var bytes = 0;
        var lastSpace = -1;
        var lastSentence = -1;
        var i = start;

        while (i < text.Length)
        {
            if (text[i] == '\n' || Sentinels.Contains(text[i]))
                return i - start;

            if (text[i] == ' ')
            {
                // Stops before the space so the break eats it, and so the space doesn't have to fit.
                lastSpace = i - start;
                if (IsSentenceEnd(text, i - 1))
                    lastSentence = i - start;
            }

            var elementLength = StringInfo.GetNextTextElementLength(text.AsSpan(i));
            var elementBytes = Encoding.UTF8.GetByteCount(text.AsSpan(i, elementLength));

            if (bytes + elementBytes > budget)
                break;

            bytes += elementBytes;
            i += elementLength;
        }

        return i >= text.Length ? text.Length - start
            : o.PreferSentenceBreaks && lastSentence > 0 ? lastSentence
            : lastSpace > 0 ? lastSpace
            : i > start ? i - start
            : throw new SplitBudgetException($"A single character needs more than the {budget} bytes left in a part.");
    }

    private static int PullBackFromLeadingSlash(string text, int start, int length)
    {
        var candidate = length;

        while (true)
        {
            // After a newline or marker the player put in, the slash starts its own line.
            var next = start + candidate;
            for (; next < text.Length && IsSkippable(text[next]); next++)
                if (text[next] == '\n' || Sentinels.Contains(text[next]))
                    return candidate;

            if (next >= text.Length || text[next] != '/')
                return candidate;

            var moved = text.LastIndexOf(' ', start + candidate - 1, candidate - 1) - start;

            if (moved <= 0)
                return length;

            candidate = moved;
        }
    }

    private static bool IsSentenceEnd(string text, int index)
    {
        while (index >= 0 && SentenceTrailers.Contains(text[index]))
            index--;

        return index >= 0 && SentenceTerminators.Contains(text[index]);
    }

    // Measuring uses this too, so the two can't drift apart.
    private static (string Prefix, string Suffix) Affixes(string header, int index, int total, SplitOptions o, IReadOnlyList<ChunkMarker> markers)
    {
        var (prefix, suffix) = (new StringBuilder(), new StringBuilder());

        if (header.Length > 0)
            prefix.Append(header).Append(' ');

        Append(MarkerSlot.BeforeOoc);

        if (o.OocOpen.Length > 0)
            prefix.Append(o.OocOpen).Append(' ');

        Append(MarkerSlot.BeforeBody);
        Append(MarkerSlot.AfterBody);

        if (o.OocClose.Length > 0)
            suffix.Append(' ').Append(o.OocClose);

        Append(MarkerSlot.AfterOoc);

        return (prefix.ToString(), suffix.ToString());

        void Append(MarkerSlot slot)
        {
            foreach (var marker in markers)
            {
                if (marker.Slot != slot || !marker.AppliesAt(index, total, o.IsOoc))
                    continue;

                if (slot is MarkerSlot.BeforeOoc or MarkerSlot.BeforeBody)
                    prefix.Append(marker.Render(index, total)).Append(' ');
                else
                    suffix.Append(' ').Append(marker.Render(index, total));
            }
        }
    }

    private static List<ChunkMarker> EffectiveMarkers(SplitOptions o)
    {
        var markers = new List<ChunkMarker>(o.Markers);

        // Outside the OOC tags, which wrap the spoken text only.
        Add(o.ContinuationPrefix, MarkerSlot.BeforeOoc, MarkerRepeat.AllExceptFirst);
        Add(o.ContinuationSuffix, MarkerSlot.AfterOoc, MarkerRepeat.AllExceptLast);
        Add(o.FinalMarker, MarkerSlot.AfterOoc, MarkerRepeat.OnlyOnLast);

        return markers;

        void Add(string text, MarkerSlot slot, MarkerRepeat repeat)
        {
            if (text.Length > 0)
                markers.Add(new ChunkMarker { Text = text, Slot = slot, Repeat = repeat });
        }
    }

    // The player's break is the marker, then n or b for the kind, then a pause in seconds, like "|n", "|nn" or "|nb3"
    // It has to stand alone between whitespace or at either end, so "and|nor" doesn't count
    // A pause too long for an int comes back as int.MaxValue for the caller to cap
    // anywhere: glued to other text too, like "((|n3" before the OOC tags come off.
    public static (int At, int Length, BreakKind Kind, int Seconds) FindBreak(string text, int from = 0, bool anywhere = false)
    {
        for (var at = text.IndexOf(BreakMarker, from, StringComparison.Ordinal); at >= 0; at = text.IndexOf(BreakMarker, at + 1, StringComparison.Ordinal))
        {
            var digits = at + BreakMarker.Length;
            var kind = digits < text.Length ? text[digits] switch { 'n' => BreakKind.Bare, 'b' => BreakKind.Counted, _ => BreakKind.Part } : BreakKind.Part;

            if (kind != BreakKind.Part)
                digits++;

            var end = digits;
            while (end < text.Length && char.IsAsciiDigit(text[end]))
                end++;

            if (anywhere || (at == 0 || char.IsWhiteSpace(text[at - 1])) && (end == text.Length || char.IsWhiteSpace(text[end])))
                return (at, end - at, kind, end == digits ? 0 : int.TryParse(text.AsSpan(digits, end - digits), out var seconds) ? seconds : int.MaxValue);
        }

        return (-1, 0, BreakKind.Part, 0);
    }

    private static bool IsSkippable(char c) => c == ' ' || c == '\n' || c == '\t' || Sentinels.Contains(c);
}
