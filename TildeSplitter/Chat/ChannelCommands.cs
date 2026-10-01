using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TildeTools.Modules.EmoteSplitter.Chat;

internal static partial class ChannelCommands
{
    private static Dictionary<string, string> Channels = new(StringComparer.OrdinalIgnoreCase);

    internal const string Reply = "Reply";
    internal const string Tell = "Tell";
    internal const string Party = "Party";
    internal const string Alliance = "Alliance";
    internal const string Linkshell = "Linkshell";

    static ChannelCommands()
    {
        (string Name, string Aliases)[] named =
        [
            ("Say", "say s"), ("Yell", "yell y"), ("Shout", "shout sh"), ("Emote", "emote em me"),
            (Party, "party p"), (Alliance, "alliance a"), ("Free Company", "freecompany fc"),
            ("Novice Network", "novice n beginner b"), ("PvP Team", "pvpteam pt"), ("Echo", "echo e"),
            (Reply, "reply r"), (Tell, "tell t"), ($"Cross-world {Linkshell}", "cwl cwlinkshell"),
        ];

        foreach (var (name, aliases) in named)
            Name(name, aliases);

        for (var i = 1; i <= 8; i++)
        {
            Name($"{Linkshell} {i}", $"linkshell{i} l{i}");
            Name($"Cross-world {Linkshell} {i}", $"cwlinkshell{i} cwl{i}");
        }
    }

    private static void Name(string name, string aliases)
    {
        foreach (var alias in aliases.Split(' '))
            Channels[alias] = name;
    }

    // TextCommand sheet rows, where DE or FR clients keep stuff like /sagen and /dire.
    // Filled into a copy so each row only matches the built-in names and not one any earlier rows added.
    internal static void AddClientNames(IEnumerable<IEnumerable<string>> rows)
    {
        var channels = new Dictionary<string, string>(Channels, StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var names = row.Select(n => n.TrimStart('/')).Where(n => n.Length > 0).ToList();
            if (names.FirstOrDefault(Channels.ContainsKey) is { } known)
                foreach (var name in names)
                    channels[name] = Channels[known];
        }

        Channels = channels;
    }

    internal static string? NameOf(string header) => header.Length > 1 && header[0] == '/' ? Channels.GetValueOrDefault(header[1..]) : null;

    // The key the send queue keeps order within, "" for the active channel.
    // Every tell gets Tell, whoever it's to, because Name and Name@World can be the same person.
    internal static string KeyOf(string header) => NameOf(header) ?? (header.Length > 0 ? Tell : "");

    // "" is whichever channel the box is on, and a /r goes to someone's tells.
    internal static bool MightShare(string a, string b) =>
        a == b || a.Length == 0 || b.Length == 0 || a is Tell or Reply && b is Tell or Reply;

    // 0x02/0x03 frame links and auto-translate phrases.
    internal static bool HasPayload(string line) => line.AsSpan().ContainsAny('\x02', '\x03');

    // The game's "not heard" notice only names /tell, /say, /yell and /shout, so these probably don't share the wait.
    // Emote drops lines with no feedback, a macro of 15 /em lines only posted ~3 of them with no warning.
    // Novice network and "" wait along with those until shown otherwise.
    internal static bool Unlimited(string key) =>
        key is "Free Company" or Party or Alliance or "PvP Team" or "Echo" || key.Contains(Linkshell);

    // A /t or /tell, whether or not its target reads as a name.
    internal static bool IsTell(string line) =>
        CommandRegex().Match(line) is { Success: true } match && Channels.GetValueOrDefault(match.Groups["cmd"].Value) == Tell;

    [GeneratedRegex(@"^\s*(?<target>[\p{L}'\-]+\s+[\p{L}'\-]+(?:@[\p{L}]+)?)\s+(?<rest>.*)$",
        RegexOptions.Singleline)]
    private static partial Regex TellTargetRegex();

    [GeneratedRegex(@"^/(?<cmd>\p{L}+[0-9]*)(?:\s+(?<rest>.*))?$", RegexOptions.Singleline)]
    private static partial Regex CommandRegex();

    internal static bool TrySplittable(string line, out string header, out string body)
    {
        header = string.Empty;
        body = string.Empty;

        if (string.IsNullOrWhiteSpace(line))
            return false;

        if (line[0] != '/')
        {
            body = line;
            return true;
        }

        var match = CommandRegex().Match(line);
        if (!match.Success)
            return false;

        var command = match.Groups["cmd"].Value;
        var rest = match.Groups["rest"].Value;

        var name = Channels.GetValueOrDefault(command);
        if (name == Tell)
        {
            var tell = TellTargetRegex().Match(rest);
            if (!tell.Success)
                return false;

            (command, rest) = ($"{command} {tell.Groups["target"].Value}", tell.Groups["rest"].Value);
        }
        else if (name == null)
            return false;

        header = $"/{command}";
        body = rest;
        return body.Length > 0;
    }
}
