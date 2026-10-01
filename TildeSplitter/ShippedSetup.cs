using System;
using System.Collections.Generic;
using System.IO;

namespace TildeTools.Modules;

// Restarts: can restart to take a queued setup at once.
internal readonly record struct SetupFile(string Name, string Path, bool Restarts)
{
    internal bool HasSettings => new FileInfo(Path) is { Exists: true, Length: > 0 };

    internal bool Declined => ShippedSetup.Declined.Contains(Name);
}

// My C2 and XIM settings for the defaults
internal static class ShippedSetup
{
    internal static HashSet<string> Declined { get; set; } = [];

    // Where the standalone plugins kept their configs ala pluginConfigs.
    internal static string PathOf(params string[] parts) =>
        Path.Combine([Svc.Pi.ConfigFile.DirectoryName!, .. parts]);

    internal static void Prepare(SetupFile setup)
    {
        var queued = setup.Path + ".replace";

        if (File.Exists(queued))
        {
            if (File.Exists(setup.Path))
                File.Replace(queued, setup.Path, $"{setup.Path}.{DateTime.Now:yyyyMMdd-HHmmss}.bak");
            else
                File.Move(queued, setup.Path);

            Svc.Log.Info($"Replaced {setup.Path} with my setup.");
        }
        else if (!setup.HasSettings && !setup.Declined)
        {
            Write(setup.Name, setup.Path);
            Svc.Log.Info($"No settings at {setup.Path}, so it starts from my setup.");
        }
    }

    // We write beside and not over other plugins' configs so that a running copy saving its settings doesn't undo it before Prepare.
    internal static void Queue(SetupFile setup) => Write(setup.Name, setup.Path + ".replace");

    private static void Write(string name, string path)
    {
        using var shipped = typeof(ShippedSetup).Assembly.GetManifestResourceStream($"TildeTools.Defaults.{name}")!;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using (var file = File.Create(path + ".tmp"))
            shipped.CopyTo(file);

        File.Move(path + ".tmp", path, overwrite: true);
    }
}
