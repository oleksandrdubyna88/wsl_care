using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Status;

// The `interopRelays` block of the distro's `status --json` (plan E14 S7b.2): what an unprivileged status CAN read about the interop
// relays of catalogued Windows MCP servers — how many there are, and how many are re-parented to a reaper and not born there. Whether
// a relay's client is GONE needs root's scan of every fd table, so it is the button's preview, never this block. Additive: absent from
// every older daemon and from the Windows binary's answer; nullable members only here, at the JSON edge.

/// <summary>The interop relays this status can see, and A21's switch.</summary>
/// <param name="Count">The relays of catalogued Windows MCP servers readable by this caller (its own processes; every process as root).</param>
/// <param name="Reparented">Of them, re-parented to a reaper and not born there — the candidates A21's preview then judges by stdio.</param>
/// <param name="AutoStop">The EFFECTIVE <c>auto.A21</c> — whether the timer and the watch may stop client-gone relays.</param>
/// <param name="Command">The command that flips <paramref name="AutoStop"/> — the extension shows it, it never writes daemon config.</param>
public sealed record InteropRelaysReport(bool Available, string? Reason, int? Count, int? Reparented, bool AutoStop, string Command)
{
    public static InteropRelaysReport From(Reading<ProcessSnapshot> processes, IHostPaths paths, IFileSystem files, EffectiveConfig config)
    {
        var autoStop = config.Bool(ConfigKeys.Auto.A21);
        var command = $"wsl-care config set {ConfigKeys.Auto.A21.Name} {(autoStop ? "false" : "true")}";
        if (paths is not LinuxHostPaths linux || processes is not Reading<ProcessSnapshot>.Available { Value: var snapshot })
        {
            return new(false, paths is LinuxHostPaths ? processes.ReasonOrEmpty : "interop relays are the distro's", null, null, autoStop, command);
        }

        var servers = InteropRelays.Servers(config);
        var relays = snapshot.All
            .Where(p => InteropRelays.ServerOf(p, servers) is not null)
            .Select(p => InteropRelays.Read(files, linux, p, servers))
            .OfType<RelayFacts>()
            .ToList();
        return new(true, null, relays.Count, relays.Count(r => InteropRelays.IsReaper(r.Parent) && !InteropRelays.BornThere(r)), autoStop, command);
    }
}
