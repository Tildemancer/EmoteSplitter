using Dalamud.Interface.Windowing;
using Dalamud.Plugin;

namespace TildeSplitter;

public sealed class Plugin : IDalamudPlugin
{
    private readonly Configuration _config;
    private readonly WindowSystem _windows = new("TildeSplitter");
    private readonly EmoteSplitterModule _splitter;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Svc>();

        _config = Svc.Pi.GetPluginConfig() as Configuration ?? new Configuration();
        _splitter = new EmoteSplitterModule(_config, Save, _windows);

        if (_splitter.UnavailableReason is { } reason)
            Svc.Chat.PrintError($"[Emote Splitter] {reason}");
        else
            _splitter.Enable();

        Svc.Pi.UiBuilder.Draw += _windows.Draw;
        Svc.Pi.UiBuilder.OpenConfigUi += _splitter.Settings.Toggle;
    }

    private void Save() => Svc.Pi.SavePluginConfig(_config);

    public void Dispose()
    {
        Svc.Pi.UiBuilder.Draw -= _windows.Draw;
        Svc.Pi.UiBuilder.OpenConfigUi -= _splitter.Settings.Toggle;

        _splitter.Disable();
        _windows.RemoveAllWindows();
    }
}
