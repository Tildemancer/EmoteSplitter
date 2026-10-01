using System;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace TildeSplitter.Chat;

// ProcessChatBoxEntry is sig-scanned and can be null post-patch.
// If called null, it crashes the game. Yikes!
internal static unsafe class ChatSender
{
    // Lets our own sends past our send hook.
    [ThreadStatic] internal static bool Passthrough;

    internal static bool Available => UIModule.MemberFunctionPointers.ProcessChatBoxEntry != null;

    // Framework thread only.
    // No Available check
    // Only the send queue calls this, and the module won't enable without it.
    internal static void Send(string line, bool saveToHistory)
    {
        ReadOnlySpan<byte> bytes = Encoding.UTF8.GetBytes(line);

        if (bytes.Contains((byte)0))
            throw new InvalidOperationException("Message contained an embedded null byte.");

        byte[] buffer = [.. bytes, 0];

        Utf8String* message = null;
        try
        {
            fixed (byte* p = buffer)
                message = Utf8String.FromSequence(p);

            Passthrough = true;
            UIModule.Instance()->ProcessChatBoxEntry(message, 0, saveToHistory);
        }
        finally
        {
            Passthrough = false;

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
