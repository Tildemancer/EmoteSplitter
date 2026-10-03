using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace EmoteSplitter.Chat;

// Re-applies on addon events since the game will rebuild the component and resets the cap in the process.
internal sealed unsafe class InputCapManager : IDisposable
{
    private readonly Configuration _settings;

    private uint _originalMaxByte;
    private uint _originalMaxChar;
    private bool _captured;

    internal static readonly AddonEvent[] ChatLogEvents = [AddonEvent.PostSetup, AddonEvent.PostRefresh, AddonEvent.PostRequestedUpdate];

    internal static bool Available =>
        AtkComponentTextInput.MemberFunctionPointers.SetMaxByte != null && AtkComponentTextInput.MemberFunctionPointers.SetMaxChar != null;

    internal InputCapManager(Configuration settings)
    {
        _settings = settings;

        // Should only warn once. "Should".
        if (!Available)
            Svc.Log.Warning("SetMaxByte/SetMaxChar could not be located for this game version; leaving the chat input limit alone.");

        foreach (var ev in ChatLogEvents)
            Svc.AddonLife.RegisterListener(ev, "ChatLog", OnChatLogChanged);

        Apply();
    }

    private void OnChatLogChanged(AddonEvent type, AddonArgs args) => Apply();

    internal void Apply()
    {
        if (!_settings.UnlockChatInput || !Available)
        {
            Restore();
            return;
        }

        var input = ChatSender.ChatLogInput();
        if (input == null)
            return;

        if (!_captured)
        {
            (_originalMaxByte, _originalMaxChar, _captured) = (input->ComponentTextData.MaxByte, input->ComponentTextData.MaxChar, true);
            Svc.Log.Info($"Chat input native caps: MaxByte={_originalMaxByte}, MaxChar={_originalMaxChar}");
        }

        // Clamped by UnlockedMaxBytes' setter.
        var targetBytes = _settings.UnlockedMaxBytes;
        if (input->ComponentTextData.MaxByte == targetBytes)
            return;

        input->SetMaxByte(targetBytes);

        // MaxChar=0 means no limit, so setting one would necessarily add a limit.
        if (_originalMaxChar != 0)
            input->SetMaxChar(targetBytes);

        Truncate(input, targetBytes);

        var applied = input->ComponentTextData.MaxByte;
        if (applied == targetBytes)
            Svc.Log.Info($"Chat input limit set to {applied} bytes.");
        else
            Svc.Log.Warning($"Set the chat input limit to {targetBytes} bytes but it still reads {applied}.");
    }

    private void Restore()
    {
        if (!_captured)
            return;

        var input = ChatSender.ChatLogInput();
        if (input == null)
            return;

        input->SetMaxByte((int)_originalMaxByte);
        input->SetMaxChar((int)_originalMaxChar);

        // >>> DANGER!!! <<<
        // We MUST reset the text limit, the editbox WILL accept up to 1kb of text and SEND IT IN A WAY THAT THE SERVER CAN SEE!!!! Over that can be sent, but presumably rejects it.
        // I don't know why, just that it works.
        Truncate(input, (int)_originalMaxByte);

        // Otherwise every ChatLog event would set them again while unlocking is off.
        _captured = false;
    }

    private static void Truncate(AtkComponentTextInput* input, int limit)
    {
        if (input->RawString.AsSpan().Length <= limit)
            return;

        // A focused box keeps its own copy of the line and puts it back when it loses focus, so focus goes first
        RaptureAtkModule.Instance()->ClearFocus();

        // SetText of a phrase's raw bytes leaves it editable inside, and a cut can land mid-phrase, so a line holding one is emptied instead for safety.
        // Man, they said this shit was dangerous, and I didn't listen...
        // fuck me.
        byte[] line = ChatSender.HasPayload(input->RawString.AsSpan()) ? [0] : [.. input->RawString.AsSpan(), 0];
        fixed (byte* text = line)
            input->SetText(text);
    }

    public void Dispose()
    {
        Svc.AddonLife.UnregisterListener(OnChatLogChanged);
        Restore();
    }
}
