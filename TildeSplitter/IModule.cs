using System;

namespace TildeTools.Modules;

// Enable takes hooks, listeners and patched pointers, Disable gives them back.
// Off must leave the game as if it never loaded!
internal interface IModule : IDisposable
{
    // Config key, never shown
    string Id { get; }

    string Name { get; }

    string Description { get; }

    bool IsEnabled { get; }

    string? UnavailableReason => null;

    // For a module that caches its UnavailableReason.
    void PluginsChanged() { }

    bool StartsOnFirstRun => true;

    SetupFile? Setup => null;

    void UseShippedSetup() { }

    // True, once, each time a hosted plugin opens its own settings window.
    // Our tab draws in its place.
    bool TakeSettingsRequest() => false;

    // True, once, after those settings close themselves, as above.
    // Specifically, clicking a Close or Save and close button.
    bool TakeCloseRequest() => false;

    void SettingsOpened() { }

    void SettingsClosed() { }

    void Enable();

    void Disable();

    void DrawTab();
}
