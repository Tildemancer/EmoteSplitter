using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace TildeTools.Modules.EmoteSplitter.Chat;

// Re-applies on addon events since the game will rebuild the component and resets the cap in the process.
internal sealed unsafe class InputCapManager : IDisposable
{
    private readonly EmoteSplitterSettings _settings;

    private uint _originalMaxByte;
    private uint _originalMaxChar;
    private bool _captured;

    internal static readonly AddonEvent[] ChatLogEvents = [AddonEvent.PostSetup, AddonEvent.PostRefresh, AddonEvent.PostRequestedUpdate];

    internal static bool Available =>
        AtkComponentTextInput.MemberFunctionPointers.SetMaxByte != null && AtkComponentTextInput.MemberFunctionPointers.SetMaxChar != null;

    internal InputCapManager(EmoteSplitterSettings settings)
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

        // Otherwise every ChatLog event would set them again while unlocking is off.
        _captured = false;
    }

    public void Dispose()
    {
        Svc.AddonLife.UnregisterListener(OnChatLogChanged);
        Restore();
    }
}
