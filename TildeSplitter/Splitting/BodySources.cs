using System;
using System.Collections.Generic;

namespace TildeTools.Modules.EmoteSplitter.Splitting;

// Where each part's body sits in the typed line.
// Headers, markers and trimming move a part's own offsets off the clicked word.
public static class BodySources
{
    // Searched for, not summed, because trimming and the OOC lift shift things unpredictably.
    // Empty when any body can't be found.
    // `from` skips the channel command, or "/say say say" would match inside the header.
    // A match can't overlap part of a break marker, or in "|n3 3" the body "3" would be found inside the marker "|n3"
    // Covering one completely is fine, "and|nor" is just a body's own text.
    public static List<int> Locate(string source, IReadOnlyList<SplitPart> parts, int from = 0)
    {
        var marks = new List<(int At, int Length)>();
        for (var mark = MessageSplitter.FindBreak(source, from, anywhere: true); mark.At >= 0;
             mark = MessageSplitter.FindBreak(source, mark.At + 1, anywhere: true))
            marks.Add((mark.At, mark.Length));

        var starts = new List<int>(parts.Count);
        var pos = from;

        foreach (var part in parts)
        {
            var body = part.Line.Substring(part.BodyStart, part.BodyLength);

            var at = source.IndexOf(body, pos, StringComparison.Ordinal);
            while (at >= 0 && Cuts(marks, at, body.Length))
                at = source.IndexOf(body, at + 1, StringComparison.Ordinal);

            if (at < 0)
                return [];

            starts.Add(at);
            pos = at + body.Length;
        }

        return starts;
    }

    private static bool Cuts(List<(int At, int Length)> marks, int at, int length)
    {
        foreach (var (from, size) in marks)
            if (from < at + length && at < from + size && (from < at || from + size > at + length))
                return true;

        return false;
    }
}
