using System;
using System.Collections.Generic;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;

namespace TildeSplitter;

public sealed class Plugin : IDalamudPlugin
{
    private static readonly string[] CommandNames = ["/splitter", "/split", "/tsplit", "/tsplitter"];

    private readonly Configuration _config;
    private readonly WindowSystem _windows = new("TildeSplitter");
    private readonly EmoteSplitterModule _splitter;
    private readonly List<string> _commands = [];

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Svc>();

        _config = Svc.Pi.GetPluginConfig() as Configuration ?? new Configuration();
        _splitter = new EmoteSplitterModule(_config, Save, _windows);

        Svc.Pi.UiBuilder.Draw += _windows.Draw;
        Svc.Pi.UiBuilder.OpenConfigUi += _splitter.Settings.Toggle;

        foreach (var name in CommandNames)
        {
            var info = new CommandInfo(OnCommand)
            {
                HelpMessage = "Open TildeSplitter\n/splitter cancel - Stop a message that's mid-send",
                ShowInHelp = name == CommandNames[0],
            };

            if (Svc.Commands.AddHandler(name, info))
                _commands.Add(name);
            else
                Svc.Log.Warning($"{name} is already another plugin's command.");
        }
    }

    private void Save() => Svc.Pi.SavePluginConfig(_config);

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("cancel", StringComparison.OrdinalIgnoreCase))
            _splitter.Stop();
        else
            _splitter.Settings.Toggle();
    }

    public void Dispose()
    {
        foreach (var name in _commands)
            Svc.Commands.RemoveHandler(name);

        Svc.Pi.UiBuilder.Draw -= _windows.Draw;
        Svc.Pi.UiBuilder.OpenConfigUi -= _splitter.Settings.Toggle;

        _splitter.Dispose();
        _windows.RemoveAllWindows();
    }
}
