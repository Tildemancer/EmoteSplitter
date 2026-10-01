using System;
using System.Runtime.InteropServices;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Component.GUI;
using InteropGenerator.Runtime;

namespace TildeTools.Modules.EmoteSplitter.Chat;

// byte* for CStringPointer, which UnmanagedCallersOnly won't take.
// They pass identically.
using unsafe Handler = delegate* unmanaged<AtkUnitBase*, InputCallbackType, byte*, byte*, int, InputCallbackResult>;
using unsafe GameHandler = delegate* unmanaged<AtkUnitBase*, InputCallbackType, CStringPointer, CStringPointer, int, InputCallbackResult>;

internal sealed unsafe class EnterInterceptor : IDisposable
{
    private Handler _original;

    private static EnterInterceptor? _active;

    private readonly Func<string, byte[], bool> _onTake;

    private AtkComponentTextInput* _patched;

    internal EnterInterceptor(Func<string, byte[], bool> onTake)
    {
        _onTake = onTake;
        _active = this;

        // If missed, long text goes through with no catch. No bueno
        foreach (var ev in InputCapManager.ChatLogEvents)
            Svc.AddonLife.RegisterListener(ev, "ChatLog", OnChatLogChanged);

        Svc.AddonLife.RegisterListener(AddonEvent.PreFinalize, "ChatLog", OnChatLogFinalized);

        Apply();
    }

    private void OnChatLogChanged(AddonEvent type, AddonArgs args) => Apply();

    // No restore, the component is about to be freed!
    private void OnChatLogFinalized(AddonEvent type, AddonArgs args)
    {
        _patched = null;
        _original = null;
    }

    private void Apply()
    {
        var input = ChatSender.ChatLogInput();
        if (input == null || _patched == input)
            return;

        var current = (Handler)input->Callback;
        if (current == null)
        {
            Svc.Log.Warning("Chat input has no Enter callback; cannot intercept the chat box submit.");
            return;
        }

        _patched = input;

        // Adopting our own pointer would make the original recurse forever.
        Handler ours = &OnInputCallback;
        if (current == ours)
            return;

        _original = current;
        input->Callback = (GameHandler)ours;

        Svc.Log.Info($"Enter interceptor installed; the game's handler is at 0x{(nint)_original:X}.");
    }

    private void Restore()
    {
        if (_patched == null || _original == null)
            return;

        if (ChatSender.ChatLogInput() == _patched)
        {
            _patched->Callback = (GameHandler)_original;
            Svc.Log.Info("Enter interceptor removed.");
        }

        _patched = null;
        _original = null;
    }

    // An exception escaping into native code crashes the client. Yikes!
    [UnmanagedCallersOnly]
    private static InputCallbackResult OnInputCallback(AtkUnitBase* addon, InputCallbackType type, byte* raw, byte* evaluated, int eventKind)
    {
        if (_active is not { } self)
            return InputCallbackResult.None;

        try
        {
            // Passes RawString too, null-terminated, because decoding to a string breaks an auto-translated phrase's macro.
            if (type == InputCallbackType.Enter && evaluated != null && self._patched != null
                && self._onTake(MemoryHelper.ReadStringNullTerminated((nint)evaluated), [.. self._patched->RawString.AsSpan(), 0]))
            {
                // ClearText doesn't reset the character count.
                if (AtkComponentTextInput.MemberFunctionPointers.UpdateCharacterCount != null)
                    self._patched->UpdateCharacterCount(0, 0);

                return InputCallbackResult.ClearText;
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Enter interceptor failed; handing the key back to the game.");
        }

        return self._original == null ? InputCallbackResult.None : self._original(addon, type, raw, evaluated, eventKind);
    }

    public void Dispose()
    {
        Svc.AddonLife.UnregisterListener(OnChatLogChanged);
        Svc.AddonLife.UnregisterListener(OnChatLogFinalized);
        Restore();

        // Cleared last so an in-flight callback can't find a half-disposed object.
        _active = null;
    }
}
