using WslCare.Core.Collectors;
using WslCare.Core.Config;

namespace WslCare.Core.Mcp;

/// <summary>
/// The Windows side's MCP server instances (E14 S7a) — READ-ONLY: it holds the process table seam and nothing that starts, signals
/// or stops a process. One snapshot names the processes; the servers (<see cref="WindowsMcpCatalogue"/>) are matched by exe name;
/// each instance is owned (<see cref="WindowsMcpOwners"/>); its CPU is read twice, <c>mcpServers.cpuWindowMilliseconds</c> apart —
/// waiting only when an instance can be measured — and compared only while the pid still names the process the first read saw
/// (same creation time, coai plan round 2026-10-09 finding 2).
/// </summary>
/// <remarks>The distro's S1 ledger (an interval since the previous sample) is a follow-up on Windows: the owner's Q-M5 keeps the
/// window, and this first step measures across it (coai plan round, finding 3 rejected).</remarks>
public sealed class WindowsMcpCollector(IWindowsProcessTable table, TimeProvider clock, Func<TimeSpan, CancellationToken, Task> wait)
{
    public async Task<Reading<WindowsMcpSample>> SampleAsync(EffectiveConfig config, CancellationToken cancellationToken)
    {
        var listed = table.List();
        if (listed is not Reading<IReadOnlyList<WindowsProcessEntry>>.Available { Value: var entries })
        {
            return Reading.Missing<WindowsMcpSample>($"the Windows process table could not be read: {listed.ReasonOrEmpty}");
        }

        var settings = McpSettings.From(config);
        var servers = WindowsMcpCatalogue.Servers(config);
        var byPid = entries.GroupBy(e => e.Pid).ToDictionary(g => g.Key, g => g.First());
        var first = new Dictionary<int, WindowsProcessDetails>();
        Reading<DateTimeOffset> Created(int pid) => FirstRead(first, pid).Created;
        var matched = entries
            .Select(e => (Entry: e, Server: WindowsMcpCatalogue.ServerOf(e.ExeName, servers)))
            .Where(m => m.Server is not null)
            .Select(m => new Matched(m.Entry, m.Server!, FirstRead(first, m.Entry.Pid)))
            .ToList();
        var agesAt = clock.GetUtcNow();
        var owners = matched.ToDictionary(m => m.Entry.Pid, m => WindowsMcpOwners.Of(m.Entry, m.First.Created, byPid, Created));
        var cpu = await CpuAsync(matched, settings.Window, cancellationToken).ConfigureAwait(false);
        List<WindowsMcpInstance> instances =
        [
            .. matched
                .Select(m => Instance(m, owners[m.Entry.Pid], cpu[m.Entry.Pid], agesAt, settings))
                .OrderByDescending(i => i.PrivateBytes.ValueOr(-1))
                .ThenBy(i => i.Pid),
        ];
        return Reading.Of(new WindowsMcpSample((int)settings.Window.TotalMilliseconds, instances, [.. instances.Take(settings.MaxInstances)], WindowsMcpOwners.Groups(instances)));
    }

    /// <summary>A matched process and what the first read of it answered.</summary>
    private sealed record Matched(WindowsProcessEntry Entry, McpServerEntry Server, WindowsProcessDetails First);

    /// <summary>One read per pid in this sample: an instance's ancestor walk and its CPU's first read see the same answer.</summary>
    private WindowsProcessDetails FirstRead(Dictionary<int, WindowsProcessDetails> first, int pid)
    {
        if (!first.TryGetValue(pid, out var details))
        {
            details = table.Details(pid);
            first[pid] = details;
        }

        return details;
    }

    /// <summary>Each instance's CPU % of one core across the window — the wait paid only when one can be measured — over the LONGER
    /// of the configured window and the measured elapsed time (as the distro's collector does).</summary>
    private async Task<IReadOnlyDictionary<int, Reading<double>>> CpuAsync(IReadOnlyList<Matched> matched, TimeSpan window, CancellationToken cancellationToken)
    {
        if (!matched.Any(m => m.First.CpuTime.IsAvailable && m.First.Created.IsAvailable))
        {
            return matched.ToDictionary(m => m.Entry.Pid, m => Unmeasured(m.First));
        }

        var started = clock.GetTimestamp();
        await wait(window, cancellationToken).ConfigureAwait(false);
        var seconds = Math.Max(window.TotalSeconds, clock.GetElapsedTime(started).TotalSeconds);
        return matched.ToDictionary(m => m.Entry.Pid, m => Rate(m.First, m.First.CpuTime.IsAvailable ? table.Details(m.Entry.Pid) : m.First, seconds));
    }

    private static Reading<double> Unmeasured(WindowsProcessDetails first) =>
        Reading.Missing<double>(first.CpuTime.IsAvailable ? first.Created.ReasonOrEmpty : first.CpuTime.ReasonOrEmpty);

    /// <summary>The CPU between the two reads — only while the pid names the same process (the same creation time).</summary>
    private static Reading<double> Rate(WindowsProcessDetails first, WindowsProcessDetails second, double seconds) =>
        Reading.Combine(first.Created, first.CpuTime, (created, before) => (created, before)).Bind(start => second.Created switch
        {
            Reading<DateTimeOffset>.Available { Value: var again } when again != start.created =>
                Reading.Missing<double>("its pid was taken by another process during the window"),
            Reading<DateTimeOffset>.Available => second.CpuTime.Map(after => Percent(after - start.before, seconds)),
            _ => Reading.Missing<double>($"it could not be read again after the window (it may have exited): {second.Created.ReasonOrEmpty}"),
        });

    private static double Percent(TimeSpan cpu, double seconds) =>
        Math.Round(McpSample.PercentPerCore * Math.Max(0, cpu.TotalSeconds) / seconds, 1);

    private static WindowsMcpInstance Instance(Matched m, WindowsMcpOwner owner, Reading<double> cpu, DateTimeOffset agesAt, McpSettings settings) =>
        new(m.Entry.Pid, m.Server.Name, owner, m.First.Created, cpu, m.First.WorkingSet, m.First.PrivateBytes, m.First.SessionId, Idle(cpu, m.First.Created, agesAt, settings));

    /// <summary>Under <c>mcpServers.idleCpuPercent</c> across the window and at least <c>mcpServers.idleMinAgeMinutes</c> old; an
    /// unmeasured CPU or an unknown age is never idle.</summary>
    private static bool Idle(Reading<double> cpu, Reading<DateTimeOffset> created, DateTimeOffset agesAt, McpSettings settings) =>
        cpu is Reading<double>.Available { Value: var percent } && percent < settings.IdleCpuPercent
        && created is Reading<DateTimeOffset>.Available { Value: var at } && agesAt - at >= settings.IdleMinAge;
}
