using System.Collections.Generic;

namespace TildeTools.Modules.EmoteSplitter.Chat;

// LogMessage rows the send queue acts on, see EmoteSplitterModule.OnLogMessage
internal static class LogMessages
{
    // "Your message was not heard." must be the client's, probably from typing too fast, because TT sends skip the rate check.
    internal const uint Throttled = 749;

    // From the game;
    // 483 unable to send
    // 726 command unavailable (i.e. a linkshell you're not in)
    // 728 no party members
    // 924 no alliance members
    // 3810 consecutive commands restricted,
    // 3872 to 3874 duty tell limits
    // 3876 and 9743 muted
    internal static readonly HashSet<uint> Fatal = [483, 726, 728, 924, 3810, 3872, 3873, 3874, 3876, 9743];

    // About one channel each, and the player's own /p typed mid-post raises 728 as well.
    internal static string? ChannelOf(uint id) => id switch
    {
        726 => ChannelCommands.Linkshell,
        728 => ChannelCommands.Party,
        924 => ChannelCommands.Alliance,
        _ => null,
    };
}
