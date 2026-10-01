using System;
using System.Collections.Generic;
using Dalamud.Plugin.Ipc;
using TildeTools.Modules.EmoteSplitter;

namespace TildeTools.Ipc;

internal sealed class SplitterIpc : IDisposable
{
    // Only bumped for breaking changes.
    private const int ApiVersion = 2;

    private const string Prefix = "TildeTools.Split.";

    private readonly ICallGateProvider<object?> _available = Svc.Pi.GetIpcProvider<object?>($"{Prefix}Available");
    private readonly List<ICallGateProvider> _gates = [];

    internal SplitterIpc(EmoteSplitterModule splitter, Func<int> inputCap)
    {
        var (apiVersion, inputByteCap) = (Svc.Pi.GetIpcProvider<int>($"{Prefix}ApiVersion"), Svc.Pi.GetIpcProvider<int>($"{Prefix}InputByteCap"));
        apiVersion.RegisterFunc(() => ApiVersion);
        inputByteCap.RegisterFunc(inputCap);
        _gates.AddRange([apiVersion, inputByteCap]);

        // How long a line's parts take to go out (its channel's gaps plus any |n pauses), 0 for a single part.
        Serve("PostingMs", splitter.PostingMsForIpc, () => 0);
        Serve("SplitLine", splitter.SplitForIpc, () => []);

        // Flat start/length pairs, because only framework types can cross between plugins that share no assembly.
        Serve("SplitLineBodySpans", splitter.SplitBodySpansForIpc, () => []);
        Serve("SplitLineBodySources", splitter.SplitBodySourcesForIpc, () => []);

        // This is WS's Post, and a line that fits goes out as one part.
        // If declined, the pad could only copy it.
        Serve("SendLine", line => splitter.SendStatusForIpc(line, requireSplit: false) == SplitTake.Queued, () => false, SendError);

        // SendLine's bool can't tell NotTaken from Refused.
        // It answers Refused if it throws, so the caller keeps the text instead of sending it over the limit.
        Serve("SendLineStatus", line => (int)splitter.SendStatusForIpc(line), () => (int)SplitTake.Refused, SendError);

        Announce();

        Svc.Log.Info($"Splitter IPC registered (version {ApiVersion}).");
    }

    // Callers cache the gates' answers until this fires.
    internal void Announce() => _available.SendMessage();

    private const string SendError = "[Emote Splitter] That message hit an error. /xllog has the detail.";

    // Takes a line and a byte cap, but the cap goes unused.
    // Every caller passes 500 or 0, and both basically mean Budget.
    private void Serve<T>(string name, Func<string, T> call, Func<T> fallback, string? say = null)
    {
        var gate = Svc.Pi.GetIpcProvider<string, int, T>(Prefix + name);
        gate.RegisterFunc((line, _) =>
        {
            try
            {
                return call(line);
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, $"IPC {name} failed.");

                // Callers print nothing on a refusal, it's Emote Splitter's responsibility to explain.
                if (say != null)
                    Svc.Chat.PrintError(say);

                return fallback();
            }
        });

        _gates.Add(gate);
    }

    public void Dispose()
    {
        foreach (var gate in _gates)
            gate.UnregisterFunc();
    }
}
