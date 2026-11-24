using System.Diagnostics;

namespace InputFinder;

static class KnownInputProcesses
{
    static readonly string[] Tokens =
    [
        "icue",
        "corsair.service",
        "corsairgaming",
        "lghub",
        "logioptions",
        "lcore",
        "razer",
        "rzsdk",
        "powertoys",
        "autohotkey",
        "streamdeck",
        "rewasd",
        "joytokey",
        "ds4windows",
        "dualsensex",
        "dsx",
        "vigem",
        "touchportal",
        "barrier",
        "synergy",
        "sharex",
        "steelseries",
        "armoury",
        "lightingservice",
        "xpadder",
        "antimicro",
        "voiceattack",
        "pulover",
        "macrorecorder",
        "controllercompanion",
        "discord",
        "steam",
        "nvidia share",
        "gamebar",
        "voicemeeter",
        "openrgb",
        "signalrgb",
        "asus_framework",
    ];

    public static IReadOnlyList<string> Running()
    {
        var found = new List<string>();
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return found;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in processes)
        {
            try
            {
                var name = process.ProcessName;
                var lower = name.ToLowerInvariant();
                foreach (var token in Tokens)
                {
                    if (!lower.Contains(token, StringComparison.Ordinal))
                        continue;
                    var line = name + "  (pid " + process.Id + ")";
                    if (seen.Add(line))
                        found.Add(line);
                    break;
                }
            }
            catch
            {
                // Process exited between the snapshot and the read.
            }
            finally
            {
                process.Dispose();
            }
        }

        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }
}
