using System;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;

namespace TildeTools.Modules.EmoteSplitter.Chat;

// A bare line goes wherever the chat box is when each part leaves, that way switching mid-post follows the client.
// This turns the box's channel into a command that every part can carry.
internal static unsafe class ActiveChannel
{
    // Indexed by RaptureShellModule.ChatType, which matches C2's InputChannel numbering except for tells (17 or 18 here, 0 there)
    // I checked 1, 6, 9 and 17 in game, presumably the rest are identical as well.
    private static readonly string?[] Commands =
    [
        null, "/s", "/p", "/a", "/y", "/sh", "/fc", "/pt", "/b",
        "/cwl1", "/cwl2", "/cwl3", "/cwl4", "/cwl5", "/cwl6", "/cwl7", "/cwl8",
        null, null,
        "/l1", "/l2", "/l3", "/l4", "/l5", "/l6", "/l7", "/l8",
    ];

    internal const string Unreadable =
        "Could not tell which channel the chat box is on, so the parts of that message could have gone to " +
        "different places. Nothing was sent. Start it with a channel command, like /s, to split it.";

    internal static bool TryPin(ref string header)
    {
        // ExtraChat's channels aren't the game's, and a game command would skip its override.
        // C2 checks for the same command.
        if (header.Length > 0 || Svc.Commands.Commands.ContainsKey("/ecl1"))
            return true;

        var shell = RaptureShellModule.Instance();
        var pinned = shell == null ? null : Read(shell);

        if (pinned == null)
            return false;

        header = pinned;
        return true;
    }

    // Checks what Read looks at every frame. Too cheap to worry about...
    internal static int Fingerprint()
    {
        var shell = RaptureShellModule.Instance();
        if (shell == null)
            return 0;

        HashCode hash = new();
        hash.Add(shell->ChatType);
        hash.Add(shell->TempChatType);
        hash.AddBytes(shell->TellName.AsSpan());
        hash.AddBytes(shell->TellWorld.AsSpan());
        return hash.ToHashCode();
    }

    private static string? Read(RaptureShellModule* shell)
    {
        var type = shell->ChatType;

        // TempChatType read -2 in every state tried in game???
        // 0 and up is some unnamable temp channel.
        if (shell->TempChatType >= 0)
            return null;

        if (type is not (17 or 18))
            return type >= 0 && type < Commands.Length ? Commands[type] : null;

        // The editbox writes out /tell Name@World itself, this is for WS's None type.
        var name = shell->TellName.ToString();
        var world = shell->TellWorld.ToString();
        return name.Length > 0 && world.Length > 0 ? $"/t {name}@{world}" : null;
    }
}
