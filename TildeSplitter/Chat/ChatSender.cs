using System;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Text.ReadOnly;

namespace EmoteSplitter.Chat;

internal static unsafe class ChatSender
{
    // Lets our own sends past our send hook.
    [ThreadStatic] internal static bool Passthrough;

    // Framework thread only.
    internal static void Send(string line)
    {
        var message = Utf8String.FromString(line);
        try
        {
            Passthrough = true;
            UIModule.Instance()->ProcessChatBoxEntry(message, 0, false);
        }
        finally
        {
            Passthrough = false;

            // The game destructor has to handle this since it's on the game's heap.
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

    // EVERY payload counts, INCLUDING broken ones.
    // It's the bytes that matter here, not the macro type.
    internal static bool HasPayload(ReadOnlySpan<byte> raw)
    {
        foreach (var payload in new ReadOnlySeStringSpan(raw))
            if (payload.Type != ReadOnlySePayloadType.Text)
                return true;

        return false;
    }

    internal static AtkComponentTextInput* ChatLogInput() =>
        Svc.GameGui.GetAddonByName("ChatLog") is { IsNull: false } chatLog ? ((AddonChatLog*)chatLog.Address)->TextInput : null;
}
