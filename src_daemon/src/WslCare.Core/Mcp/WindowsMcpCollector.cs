using WslCare.Core.Collectors;
using WslCare.Core.Config;

namespace WslCare.Core.Mcp;

/// <summary>
/// The Windows side's MCP server instances (E14 S7a) — READ-ONLY: it holds the process table seam and nothing that starts, signals
/// or stops a process. One snapshot names the processes; the servers (<see cref="WindowsMcpCatalogue"/>) are matched by exe name;
/// each instance is owned (<see cref="WindowsMcpOwners"/>).
/// </summary>
/// <remarks>
/// <para><b>CPU (E14 S7b.1): S1's ledger, as it is.</b> An instance whose identity — pid AND creation time — has a point in the
/// Windows binary's own ledger between <c>mcpServers.cpuIntervalMinSeconds</c> and <c>cpuIntervalMaxMinutes</c> old is measured over
/// the REAL interval since that point, on Windows' unbiased interrupt clock (<see cref="IWindowsBoot"/>), so a server that bursts
/// between two short windows is still seen. The rest are read twice, <c>mcpServers.cpuWindowMilliseconds</c> apart — waiting only
/// when one needs it — and compared only while the pid still names the process the first read saw (same creation time, coai plan
/// round 2026-10-09 finding 2). This sample's readings become the next one's baselines (S1's two-point rule).</para>
/// <para>Without a ledger, or without a boot id, every instance is measured across the window and nothing is recorded.</para>
/// </remarks>
public sealed class WindowsMcpCollector(IWindowsProcessTable table, TimeProvider clock, Func<TimeSpan, CancellationToken, Task> wait, WindowsCpuLedger? ledger = null)
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
        // coai code round 2026-10-09, finding 9: a torn snapshot may name a pid twice — one instance, never a duplicate key.
        var matched = entries
            .DistinctBy(e => e.Pid)
            .Select(e => (Entry: e, Server: WindowsMcpCatalogue.ServerOf(e.ExeName, servers)))
            .Where(m => m.Server is not null)
            .Select(m => new Matched(m.Entry, m.Server!, FirstRead(first, m.Entry.Pid)))
            .ToList();
        var agesAt = clock.GetUtcNow();
        var owners = matched.ToDictionary(m => m.Entry.Pid, m => WindowsMcpOwners.Of(m.Entry, m.First.Created, byPid, Created));
        var round = await CpuAsync(matched, settings, cancellationToken).ConfigureAwait(false);
        List<WindowsMcpInstance> instances =
        [
            .. matched
                .Select(m => Instance(m, owners[m.Entry.Pid], round.Cpu[m.Entry.Pid], agesAt, settings))
                .OrderByDescending(i => i.PrivateBytes.ValueOr(-1))
                .ThenBy(i => i.Pid),
        ];
        return Reading.Of(new WindowsMcpSample((int)settings.Window.TotalMilliseconds, instances, [.. instances.Take(settings.MaxInstances)], WindowsMcpOwners.Groups(instances))
        {
            Baseline = Record(round, settings),
        });
    }

    /// <summary>A matched process and what the first read of it answered.</summary>
    private sealed record Matched(WindowsProcessEntry Entry, McpServerEntry Server, WindowsProcessDetails First);

    /// <summary>One instance's CPU: the figure, over what it was measured, and how long.</summary>
    private sealed record Cpu(Reading<double> Percent, McpCpuBasis Basis, TimeSpan Over);

    /// <summary>The round's CPU per pid, the boot it was read in, the ledger before it, and this sample's readings for the next one.</summary>
    private sealed record CpuRound(IReadOnlyDictionary<int, Cpu> Cpu, string Boot, McpCpuFile Before, IReadOnlyList<McpCpuReading> Readings);

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

    /// <summary>Each instance's CPU: over the interval since its ledger point when it has one in bounds, otherwise across the window.</summary>
    private async Task<CpuRound> CpuAsync(IReadOnlyList<Matched> matched, McpSettings settings, CancellationToken cancellationToken)
    {
        var boot = ledger?.Boot.BootId() is Reading<string>.Available { Value: var id } ? id : string.Empty;
        var before = ledger is null || boot.Length == 0 ? McpCpuFile.Empty : McpCpuLedger.Read(ledger.Files, ledger.Place, settings.LedgerMaxBytes);
        var at = new McpCpuPoint(0, clock.GetUtcNow(), ledger?.Boot.UnbiasedMilliseconds() ?? 0);
        var readings = matched.Select(m => ReadingOf(m, at)).OfType<McpCpuReading>().ToList();
        var fromLedger = readings
            .Select(r => (r.Pid, Cpu: McpCpuLedger.Baseline(before, boot, r, settings.Bounds) is { } from ? Interval(r.At, from) : null))
            .Where(r => r.Cpu is not null)
            .ToDictionary(r => r.Pid, r => r.Cpu!);
        var windowed = await WindowAsync([.. matched.Where(m => !fromLedger.ContainsKey(m.Entry.Pid))], settings.Window, cancellationToken).ConfigureAwait(false);
        return new CpuRound(matched.ToDictionary(m => m.Entry.Pid, m => fromLedger.GetValueOrDefault(m.Entry.Pid) ?? windowed[m.Entry.Pid]), boot, before, readings);
    }

    /// <summary>This instance as the ledger keys it — pid, creation time as FILETIME ticks, CPU in 100-ns ticks — or <c>null</c> when its
    /// creation time or CPU could not be read (nothing can name it across samples).</summary>
    private static McpCpuReading? ReadingOf(Matched m, McpCpuPoint at) =>
        (m.First.Created, m.First.CpuTime) is (Reading<DateTimeOffset>.Available { Value: var created }, Reading<TimeSpan>.Available { Value: var cpu })
            ? new McpCpuReading(m.Entry.Pid, created.UtcDateTime.ToFileTimeUtc(), at with { CpuTicks = cpu.Ticks })
            : null;

    /// <summary>The CPU between the ledger point and now, over the unbiased clock's interval (S1's rule: the monotonic clock is the
    /// denominator).</summary>
    private static Cpu Interval(McpCpuPoint now, McpCpuPoint from)
    {
        var over = TimeSpan.FromMilliseconds(now.MonotonicMs - from.MonotonicMs);
        return new Cpu(Reading.Of(Percent(TimeSpan.FromTicks(now.CpuTicks - from.CpuTicks), over.TotalSeconds)), McpCpuBasis.Interval, over);
    }

    /// <summary>Each given instance's CPU % of one core across the window — the wait paid only when one can be measured — over the
    /// LONGER of the configured window and the measured elapsed time (as the distro's collector does).</summary>
    private async Task<IReadOnlyDictionary<int, Cpu>> WindowAsync(IReadOnlyList<Matched> matched, TimeSpan window, CancellationToken cancellationToken)
    {
        if (!matched.Any(m => m.First.CpuTime.IsAvailable && m.First.Created.IsAvailable))
        {
            return matched.ToDictionary(m => m.Entry.Pid, m => new Cpu(Unmeasured(m.First), McpCpuBasis.None, TimeSpan.Zero));
        }

        var started = clock.GetTimestamp();
        await wait(window, cancellationToken).ConfigureAwait(false);
        var seconds = Math.Max(window.TotalSeconds, clock.GetElapsedTime(started).TotalSeconds);
        return matched.ToDictionary(m => m.Entry.Pid, m => Windowed(Rate(m.First, m.First.CpuTime.IsAvailable ? table.Details(m.Entry.Pid) : m.First, seconds), seconds));
    }

    private static Cpu Windowed(Reading<double> rate, double seconds) =>
        rate.IsAvailable ? new Cpu(rate, McpCpuBasis.Window, TimeSpan.FromSeconds(seconds)) : new Cpu(rate, McpCpuBasis.None, TimeSpan.Zero);

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

    /// <summary>This sample's readings into the ledger by S1's two-point rule — or why they are not recorded.</summary>
    private McpCpuBaseline Record(CpuRound round, McpSettings settings) => (ledger, round) switch
    {
        (null, _) => McpCpuBaseline.NotRecorded(string.Empty, "no CPU ledger"),
        ({ } l, { Boot.Length: 0 }) => McpCpuBaseline.NotRecorded(l.Place.FileOrEmpty, "the Windows boot id cannot be read, so no reading can name its process across samples"),
        ({ } l, { Readings.Count: 0 }) => McpCpuBaseline.NotRecorded(l.Place.FileOrEmpty, "no MCP server instance to record"),
        ({ } l, _) => McpCpuLedger.Record(l.Files, l.Place, round.Before, McpCpuLedger.Next(round.Before, round.Boot, round.Readings, settings.Bounds, McpCpuLedger.MaxEntries(settings.LedgerMaxBytes)), new McpCpuSweep(clock.GetUtcNow(), settings.Bounds.Min)),
    };

    private static WindowsMcpInstance Instance(Matched m, WindowsMcpOwner owner, Cpu cpu, DateTimeOffset agesAt, McpSettings settings) =>
        new(m.Entry.Pid, m.Server.Name, owner, m.First.Created, cpu.Percent, m.First.WorkingSet, m.First.PrivateBytes, m.First.SessionId, Idle(cpu.Percent, m.First.Created, agesAt, settings))
        {
            CpuBasis = cpu.Basis,
            CpuOver = cpu.Over,
        };

    /// <summary>Under <c>mcpServers.idleCpuPercent</c> over whichever basis answered and at least <c>mcpServers.idleMinAgeMinutes</c>
    /// old; an unmeasured CPU or an unknown age is never idle.</summary>
    private static bool Idle(Reading<double> cpu, Reading<DateTimeOffset> created, DateTimeOffset agesAt, McpSettings settings) =>
        cpu is Reading<double>.Available { Value: var percent } && percent < settings.IdleCpuPercent
        && created is Reading<DateTimeOffset>.Available { Value: var at } && agesAt - at >= settings.IdleMinAge;
}
