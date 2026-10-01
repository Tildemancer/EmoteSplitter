using System;
using System.Collections.Generic;
using System.Linq;
using TildeTools.Modules.EmoteSplitter.Chat;
using Part = (string Line, int Part, int Of, int WaitMs);

namespace TildeTools.Modules.EmoteSplitter.Sending;

public enum SendQueueState
{
    Idle,
    Sending,
    Held,

    // A suspect is set and nothing goes out until Resume.
    Asking,
}

public sealed class SendQueue
{
    // Part and Of count within the message
    // WaitMs is a pause the player asked for via |n#, counted from PausedFrom (when it was queued, then when the message's last part went)
    // So, a typed line or cut-in in between shouldn't stretch it.
    private sealed class Message(string channel, List<Part> parts)
    {
        public readonly string Channel = channel;
        public readonly List<Part> Parts = parts;
        public bool Started;
        public bool Typed;
        public long PausedFrom;
    }

    private readonly List<Message> _messages = [];
    private long _heldUntil;

    private Message? _current;

    private (Message From, string Line, int Part, int Of)? _lastSent, _suspect;
    private long _lastSentAt = -MaxIntervalMs;
    private long _lastPartAt = -MaxIntervalMs;
    private long _lastTypedAt = -MaxIntervalMs;
    private bool _queueWentLast;

    public const int ThrottleClaimWindowMs = 3000;

    // ProcessChatBoxEntry skips the game's rate limit, so this is a custom floor to be safe.
    public const int MinIntervalMs = 1500;
    public const int MaxIntervalMs = 30000;

    public int IntervalMs { get; set => field = Math.Clamp(value, MinIntervalMs, MaxIntervalMs); } = 1500;

    public int FreeIntervalMs { get; set; }

    public Func<bool> CanSend { get; set; } = () => true;

    public Action<string> Sender { get; set; } = _ => { };

    // Null holds for the next Update.
    public Func<string, string?> Rewrite { get; set; } = line => line;

    public event Action<string, long>? LineSent;

    public event Action<Exception>? SendFailed;

    // (part, of), within the sent line.
    public event Action<int, int>? Progress;

    public event Action? Finished;

    // This means a throttle notice came right after this line, ergo it may or may not have posted.
    public event Action<string>? Suspected;

    // Fires before a message's first line is rewritten, and again when it picks up after a cut-in.
    public event Action? MessageStarting;

    public int PendingCount => _messages.Sum(message => message.Parts.Count);

    public string? Head => _messages.Count > 0 ? _messages[0].Parts[0].Line : null;

    public (int Part, int Of)? HeadPart => _messages.Count > 0 ? (_messages[0].Parts[0].Part, _messages[0].Parts[0].Of) : null;

    public string? Suspect => _suspect?.Line;

    public bool LastSentWasTyped => _lastSent?.From.Typed == true;

    // The channel of the message a cut-in would go in front of (with the last started one still queued).
    // CanCutIn checks it so nothing cuts in front of /r.
    public string? Underway => _messages.FindLast(message => message.Started)?.Channel;

    public string? Current => _current is { } message && _messages.Contains(message) ? message.Channel : null;

    public SendQueueState State =>
        _suspect != null ? SendQueueState.Asking
        : _messages.Count == 0 ? SendQueueState.Idle
        : CanSend() ? SendQueueState.Sending
        : SendQueueState.Held;

    // If ahead: cuts in front of the message while it's partway through sending; then that one resumes afterwards.
    // If typed: a single line the player typed, broken only where they put split markers.
    public void Enqueue(
        IEnumerable<string> lines, string channel = "", bool ahead = false, IReadOnlyList<int>? pausesMs = null, long nowMs = 0, bool typed = false)
    {
        var parts = lines.ToList();
        if (parts.Count == 0)
            return;

        if (_messages.Count == 0)
            _lastSent = null;

        var (at, asTyped) = Place(channel, ahead, typed);
        _messages.Insert(at, new Message(channel, [.. parts.Select((line, i) => (line, i + 1, parts.Count, pausesMs?.ElementAtOrDefault(i) ?? 0))])
        {
            PausedFrom = nowMs,
            Typed = asTyped,
        });
    }

    // Never jumps a message that might share its channel, so each channel reads in the order sent.
    // EXCEPTION: one line typed while something's queued goes next, like a held line, after any lines typed before it.
    private (int At, bool Typed) Place(string channel, bool ahead, bool typed)
    {
        if (typed && ahead && _messages.Count > 0 && TypedAt is { } next)
            return (next, true);

        var underway = _messages.FindLastIndex(message => message.Started);

        return (ahead && underway >= 0 && _messages.FindIndex(underway, message => ChannelCommands.MightShare(message.Channel, channel)) < 0
            ? underway
            : _messages.Count, false);
    }

    // Goes right after the lines typed before it, or null if a reply is underway in front.
    private int? TypedAt => _messages.FindLastIndex(message => message.Typed) + 1 is var at
        && !(at < _messages.Count && _messages[at] is { Started: true, Channel: ChannelCommands.Reply }) ? at : null;

    public bool GoesNext(string channel, bool ahead, bool typed = false) => _suspect == null && Place(channel, ahead, typed).At == 0;

    // For a line the player sent on a channel with the game's wait.
    // During a post and within MinIntervalMs of the last line, it's held to go next and the post carries on after it.
    // If a typed line is still held, it goes behind that one, no matter the time, so they keep the order typed.
    // Otherwise, it's gone out, and the next part waits MinIntervalMs after it.
    public bool Typed(string line, string channel, long nowMs, bool canHold)
    {
        var posting = _queueWentLast || _messages.Any(message => !ChannelCommands.Unlimited(message.Channel));
        var tooSoon = nowMs < Math.Max(_lastPartAt, _lastTypedAt) + MinIntervalMs;

        // Held or not, ours is no longer the last line to a notice.
        _queueWentLast = false;

        if (canHold && TypedAt is { } at && (at > 0 || _suspect == null && posting && tooSoon))
        {
            _messages.Insert(at, new Message(channel, [(line, 1, 1, 0)]) { Typed = true });
            return true;
        }

        _lastTypedAt = nowMs;
        return false;
    }

    public void Cancel()
    {
        _messages.Clear();
        (_lastSent, _suspect) = (null, null);
    }

    // Drops the rest of the message posting now, wherever cut-ins have put it.
    // Returns how many unsent lines it dropped.
    public int DropCurrentMessage()
    {
        // Nothing to drop if the question is about a finished message, so it stays.
        if (_current is not { } message || !_messages.Remove(message))
            return 0;

        // A late throttle notice for a dropped line must not bring it back.
        (_lastSent, _suspect) = (null, null);
        return message.Parts.Count;
    }

    public void ReportThrottled(long nowMs)
    {
        // The notice fires for anything sent too fast, so we only claim one within ThrottleClaimWindowMs.
        // Claimed later, posting would pause and offer to resend a part from god only knows how long ago. imagine getting rate limited and posting your last spicy rp in novice network
        if (_lastSent is not { } sent || nowMs - _lastSentAt > ThrottleClaimWindowMs)
            return;

        // If the player sent a line after ours, the notice is about theirs.
        // Free company and the like have no wait to break, so it's never about them.
        if (!_queueWentLast || ChannelCommands.Unlimited(sent.From.Channel))
            return;

        (_suspect, _lastSent) = (sent, null);

        Suspected?.Invoke(sent.Line);
    }

    // Resend puts the suspect back in front of the rest of its message, otherwise it counts as posted.
    public void Resume(bool resend)
    {
        if (_suspect is not { } suspect)
            return;

        _suspect = null;
        if (!resend)
            return;

        var part = (suspect.Line, suspect.Part, suspect.Of, 0);

        // If its message finished, the line goes first on its own, nothing has posted since.
        if (!_messages.Contains(suspect.From))
            _messages.Insert(0, new Message(suspect.From.Channel, [part]) { Typed = suspect.From.Typed });
        else
            suspect.From.Parts.Insert(0, part);
    }

    public void Update(long nowMs)
    {
        if (_messages.Count == 0 || _suspect != null || nowMs < DueAt)
            return;

        if (!CanSend())
        {
            // While held, keep pushing the next send back so the parts don't rush out when it clears.
            _heldUntil = nowMs + IntervalMs;
            return;
        }

        if (NextLine() is { } line)
            SendHead(line, nowMs);
    }

    // Outside ChannelCommands.Unlimited the channels share the game's wait, so it runs from the last part on any of them.
    // A part keeps the post's own pace, and only needs MinIntervalMs after a typed line.
    // The unlimited ones (FC, party, linkshells) took parts a frame apart and every one arrived, so they're paced like a macro instead. See MacroPaceMs
    private long DueAt => Math.Max(Math.Max(_heldUntil, _messages[0].PausedFrom + _messages[0].Parts[0].WaitMs), _messages[0] switch
    {
        var head when ChannelCommands.Unlimited(head.Channel) => _lastSentAt + FreeIntervalMs,
        { Typed: true } head when head.Parts[0].Part == 1 => Math.Max(_lastPartAt, _lastTypedAt) + MinIntervalMs,
        _ => Math.Max(_lastPartAt + IntervalMs, _lastTypedAt + MinIntervalMs),
    });

    private string? NextLine()
    {
        var message = _messages[0];

        if (message != _current)
        {
            (_current, message.Started) = (message, true);
            MessageStarting?.Invoke();
        }

        return Rewrite(message.Parts[0].Line);
    }

    private void SendHead(string line, long nowMs)
    {
        var message = _messages[0];
        var part = message.Parts[0];

        // Stored as sent, so a resend repeats the rewritten /tell, not the /r.
        // Set before sending.
        // A refusal inside the call drops the message and clears this, and that needs to stick.
        _lastSent = (message, line, part.Part, part.Of);
        (_lastSentAt, message.PausedFrom) = (nowMs, nowMs);

        var limited = !ChannelCommands.Unlimited(message.Channel);
        // Only a typed message's first part is the typed line, the rest keep the post's pace.
        if (limited && message.Typed && part.Part == 1)
            _lastTypedAt = nowMs;
        else if (limited)
            _lastPartAt = nowMs;

        _queueWentLast |= limited;

        try
        {
            Sender(line);
        }
        catch (Exception ex)
        {
            // It's cleaner to not send anything at all instead of a partial message.
            Cancel();
            SendFailed?.Invoke(ex);
            return;
        }

        // If it got dropped or canceled inside the call, the batch has already ended.
        if (_lastSent is null && _suspect is null)
            return;

        // Only now, so a refusal raised inside the call finds its message, last part or not.
        message.Parts.RemoveAt(0);
        if (message.Parts.Count == 0)
            _messages.Remove(message);

        LineSent?.Invoke(line, nowMs);

        Progress?.Invoke(part.Part, part.Of);

        if (_messages.Count == 0)
            Finished?.Invoke();
    }
}
