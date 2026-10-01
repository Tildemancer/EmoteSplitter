using System;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace TildeTools.Modules.EmoteSplitter.Chat;

// ProcessChatBoxEntry is sig-scanned and can be null post-patch.
// If called null, it crashes the game. Yikes!
internal static unsafe class ChatSender
{
    // Lets our own sends past our send hook.
    [ThreadStatic] internal static bool Passthrough;

    internal static bool Available => UIModule.MemberFunctionPointers.ProcessChatBoxEntry != null;

    // C2's <at:group,key> tags arrive as text and get encoded the way C2 would, dropping unknown pairs.
    // ChatTwoModule.EncodeTags, set in Plugin.
    internal static Func<byte[], byte[]>? EncodeTags;

    // Shift-click puts "<item>" in the box and the item in a store that lasts one send, so only part 1 would get the link.
    // Plain data apart from the vtable, so copy-safe at least.
    // No LinkedItemName, that's a Utf8String and its heap buffer.
    private static AgentChatLog.LinkedInventoryItem _heldItem;

    internal static bool HoldingItemLink { get; private set; }

    // Call on the frame it was typed, while the copy is still the right one.
    internal static void HoldItemLink()
    {
        _heldItem = AgentChatLog.Instance()->LinkedItem;
        HoldingItemLink = true;
    }

    internal static void ReleaseItemLink() => HoldingItemLink = false;

    // Framework thread only.
    // No Available check
    // Only the send queue calls this, and the module won't enable without it.
    internal static void Send(string line, bool saveToHistory)
    {
        // Only when there's a tag.
        // The first encode builds a list of every tag C2 has.
        // Each tag it swaps leaves a zero byte at the end, which its own send treats as the terminator.
        ReadOnlySpan<byte> bytes = EncodeTags is { } encode && line.Contains("<at:", StringComparison.Ordinal)
            ? encode(Encoding.UTF8.GetBytes(line)).AsSpan().TrimEnd((byte)0)
            : Encoding.UTF8.GetBytes(line);

        if (bytes.Contains((byte)0))
            throw new InvalidOperationException("Message contained an embedded null byte.");

        byte[] buffer = [.. bytes, 0];

        var agent = HoldingItemLink ? AgentChatLog.Instance() : null;
        var theirs = agent != null ? agent->LinkedItem : default;

        Utf8String* message = null;
        try
        {
            fixed (byte* p = buffer)
                message = Utf8String.FromSequence(p);

            if (agent != null)
                agent->LinkedItem = _heldItem;

            Passthrough = true;
            UIModule.Instance()->ProcessChatBoxEntry(message, 0, saveToHistory);
        }
        finally
        {
            Passthrough = false;

            if (agent != null)
                agent->LinkedItem = theirs;

            // The game destructor has to handle this since it's on the game's heap.
            if (message != null)
                message->Dtor(true);
        }
    }

    // Null terminated bytes from the box.
    // From EnterInterceptor as it passes them.
    internal static void SaveToHistory(byte[] raw)
    {
        var input = ChatLogInput();
        if (input == null)
            return;

        Utf8String* line;
        fixed (byte* p = raw)
            line = Utf8String.FromSequence(p);

        // The history keeps its own copy, as it does of the box's own lines, so ours can go.
        UIModule.Instance()->AddAtkHistoryEntry(line, input->AtkHistoryIndex);
        line->Dtor(true);
    }

    // Reads the chat input's flags.
    // C2 hardcodes 0x27F (Chat.cs:548, ChatBox.cs:37), which lacks the CJK bit, might strip kana and kanji for JP...
    internal static string Sanitize(string text)
    {
        var input = ChatLogInput();

        // Plus the CJK bit to address the above.
        var flags = input != null ? input->InputSanitizationFlags : (AllowedEntities)(0x27F | 0x400);

        var str = Utf8String.FromString(text);
        try
        {
            str->SanitizeString(flags);
            return str->ToString();
        }
        finally
        {
            str->Dtor(true);
        }
    }

    internal static AtkComponentTextInput* ChatLogInput() =>
        Svc.GameGui.GetAddonByName("ChatLog") is { IsNull: false } chatLog ? ((AddonChatLog*)chatLog.Address)->TextInput : null;
}
