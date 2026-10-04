using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using EmoteSplitter.Chat;
using EmoteSplitter.Sending;
using EmoteSplitter.Splitting;
using Chunks = System.Collections.Generic.IReadOnlyList<EmoteSplitter.Splitting.SplitPart>;
using InputCallbackResult = FFXIVClientStructs.FFXIV.Component.GUI.InputCallbackResult;

namespace EmoteSplitter;

internal sealed class EmoteSplitterModule : IDisposable
{
    private readonly Configuration _settings;
    private readonly Action _save;
    private readonly SendQueue _queue = new();
    private readonly ReplyPin _pin = new();

    private readonly SubmitInterceptor _submit;
    private readonly EnterInterceptor _enter;
    private readonly InputCapManager _inputCap;

    internal SettingsWindow Settings { get; }

    private static long NowMs => Environment.TickCount64;

    // Hooked up once, posting window included.
    internal EmoteSplitterModule(Configuration settings, Action save, WindowSystem windows)
    {
        _settings = settings;
        _save = save;
        Settings = new SettingsWindow(settings, OnSettingsChanged)
        {
            SizeConstraints = new() { MinimumSize = new(420, 400), MaximumSize = new(9999, 9999) },
        };
        windows.AddWindow(Settings);
        windows.AddWindow(new PostingWindow(_queue, _pin, Stop));

        _queue.Sender = ChatSender.Send;
        _queue.Rewrite = _pin.Rewrite;
        // GPose sets WatchingCutscene, so InWorld is false there.
        // C2's GposeActive reads the same flag, so I do it that way too. Thanks Infi
        // Chat posts in GPose too, though.
        _queue.CanSend = () => Svc.InWorld || Svc.ClientState.IsGPosing;
        _queue.Progress += OnProgress;
        _queue.Finished += OnFinished;
        _queue.SendFailed += OnSendFailed;
        _queue.Suspected += OnSuspected;
        _queue.LineSent += _pin.Sent;
        _queue.MessageStarting += _pin.Reset;

        _queue.IntervalMs = _settings.IntervalMs;
        _queue.FreeIntervalMs = _settings.FreeIntervalMs;

        // ChatMessage fires first for every line, handled later or not, ergo we see the sender before another plugin writes a rename back.
        Svc.Chat.ChatMessage += OnChatMessage;
        Svc.Chat.LogMessage += OnLogMessage;
        Svc.ClientState.Logout += OnLogout;
        Svc.ClientState.Login += OnLogin;

        _submit = new SubmitInterceptor(_settings, (header, body) => OnMessageNeedsSplitting(header, body) != null, OnPlayerLine);
        _enter = new EnterInterceptor(OnEnteredLine);
        _inputCap = new InputCapManager(_settings, _enter.TryHook);

        Svc.Framework.Update += OnFirstUpdate;
        Svc.Framework.Update += OnFrameworkUpdate;

        // Warmed up off-thread.
        // Compiling the splitter on the first split costs about 25 ms on the frame of the first long paste and stutters make me :(
        Task.Run(WarmUp);

        if (!InputCapManager.Available && _settings.UnlockChatInput)
            Svc.Chat.PrintError("[Emote Splitter] The chat box's length limit could not be raised for this game version. Splitting still works if you paste into the box.");
    }

    // Runs off the game's thread, so no game calls (no Sanitize, no channel pin), just the pure path they feed.
    private void WarmUp()
    {
        var line = "/s " + string.Join(' ', Enumerable.Repeat("A warm-up sentence, long enough to need splitting.", 40));

        try
        {
            ChannelCommands.TrySplittable(line, out var header, out var body);

            var options = _settings.ToSplitOptions();
            MessageSplitter.SplitWithBodies(header, _settings.DetachOoc(body, options), options);
        }
        catch (Exception ex)
        {
            Svc.Log.Debug(ex, "Splitter warm-up failed; the first split just runs cold.");
        }
    }

    public void Dispose()
    {
        Svc.Framework.Update -= OnFirstUpdate;
        Svc.Framework.Update -= OnFrameworkUpdate;

        DropBatch();

        Svc.Chat.ChatMessage -= OnChatMessage;
        Svc.Chat.LogMessage -= OnLogMessage;
        Svc.ClientState.Logout -= OnLogout;
        Svc.ClientState.Login -= OnLogin;

        // Reverse of the constructor
        _inputCap.Dispose();
        _enter.Dispose();
        _submit.Dispose();
    }

    private static int[] PausesOf(Chunks chunks) =>
        [.. chunks.Select(chunk => Math.Min(chunk.Pause, SendQueue.MaxIntervalMs / 1000) * 1000)];

    private const string PayloadRefusal =
        "That message has to be split, and it contains an auto-translate phrase or item link, " +
        "which splitting would corrupt. Nothing was sent.";

    private const string UnsplittableRefusal =
        "That message is too long for the game and this isn't a recognized channel. Nothing was sent.";

    private const string ItemRefusal =
        "The split message you just tried to send would try to put the <item> you linked past the first post, " +
        "which is impossible. Nothing was sent.";

    private bool TrySplit(ref string header, string body, out Chunks chunks, out string? reason)
    {
        (chunks, reason) = ([], null);

        // A /r goes to whoever last sent a tell, even from before a restart.
        // If we know who that is, the parts go to them as tells;
        // If not, ReplyPin tries to work it out from part 1's echo.
        // A bare line gets pinned to the box's channel so switching channels later can't move it.
        if (ReplyPin.IsReplyHeader(header) && _replyTo is { } to)
            header = $"/tell {to}";

        if (!ActiveChannel.TryPin(ref header))
        {
            reason = ActiveChannel.Unreadable;
            return false;
        }

        var options = _settings.ToSplitOptions();

        // Leaves room for the /tell Name@World that every part after the first turns into.
        options.SafetyMargin += ReplyPin.IsReplyHeader(header) ? ReplyPin.HeaderAllowance : 0;

        try
        {
            // Sanitize first so the byte count matches what gets sent.
            chunks = MessageSplitter.SplitWithBodies(header, _settings.DetachOoc(ChatSender.Sanitize(body), options), options);
        }
        catch (SplitBudgetException ex)
        {
            reason = $"Could not split that message: {ex.Message}";
            return false;
        }

        if (chunks.Count == 0)
        {
            reason = "That message is only break markers, with no text to send. Nothing was sent.";
            return false;
        }

        if (chunks.Count > _settings.MaxChunksPerMessage)
            reason = $"That message needs {chunks.Count} parts, over the limit of {_settings.MaxChunksPerMessage}. " +
                     "Nothing was sent. Raise the limit in /splitter if you meant it.";
        // <item> is only held for a single post.
        // I had a workaround for this in TildeTools but nothing called it anymore.
        // Also, it'd need two write into a game struct for every part to implement again, though, and that's been spooky elsewhere. It's on the table for suggestions though.
        else if (chunks.Skip(1).Any(chunk => chunk.Line.Contains("<item>", StringComparison.Ordinal)))
            reason = ItemRefusal;
        else
            return true;

        chunks = [];
        return false;
    }

    private static void Refuse(string reason)
    {
        Svc.Log.Info($"Refused: {reason}");
        Svc.Chat.PrintError($"[Emote Splitter] {reason}");
    }

    private void Queue(string header, Chunks chunks, bool ahead, bool typed)
    {
        var channel = ChannelCommands.KeyOf(header);
        Svc.Log.Info($"Queued {chunks.Count} chunk(s), channel \"{channel}\", ahead {ahead}, typed {typed}.");
        _queue.Enqueue(chunks.Select(c => c.Line), channel, ahead, PausesOf(chunks), NowMs, typed);

        if (_settings.ShowProgress && chunks.Count > 1)
            Svc.Chat.Print($"[Emote Splitter] Sending {chunks.Count} parts...");
    }

    // Can't cut in front of a /r or an open question! /r resets its pin on the new message so you'd end up sending all your spicy text to like, IDK, your FC lead or something.
    private bool CanCutIn =>
        _queue.State != SendQueueState.Asking && _queue.Underway != ChannelCommands.Reply;

    private string? CantWait(string header, Chunks chunks, bool ahead, bool typed)
    {
        // Part 1's <item> is filled in as it posts, so it has the usual waits
        var why = ReplyPin.IsReplyHeader(header) ? ReplyCantWait
            : chunks[0].Line.Contains("<item>", StringComparison.Ordinal) ? ItemCantWait
            : null;

        if (why == null)
            return null;

        if (!_queue.CanSend())
            return why + "for a loading screen or cutscene to end. Nothing was sent. Send it again once it has.";

        if (chunks[0].Pause > 0)
            return why + "out a pause at its start. Nothing was sent. Send it without one.";

        return _queue.GoesNext(ChannelCommands.KeyOf(header), ahead, typed)
            ? null
            : why + "behind another message. Nothing was sent. Send it again once the Emote Splitter window has closed.";
    }

    private const string ReplyCantWait =
        "A reply goes to whoever last sent you a tell when its first part is posted, so it can't wait ";

    private const string ItemCantWait =
        "The game fills in an item link as its part is posted, so it can't wait ";

    internal void Stop() => Svc.Chat.Print($"[Emote Splitter] Stopped; {DropBatch()} part(s) not sent.");

    private void OnSettingsChanged()
    {
        _queue.IntervalMs = _settings.IntervalMs;
        _queue.FreeIntervalMs = _settings.FreeIntervalMs;
        _save();
        _inputCap.Apply();
    }

    private InputCallbackResult? OnEnteredLine(string line, byte[] raw)
    {
        // The return null below still hands the Enter to the game, which closes the box.
        if (line.Length == 0 && _queue.AwaitingGo)
            _queue.Go();

        // The editbox measures in raw bytes. better safe than sorry.
        var bytes = raw.Length - 1;
        // Less the 0 raw ends in, which Lumina reads as a broken payload.
        var payload = ChatSender.HasPayload(raw.AsSpan(..^1));
        var splittable = ChannelCommands.TrySplittable(line, out var header, out var body);

        // Also takes one that fits but has a break marker, so a refusal can say why and keep it in the box.
        // Not with a link or auto-translate phrase, since the game sends those in one piece and taking the line would drop them...
        // Anything not split is a typed line, and the send hook never sees the box's own, so the hold check runs here.
        if (bytes <= _settings.Budget && (payload || !splittable || MessageSplitter.FindBreak(body).At < 0))
        {
            return OnPlayerLine(line, saveToHistory: true, payload) ? InputCallbackResult.ClearText : null;
        }

        Svc.Log.Info($"Enter on a line to split: {bytes} bytes, budget {_settings.Budget}.");

        // If it's not handed back the game will take and drop it silently. Incredible.
        if (payload || (!splittable && bytes > Configuration.MaxChunkBytes))
        {
            Refuse(payload ? PayloadRefusal : UnsplittableRefusal);
            return InputCallbackResult.None;
        }

        if (!splittable)
        {
            Svc.Log.Info("Leaving it alone: not a chat channel.");
            return null;
        }

        return OnMessageNeedsSplitting(header, body, raw);
    }

    // Any line the player sends mid-post takes the place of the next scheduled post instead of posting immediately to avoid 'Your message was not heard' nonsense.
    private bool OnPlayerLine(string line, bool saveToHistory, bool payload)
    {
        if (!ChannelCommands.TrySplittable(line, out var header, out var body))
        {
            // A tell whose target isn't a name, like /t <t>, still shares the wait.
            if (ChannelCommands.IsTell(line))
                _queue.Typed(line, ChannelCommands.Tell, NowMs, canHold: false);

            return false;
        }

        // A held line goes where it would go if it was sent now. Except in the cases below, anyway.
        if (ReplyPin.IsReplyHeader(header) && _replyTo is { } to)
            header = $"/tell {to}";

        // Only pin bare lines flagged to save history, as the chatbox's own are (OnEnteredLine passes it by hand)
        // Compatibility notes;
        // XIM sends bare lines around a tell target it sets itself.
        // ExtraChat's channels pin to nothing, and a held bare line would go wherever the box is pointed to at that moment.
        if (header.Length == 0 && saveToHistory)
            ActiveChannel.TryPin(ref header);

        var pinned = header.Length > 0 && !ReplyPin.IsReplyHeader(header);

        var channel = ChannelCommands.KeyOf(header);
        if (ChannelCommands.Unlimited(channel))
            return false;

        // A link's bytes and an <item> wouldn't survive a later send, so lines with them aren't held.
        // Same with ones the added command pushes past 500 bytes.
        var held = header.Length > 0 ? $"{header} {body}" : body;
        var canHold = pinned && !payload
            && !line.Contains("<item>", StringComparison.Ordinal)
            && Encoding.UTF8.GetByteCount(held) <= Configuration.MaxChunkBytes;

        if (!_queue.Typed(held, channel, NowMs, canHold))
            return false;

        Svc.Log.Info($"Held a line typed mid-post to go next, channel \"{channel}\".");
        return true;
    }

    // raw is null when another plugin sent it.
    private InputCallbackResult? OnMessageNeedsSplitting(string header, string body, byte[]? raw = null)
    {
        var whole = Encoding.UTF8.GetByteCount(header.Length > 0 ? $"{header} {body}" : body);
        var fits = whole <= _settings.Budget;

        var ahead = CanCutIn;
        if (TrySplit(ref header, body, out var chunks, out var reason)
            && (reason = CantWait(header, chunks, ahead, fits)) == null)
        {
            Queue(header, chunks, ahead, fits);
            return InputCallbackResult.ClearText;
        }

        // Another plugin's line can't be kept in its box since raw is null, so; if it fits, it sits -- I mean, it goes unsplit.
        if (raw == null && whole <= Configuration.MaxChunkBytes)
        {
            Svc.Log.Info($"Sent whole: {reason}");
            return null;
        }

        Refuse(reason!);
        return InputCallbackResult.None;
    }

    // Dalamud runs the constructor on its load thread, and these two touch the chat box.
    private void OnFirstUpdate(IFramework framework)
    {
        Svc.Framework.Update -= OnFirstUpdate;

        _enter.TryHook();
        _inputCap.Apply();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        DropUnconfirmedReply();
        _queue.Update(NowMs);

        if (_finished is { } done && _queue.State == SendQueueState.Idle && NowMs - done.At > SendQueue.ThrottleClaimWindowMs)
        {
            EndBatch();

            if (done.Say)
                Svc.Chat.Print("[Emote Splitter] Message sent.");
        }
    }

    // Every way a batch can end goes through here, that way the pin always gets released with it.
    private void EndBatch()
    {
        _pin.Reset();
        _finished = null;
    }

    private int _droppedAtLogout;

    // What's left could conceivably post from whichever character logs in next, pinned /tell and all. No bueno
    private void OnLogout(int type, int code)
    {
        _droppedAtLogout += DropBatch();
        _replyTo = null;
    }

    // Reported on login, since anything printed at logout should be gone before anyone can read it.
    private void OnLogin()
    {
        if (_droppedAtLogout == 0)
            return;

        Svc.Chat.Print($"[Emote Splitter] You logged out mid-post, so {_droppedAtLogout} part(s) weren't sent.");
        _droppedAtLogout = 0;
    }

    private int DropBatch()
    {
        var dropped = _queue.PendingCount;
        _queue.Cancel();
        EndBatch();
        return dropped;
    }

    // Drops the message being sent, not the ones queued behind it.
    // Reset the pin now, or TimedOut will drop the next message too.
    private int DropMessage()
    {
        var dropped = _queue.DropCurrentMessage();

        if (_queue.PendingCount == 0)
            EndBatch();
        else
            _pin.Reset();

        return dropped;
    }

    private void OnProgress(int part, int of)
    {
        Svc.Log.Info($"Sent chunk {part}/{of}.");

        // Reset on a reply's last part so a /r queued behind it doesn't show as going to the same person.
        if (part == of)
            _pin.Reset();
    }

    private (long At, bool Say)? _finished;

    // "Not heard" can still ask about the last part for ThrottleClaimWindowMs, see OnFrameworkUpdate
    // If a typed line went last, keep the Say that the post's own Finished set.
    private void OnFinished() =>
        _finished = (NowMs, _settings.ShowProgress && (!_queue.LastSentWasTyped || _finished?.Say == true));

    private void OnSendFailed(Exception ex)
    {
        EndBatch();
        Svc.Log.Error(ex, "Send failed; the rest of the message was dropped.");
        Svc.Chat.PrintError($"[Emote Splitter] Send failed: {ex.Message}");
    }

    private void OnSuspected(string line)
    {
        _pin.Throttled(line);

        Svc.Log.Warning("Throttle notice right after a part; paused to ask.");
        Svc.Chat.Print("[Emote Splitter] Paused. The game says a message wasn't heard, so the Emote Splitter window asks whether to post the last part again.");
    }

    private void OnLogMessage(ILogMessage message)
    {
        if (message.LogMessageId == LogMessages.Throttled && _settings.RetryOnThrottle)
            _queue.ReportThrottled(NowMs);
        else if (LogMessages.Fatal.Contains(message.LogMessageId))
            OnFatalRejection(message.LogMessageId);
    }

    private void OnFatalRejection(uint logMessageId)
    {
        // Current is null when the notice is about a message that finished sending.
        // Its last part might be the one that got refused, so no "Message sent." for it.
        if (_queue.Current is not { } current)
        {
            if (_finished is { } done)
                _finished = (done.At, false);

            return;
        }

        // Only ours if the message being posted goes to the notice's channel.
        // "" could be any of them, see MightShare
        if (!LogMessages.About(logMessageId, current))
            return;

        var dropped = DropMessage();
        Svc.Log.Warning($"Chat refused (LogMessage {logMessageId}); dropped {dropped} queued chunk(s).");
        Svc.Chat.PrintError(
            $"[Emote Splitter] The game refused the message, so the remaining {dropped} part(s) " +
            "were not sent. See the error above this line for the reason.");
    }

    private string? _replyTo;

    // An outgoing tell's sender field holds the recipient.
    // See XIM's DecodeSender
    private void OnChatMessage(IChatMessage message)
    {
        var kind = message.LogKind;

        if (kind is not (XivChatType.TellOutgoing or XivChatType.TellIncoming))
            return;

        string? person = null;

        try
        {
            // Reads OriginalSender, like the echo does.
            // A plugin that renames senders can drop PlayerPayload
            if (message.OriginalSender.ToDalamudString().Payloads.OfType<PlayerPayload>().FirstOrDefault() is { } player)
            {
                var world = player.World.ValueNullable?.Name.ToString();
                if (string.IsNullOrEmpty(world))
                    Svc.Log.Warning($"A tell named no world (row {player.World.RowId}).");
                else
                    person = $"{player.PlayerName}@{world}";
            }

            // An incoming tell we can't read still moved /r, so who it goes to is unknown until the next tell or an echo...
            if (kind == XivChatType.TellIncoming)
                _replyTo = person;
            else if (person != null && _pin.Echoed(person, message.OriginalMessage.ExtractText()))
            {
                Svc.Log.Info("Reply batch pinned to its recipient.");
                _replyTo = person;
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Could not read who a tell was with.");

            if (kind == XivChatType.TellIncoming)
                _replyTo = null;
        }
    }

    // No echo means they're offline or there's nobody to reply to.
    // The rest of the parts could land on anyone.
    private void DropUnconfirmedReply()
    {
        if (!_pin.TimedOut(NowMs))
            return;

        var dropped = DropMessage();

        Svc.Log.Warning($"No echo for the first /r part; dropped {dropped} queued chunk(s).");
        Svc.Chat.PrintError(
            $"[Emote Splitter] Could not confirm who the reply went to, so the remaining {dropped} " +
            "part(s) were not sent. Use /tell Name@World to send them.");
    }
}
