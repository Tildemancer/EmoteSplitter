using System;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using TildeTools.Modules.EmoteSplitter.Chat;
using TildeTools.Modules.EmoteSplitter.Sending;
using TildeTools.Modules.EmoteSplitter.Splitting;
using static TildeTools.Ui.Widgets;

namespace TildeTools.Modules.EmoteSplitter;

internal sealed class SettingsTab(EmoteSplitterSettings settings, Action onChanged)
{
    internal void Draw()
    {
        using var scroll = ImRaii.Child("##emote-splitter-scroll");
        if (!scroll.Success)
            return;

        using var wrap = ImRaii.TextWrapPos(0f);

        var dirty = DrawInputSection();
        ImGui.Separator();
        dirty |= DrawSplittingSection();
        ImGui.Separator();
        DrawManualBreaks();
        ImGui.Separator();
        dirty |= DrawMarkers();
        ImGui.Separator();
        dirty |= DrawOoc();
        ImGui.Separator();
        dirty |= DrawCustomMarkers();
        ImGui.Separator();
        DrawMarkerPreview(dirty);
        ImGui.Separator();
        dirty |= DrawPacingSection();
        ImGui.Separator();
        dirty |= DrawPostingSection();

        if (dirty)
            onChanged();
    }

    private bool DrawInputSection()
    {
        ImGui.TextUnformatted("Chat box");

        if (!InputCapManager.Available)
            ImGui.TextColored(WarningColor, "The game functions for this were not found on this game version.");

        var dirty = Toggle("Oversized Emotes", settings.UnlockChatInput, settings, static (s, v) => s.UnlockChatInput = v);

        dirty |= Paced("Chat limit (bytes)", ref _unlockBytes, settings.UnlockedMaxBytes,
            EmoteSplitterSettings.MinUnlockBytes, EmoteSplitterSettings.MaxUnlockBytes,
            settings, static (s, v) => s.UnlockedMaxBytes = v);

        ImGui.TextDisabled("How long the text input field is for the default vanilla chat editbox.");

        ImGui.TextDisabled("If you're looking for Chat 2's editbox, check its tab.");

        return dirty;
    }

    private bool DrawSplittingSection()
    {
        ImGui.TextUnformatted("Splitting");

        var dirty = Paced("Character Limit", ref _chunkBytes, settings.MaxBytesPerChunk,
            EmoteSplitterSettings.MinChunkBytes, EmoteSplitterSettings.MaxChunkBytes,
            settings, static (s, v) => s.MaxBytesPerChunk = v);

        ImGui.TextDisabled("The length Emote Splitter fits each part into. The game can only handle ~500 itself, so you can't " +
                           "go higher than that. Technically, this is in bytes, but for most purposes 'characters' is right.");

        dirty |= Paced("Safety margin", ref _margin, settings.SafetyMargin,
            0, EmoteSplitterSettings.MaxSafetyMarginBytes, settings, static (s, v) => s.SafetyMargin = v);

        ImGui.TextDisabled("Bytes held back from each part. Change this if posts start failing.");

        dirty |= Paced("Refuse beyond this many posts", ref _maxChunks, settings.MaxChunksPerMessage,
            EmoteSplitterSettings.MinChunksPerMessage, EmoteSplitterSettings.MaxChunksPerMessageCeiling,
            settings, static (s, v) => s.MaxChunksPerMessage = v);

        ImGui.TextDisabled("Anything longer than this won't post.");

        dirty |= Toggle("Break at sentence ends where possible", settings.PreferSentenceBreaks, settings, static (s, v) => s.PreferSentenceBreaks = v);

        return dirty;
    }

    private static void DrawManualBreaks()
    {
        ImGui.TextUnformatted("Manual breaks");
        ImGui.TextDisabled("|n - Splits the text manually.");
        ImGui.TextDisabled("|nn - Splits the text manually, but with no markers on the split paragraph.");
        ImGui.TextDisabled("|nb - Splits the text manually and without markers, but counts it as a part of the total after it's posted (not before).");
        ImGui.TextDisabled($"|n# - Splits the text manually, then waits # seconds (1-{SendQueue.MaxIntervalMs / 1000}) before " +
                           "the next post. Great for dramatic timing.");
    }

    private bool DrawMarkers()
    {
        ImGui.TextUnformatted("Continuation markers");
        ImGui.TextDisabled("#c - This paragraph's number.");
        ImGui.TextDisabled("#m - How many paragraphs there are.");
        ImGui.TextDisabled("#r - How many paragraphs are left.");
        ImGui.TextDisabled("Leave a box empty for no marker.");

        var dirty = Edit("Start of later parts", settings.ContinuationPrefix, 32, settings, static (s, v) => s.ContinuationPrefix = v);

        ImGui.TextDisabled("Opening marker on body paragraphs.");

        dirty |= Edit("End of earlier parts", settings.ContinuationSuffix, 32, settings, static (s, v) => s.ContinuationSuffix = v);

        ImGui.TextDisabled("Closing marker on all paragraphs except the final one.");

        dirty |= Edit("End of the last part", settings.FinalMarker, 32, settings, static (s, v) => s.FinalMarker = v);

        ImGui.TextDisabled("Exclusive markers for the final paragraph.");

        return dirty;
    }

    private bool DrawOoc()
    {
        ImGui.TextUnformatted("Out of character");

        var dirty = Toggle("Give every part its own OOC tags", settings.WrapOocPerPart, settings, static (s, v) => s.WrapOocPerPart = v);

        ImGui.TextDisabled("Type the tags once, around the whole message. Off, the first part opens them and the last closes them.");

        if (!settings.WrapOocPerPart)
            return dirty;

        using var width = ImRaii.ItemWidth(ImGui.GetFontSize() * 5f);
        dirty |= Edit("Opening tag", settings.OocOpen, 16, settings, static (s, v) => s.OocOpen = v);
        ImGui.SameLine();
        dirty |= Edit("Closing tag", settings.OocClose, 16, settings, static (s, v) => s.OocClose = v);

        return dirty;
    }

    private bool DrawCustomMarkers()
    {
        ImGui.TextUnformatted("Extra markers");
        ImGui.TextDisabled("Each carries its own placement and repetition.");

        var dirty = ImGui.Button("Add a marker");
        if (dirty)
            settings.Markers.Add(new ChunkMarker { Text = "#c/#m" });

        var (remove, close) = (-1, ImGui.GetContentRegionAvail().X - ImGui.GetFontSize());

        for (var i = 0; i < settings.Markers.Count; i++)
        {
            var marker = settings.Markers[i];

            using var id = ImRaii.PushId(i);

            var label = marker.Text.Length > 0 ? marker.Text : "(empty)";
            using var node = ImRaii.TreeNode($"{label}###marker");

            ImGui.SameLine(close);
            if (ImGui.SmallButton("x"))
                remove = i;

            if (node.Success)
                dirty |= DrawMarker(marker);
        }

        if (remove >= 0)
        {
            settings.Markers.RemoveAt(remove);
            dirty = true;
        }

        return dirty;
    }

    private static readonly string[] SlotNames =
    [
        "Outside the tags, at the start",
        "Inside the tags, at the start",
        "Inside the tags, at the end",
        "Outside the tags, at the end",
    ];

    private static readonly string[] RepeatNames =
    [
        "Every part",
        "Every part but the first",
        "Every part but the last",
        "The first part only",
        "The last part only",
        "Every nth part",
    ];

    private static bool DrawMarker(ChunkMarker marker)
    {
        var dirty = Edit("Text", marker.Text, 64, marker, static (m, v) => m.Text = v);
        dirty |= Choose("Where", SlotNames, (int)marker.Slot, marker, static (m, v) => m.Slot = (MarkerSlot)v);
        dirty |= Choose("On which parts", RepeatNames, (int)marker.Repeat, marker, static (m, v) => m.Repeat = (MarkerRepeat)v);

        if (marker.Repeat == MarkerRepeat.EveryNth)
        {
            dirty |= Slide("Once every", marker.Nth, 1, ChunkMarker.MaxNth, marker, static (m, v) => m.Nth = v);
            dirty |= Slide("Starting at part", marker.StartAt, 1, ChunkMarker.MaxNth, marker, static (m, v) => m.StartAt = v);
        }

        dirty |= Toggle("Out of character", marker.WhenOoc, marker, static (m, v) => m.WhenOoc = v);
        ImGui.SameLine();
        dirty |= Toggle("In character", marker.WhenNotOoc, marker, static (m, v) => m.WhenNotOoc = v);

        return dirty;
    }

    private string[]? _preview;

    // Built again only when something above it changed.
    private void DrawMarkerPreview(bool stale)
    {
        if (stale || _preview == null)
        {
            var options = settings.ToSplitOptions();

            // Called only for the OOC fields it sets on options.
            _ = settings.DetachOoc($"{settings.OocOpen} sample {settings.OocClose}", options);
            _preview = [.. Enumerable.Range(1, 3).Select(i => "   " + MessageSplitter.Preview("/say", $"...part {i} of your text...", i, 3, options))];
        }

        ImGui.TextUnformatted("Preview of a three-part message");

        foreach (var line in _preview)
            ImGui.TextDisabled(line);
    }

    // Paced's shown values, -1 until first drawn, see Widgets.Paced
    private int _unlockBytes = -1;
    private int _chunkBytes = -1;
    private int _margin = -1;
    private int _maxChunks = -1;
    private int _interval = -1;
    private int _freeInterval = -1;

    private bool DrawPacingSection()
    {
        ImGui.TextUnformatted("Pacing");

        var dirty = Paced("Delay on /s, /y, /sh, /t, /em, /n (ms)", ref _interval, settings.IntervalMs,
            SendQueue.MinIntervalMs, SendQueue.MaxIntervalMs, settings, static (s, v) => s.IntervalMs = v);

        ImGui.TextDisabled($"FFXIV rate-limits these channels. {SendQueue.MinIntervalMs} ms is the least; set it higher if parts go missing.");

        dirty |= Paced("Delay on /fc, /p, /a, /l#, /cwl# (ms)", ref _freeInterval, settings.FreeIntervalMs,
            0, SendQueue.MaxIntervalMs, settings, static (s, v) => s.FreeIntervalMs = v);

        ImGui.TextDisabled($"Macros post ~{EmoteSplitterSettings.MacroPaceMs} ms apart (or every 10 frames at 60 FPS).");

        if (settings.FreeIntervalMs < EmoteSplitterSettings.MacroPaceMs)
            ImGui.TextColored(WarningColor, "DANGER! This is faster than a macro would send, so it might be detectable Square-side! You set it " +
                                             "this low at your own risk.");

        return dirty;
    }

    private bool DrawPostingSection()
    {
        ImGui.TextUnformatted("While posting");

        var dirty = Toggle("Offer to resend on 'Your message was not heard...'", settings.RetryOnThrottle,
            settings, static (s, v) => s.RetryOnThrottle = v);

        ImGui.TextDisabled("If the game rejects a message, we ask to send it again.");

        dirty |= Toggle("Post Feedback", settings.ShowProgress, settings, static (s, v) => s.ShowProgress = v);

        return dirty;
    }
}
