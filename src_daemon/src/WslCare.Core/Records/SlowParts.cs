
using WslCare.Core.Collectors;
using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Records;

/// <summary>One container's line of <c>docker stats --no-stream</c> (plan §4.2), as a full run stores it.</summary>
public sealed record ContainerStat(string Id, string Name, long MemoryBytes, double CpuPercent);

/// <summary><c>docker stats</c> as one full run sampled it — or why it could not.</summary>
/// <param name="Unavailable">Empty when sampled; otherwise the reason (daemon stopped, socket refused,
/// timeout — plan §15b #7).</param>
public sealed record ContainerStatsSample(DateTimeOffset SampledAt, IReadOnlyList<ContainerStat> Containers, string Unavailable);

/// <summary>The distro clock against Windows' (plan §4.5, §15b #5): the measured offset with the
/// launch latency of the probe already subtracted, and that latency.</summary>
public sealed record WindowsClockSample(DateTimeOffset SampledAt, double OffsetSeconds, double LaunchLatencySeconds, string Unavailable)
{
    /// <summary>The Windows user profile the probe printed (<c>C:\Users\…</c>); empty when unknown or on a line
    /// written before E2.S3 (which reads as <c>null</c> under the source generator — read through
    /// <see cref="Profile"/>).</summary>
    public string? WindowsProfile { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string Profile => WindowsProfile ?? string.Empty;

    /// <summary>Whether this is a measured observation (and not the reason there is none).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Measured => string.IsNullOrEmpty(Unavailable);
}

/// <summary>One folder a full run measured (plan §4.4 and the A8 / A9 rows of §4.3) — or why not.</summary>
/// <param name="Id">Stable: <c>npm-cache</c>, <c>apt-cache</c>, <c>snap-disabled</c>, <c>git-worktrees</c>,
/// <c>nuget-packages</c>, <c>user-cache</c>, <c>vscode-server</c>, <c>git-build-output</c>.</param>
/// <param name="Complete">The walk reached its end; <c>false</c> when a limit stopped it (a lower bound).</param>
/// <param name="Unavailable">Empty when measured; otherwise the reason (missing, unreadable, a link).</param>
public sealed record FolderSize(string Id, string Path, long Bytes, long Files, bool Complete, string Unavailable)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Measured => string.IsNullOrEmpty(Unavailable);
}

/// <summary>The daily folder sizes of plan §4.4, as one full run measured them.</summary>
public sealed record FolderSizesSample(DateTimeOffset SampledAt, IReadOnlyList<FolderSize> Folders)
{
    /// <summary>The folder of that id, or <c>null</c> — a legitimate "not measured".</summary>
    public FolderSize? Find(string id) => (Folders ?? []).FirstOrDefault(f => f?.Id == id);
}

/// <summary>
/// The parts of a sample that need a SLOW process — <c>docker stats</c> and <c>powershell.exe
/// Get-Date</c> — and so are taken only by <c>collect</c> (plan §15b #5). A full run's
/// <c>history.jsonl</c> line carries them; <c>status</c> reads them back with their age.
/// </summary>
/// <remarks>E2.S1 fixes the shape and the reader; <c>collect</c> (E2.S3) and the Docker collector
/// (E2.S2) are the writers. Either member is absent from a run that did not sample it, and a missing
/// member reads as <c>null</c> under the source generator whatever the declaration says (C# doctrine
/// §4a), so every read goes through <see cref="LastFullRun"/>, which treats both the same.</remarks>
public sealed record SlowParts
{
    public ContainerStatsSample? ContainerStats { get; init; }

    public WindowsClockSample? WindowsClock { get; init; }

    /// <summary>The daily folder sizes (plan §4.4) — present only on the run that measured them (once a day).</summary>
    public FolderSizesSample? Folders { get; init; }

    /// <summary>The AI agents' data folders (plan §4.6, §15q D1) — totals, counts and dates only, never a session's name; present
    /// only on the run that walked them (with the folders, once a day).</summary>
    public Agents.AgentsSample? Agents { get; init; }
}

/// <summary>A slow part as <c>status</c> reports it: the value, the run it came from, when it was
/// sampled and how old it is — or why there is none.</summary>
public sealed record AgedPart<T>(T Value, RunId RunId, DateTimeOffset SampledAt, TimeSpan Age);

/// <summary>The slow parts of the newest full run that sampled each one.</summary>
public sealed record LastSlowParts(Reading<AgedPart<ContainerStatsSample>> ContainerStats, Reading<AgedPart<WindowsClockSample>> WindowsClock)
{
    /// <summary>The folder sizes of the newest run that measured them; unavailable before the first.</summary>
    public Reading<AgedPart<FolderSizesSample>> Folders { get; init; } = Reading.Missing<AgedPart<FolderSizesSample>>(LastFullRun.NoFullRunYet);

    /// <summary>The sample before <see cref="Folders"/> — what "grew since yesterday" (plan §4.5) is measured against.</summary>
    public Reading<FolderSizesSample> PreviousFolders { get; init; } = Reading.Missing<FolderSizesSample>("no earlier folder sample is recorded");

    /// <summary>The agents' folders of the newest run that walked them (plan §4.6); unavailable before the first.</summary>
    public Reading<AgedPart<Agents.AgentsSample>> Agents { get; init; } = Reading.Missing<AgedPart<Agents.AgentsSample>>(LastFullRun.NoFullRunYet);

    /// <summary>The agents' sample before <see cref="Agents"/> — what an agent's growth is measured against.</summary>
    public Reading<Agents.AgentsSample> PreviousAgents { get; init; } = Reading.Missing<Agents.AgentsSample>("no earlier agent sample is recorded");
}

/// <summary>
/// Reads the slow parts back from <c>history.jsonl</c>: for each part, the newest line that carries it.
/// A line that does not parse is skipped (plan §6: a torn last line is the residual of the locked
/// append); a missing history is the ordinary state before the first full run.
/// </summary>
public static class LastFullRun
{
    public const string NoFullRunYet = "no full run has been recorded yet; \"wsl-care collect\" (run as root, or by the timer) records one";

    public static LastSlowParts Read(IHostPaths paths, IFileSystem files, TimeProvider clock) =>
        From(RunHistory.Read(paths, files), clock.GetUtcNow());

    /// <summary>The slow parts of a history already read (<c>status</c> reads it once, for these and for its verdicts).</summary>
    public static LastSlowParts From(HistoryRead history, DateTimeOffset now) =>
        history.Problem.Length > 0
            ? Unreadable(history.Problem)
            : history.Records.Count == 0
                ? Unreadable(NoFullRunYet)
                : FromRecords([.. history.Records.Reverse()], now);

    /// <summary>The newest part of each kind in <paramref name="newestFirst"/>, aged against <paramref name="now"/>.</summary>
    public static LastSlowParts FromRecords(IReadOnlyList<RunRecord> newestFirst, DateTimeOffset now) =>
        new(
            Newest(newestFirst, r => r.Slow?.ContainerStats, s => s.SampledAt, s => s.Unavailable, now, "docker stats"),
            Newest(newestFirst, r => r.Slow?.WindowsClock, s => s.SampledAt, s => s.Unavailable, now, "the Windows clock"))
        {
            Folders = Newest(newestFirst, r => r.Slow?.Folders, s => s.SampledAt, _ => string.Empty, now, "the folder sizes"),
            PreviousFolders = newestFirst.Select(r => r.Slow?.Folders).Where(f => f is not null).Skip(1).FirstOrDefault() is { } previous
                ? Reading.Of(previous)
                : Reading.Missing<FolderSizesSample>("no earlier folder sample is recorded"),
            Agents = Newest(newestFirst, r => r.Slow?.Agents, s => s.SampledAt, _ => string.Empty, now, "the AI agents' folders"),
            PreviousAgents = newestFirst.Select(r => r.Slow?.Agents).Where(a => a is not null).Skip(1).FirstOrDefault() is { } previousAgents
                ? Reading.Of(previousAgents)
                : Reading.Missing<Agents.AgentsSample>("no earlier agent sample is recorded"),
        };

    private static Reading<AgedPart<T>> Newest<T>(IReadOnlyList<RunRecord> newestFirst, Func<RunRecord, T?> part, Func<T, DateTimeOffset> sampledAt, Func<T, string?> unavailable, DateTimeOffset now, string what)
        where T : class
    {
        var record = newestFirst.FirstOrDefault(r => part(r) is not null);
        if (record is null)
        {
            return Reading.Missing<AgedPart<T>>($"no recorded full run has sampled {what} yet");
        }

        var value = part(record)!;
        var reason = unavailable(value) ?? string.Empty;
        return reason.Length == 0
            ? Reading.Of(new AgedPart<T>(value, record.RunId, sampledAt(value), now - sampledAt(value)))
            : Reading.Missing<AgedPart<T>>($"the last full run that tried ({record.RunId}) could not sample {what}: {reason}");
    }

    private static LastSlowParts Unreadable(string reason) =>
        new(Reading.Missing<AgedPart<ContainerStatsSample>>(reason), Reading.Missing<AgedPart<WindowsClockSample>>(reason))
        {
            Folders = Reading.Missing<AgedPart<FolderSizesSample>>(reason),
            Agents = Reading.Missing<AgedPart<Agents.AgentsSample>>(reason),
        };
}
