using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using TildeTools.Modules;

namespace TildeTools.Ui;

internal static class Widgets
{
    internal static readonly Vector4 ErrorColor = new(1f, 0.4f, 0.3f, 1f);
    internal static readonly Vector4 WarningColor = new(1f, 0.5f, 0.3f, 1f);

    internal static void Open(string url)
    {
        try
        {
            Dalamud.Utility.Util.OpenLink(url);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"Could not open {url}.");
        }
    }

    // Setters take what they write to as state, so callers pass static lambdas and nothing's allocated per frame. Blessings and all that.

    // Applied on release, so it doesn't save on every frame.
    internal static bool Paced<TState>(string label, ref int shown, int actual, int min, int max, TState state, Action<TState, int> apply)
    {
        if (shown < 0)
            shown = actual;

        ImGui.SliderInt(label, ref shown, min, max);

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            shown = Math.Clamp(shown, min, max);
            apply(state, shown);
            return true;
        }

        // Follows the setting again once released, so a value clamped elsewhere doesn't sit stale.
        if (!ImGui.IsItemActive())
            shown = actual;

        return false;
    }

    // Arguments run left to right, so Apply sees the value ImGui just wrote.
    internal static bool Toggle<TState>(string label, bool value, TState state, Action<TState, bool> set) =>
        Apply(ImGui.Checkbox(label, ref value), value, state, set);

    internal static bool Edit<TState>(string label, string value, int maxLength, TState state, Action<TState, string> set) =>
        Apply(ImGui.InputText(label, ref value, maxLength), value, state, set);

    // Live while dragged but only dirty on release. Otherwise it saves on every frame, again.
    internal static bool Slide<TState>(string label, int value, int min, int max, TState state, Action<TState, int> set)
    {
        if (ImGui.SliderInt(label, ref value, min, max))
            set(state, value);

        return ImGui.IsItemDeactivatedAfterEdit();
    }

    internal static bool Choose<TState>(string label, IReadOnlyList<string> names, int current, TState state, Action<TState, int> set)
    {
        using var combo = ImRaii.Combo(label, names[current]);
        if (!combo.Success)
            return false;

        var picked = current;
        for (var i = 0; i < names.Count; i++)
            if (ImGui.Selectable(names[i], i == current))
                picked = i;

        return Apply(picked != current, picked, state, set);
    }

    internal static bool ModuleRow(IModule module, ref bool on)
    {
        var unavailable = module.UnavailableReason;

        bool changed;
        using (ImRaii.Disabled(unavailable != null))
            changed = ImGui.Checkbox(module.Name, ref on);

        using (ImRaii.PushIndent())
        using (ImRaii.TextWrapPos(0f))
        {
            ImGui.TextDisabled(module.Description);

            if (unavailable != null)
                ImGui.TextColored(WarningColor, $"Unavailable: {unavailable}");
        }

        return changed;
    }

    private static bool Apply<TState, T>(bool changed, T value, TState state, Action<TState, T> set)
    {
        if (changed)
            set(state, value);

        return changed;
    }
}
