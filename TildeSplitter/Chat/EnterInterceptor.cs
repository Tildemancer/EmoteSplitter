using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace EmoteSplitter.Chat;

internal sealed unsafe class EnterInterceptor : IDisposable
{
    // byte* where the game's signature has CStringPointer, they pass identically.
    private delegate InputCallbackResult InputCallback(AtkUnitBase* addon, InputCallbackType type, byte* raw, byte* evaluated, int eventKind);

    private readonly Func<string, byte[], InputCallbackResult?> _onTake;

    private Hook<InputCallback>? _hook;
    private bool _warned;

    internal EnterInterceptor(Func<string, byte[], InputCallbackResult?> onTake)
    {
        _onTake = onTake;

        // If missed, long text goes through with no catch. No bueno
        foreach (var ev in InputCapManager.ChatLogEvents)
            Svc.AddonLife.RegisterListener(ev, "ChatLog", OnChatLogChanged);

        TryHook();
    }

    private void OnChatLogChanged(AddonEvent type, AddonArgs args) => TryHook();

    // The handler's address is read off the chat box, so this waits for one and hooks it.
    // Only a game address so presumably a handler another plugin swapped isn't ever hooked.
    // Remember the live code starts at Module.BaseAddress + TextSectionOffset! TextSectionBase points into SigScanner
    private void TryHook()
    {
        var input = ChatSender.ChatLogInput();
        if (_hook != null || input == null || input->Callback == null)
            return;

        var address = (nint)input->Callback;
        var scanner = Svc.SigScanner;
        var code = scanner.Module.BaseAddress + scanner.TextSectionOffset;

        if (address < code || address >= code + scanner.TextSectionSize)
        {
            if (!_warned)
                Svc.Log.Warning($"The chat box's Enter handler at 0x{address:X} isn't the game's own, so long lines won't be caught at Enter.");

            _warned = true;
            return;
        }

        _hook = Svc.Interop.HookFromAddress<InputCallback>(address, Detour);
        _hook.Enable();
        Svc.Log.Info($"Enter hook installed at 0x{address:X}.");
    }

    // An exception escaping into native code crashes the client. Yikes!
    private InputCallbackResult Detour(AtkUnitBase* addon, InputCallbackType type, byte* raw, byte* evaluated, int eventKind)
    {
        try
        {
            // Enter first, so typing never blows the name check.
            if (type == InputCallbackType.Enter && evaluated != null && addon != null && addon->NameString == "ChatLog")
            {
                var input = ((AddonChatLog*)addon)->TextInput;

                // Passes RawString too, null-terminated, because decoding to a string breaks an auto-translated phrase's macro.
                if (_onTake(MemoryHelper.ReadStringNullTerminated((nint)evaluated), [.. input->RawString.AsSpan(), 0]) is { } result)
                {
                    // ClearText doesn't reset the character count.
                    if (result == InputCallbackResult.ClearText && AtkComponentTextInput.MemberFunctionPointers.UpdateCharacterCount != null)
                        input->UpdateCharacterCount(0, 0);

                    // None leaves the line in the box as it was typed, unsent
                    return result;
                }
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Enter interceptor failed; handing the key back to the game.");
        }

        return _hook!.Original(addon, type, raw, evaluated, eventKind);
    }

    public void Dispose()
    {
        Svc.AddonLife.UnregisterListener(OnChatLogChanged);

        if (_hook == null)
            return;

        _hook.Dispose();
        Svc.Log.Info("Enter hook removed.");
    }
}
