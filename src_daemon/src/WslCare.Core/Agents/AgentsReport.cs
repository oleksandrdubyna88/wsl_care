using WslCare.Core.Config;
using WslCare.Core.Records;
using WslCare.Core.Status;

namespace WslCare.Core.Agents;

/// <summary>Where the sizes of an <c>agents list</c> answer come from: measured <c>now</c> (<c>--measure</c>), the newest
/// <c>fullRun</c> that walked them (with its age), or <c>none</c> with the reason.</summary>
public sealed record AgentSizesSource(string Source, DateTimeOffset? SampledAt, double? AgeSeconds, string? RunId, string? Reason)
{
    /// <summary>The wire shape read back as a closed set (coai E7 code round #2): the nullable members exist only at the JSON edge.</summary>
    public SizesView View() => (Source, RunId, AgeSeconds, Reason) switch
    {
        ("now", _, _, _) => new SizesView.MeasuredNow(),
        ("fullRun", { } runId, { } age, _) => new SizesView.FullRun(runId, age),
        (_, _, _, { } reason) => new SizesView.Unavailable(reason),
        _ => new SizesView.Unavailable("none"),
    };
}

/// <summary>Where an answer's sizes come from, as a reader takes it — measured now, a full run with its age, or unavailable and why.</summary>
public abstract record SizesView
{
    private SizesView()
    {
    }

    public sealed record MeasuredNow : SizesView;

    public sealed record FullRun(string RunId, double AgeSeconds) : SizesView;

    public sealed record Unavailable(string Reason) : SizesView;
}

/// <summary>An agent's session count as a reader takes it (coai E7 code round #3): a count exists only when counted.</summary>
public abstract record SessionCount
{
    private SessionCount()
    {
    }

    public sealed record Counted(int Count) : SessionCount;

    public sealed record NotCounted(string Reason) : SessionCount;
}

public sealed record AgentBinaryReport(string Name, string Path);

/// <summary>One data folder in the answer.</summary>
public sealed record AgentFolderReport(string Path, bool Exists, ByteFigure Size, long? Files, bool? Complete, IReadOnlyList<string> Excluded);

public sealed record AgentSessionReport(string Name, long Bytes);

/// <summary>An agent's sessions: <c>counted</c> false is "—" with the reason, never 0 (plan §15q D2). The largest sessions by
/// NAME only in a live answer (<c>--measure</c>).</summary>
public sealed record AgentSessionsReport(bool Counted, int? Count, DateTimeOffset? Oldest, DateTimeOffset? Newest, ByteFigure Largest, IReadOnlyList<AgentSessionReport>? LargestSessions, bool? Complete, string? Reason)
{
    /// <summary>The wire shape read back as a closed set: a count only when counted, else the reason.</summary>
    public SessionCount View() => (Counted, Count, Reason) switch
    {
        (true, { } count, _) => new SessionCount.Counted(count),
        (_, _, { } reason) => new SessionCount.NotCounted(reason),
        _ => new SessionCount.NotCounted("not counted"),
    };
}

/// <summary>One catalogue agent as <c>agents list --json</c> answers it (plan §4.6).</summary>
public sealed record AgentReport(
    string Id,
    string Name,
    bool Tracked,
    bool Confirmed,
    IReadOnlyList<string> DetectedBy,
    IReadOnlyList<AgentBinaryReport> Binaries,
    TextFigure Version,
    IReadOnlyList<AgentFolderReport> DataFolders,
    AgentSessionsReport Sessions,
    ByteFigure TotalBytes,
    ByteFigure GrowthBytes,
    IReadOnlyList<string> Warnings);

/// <summary>The answer of <c>agents list [--measure] --json</c> (plan §6, §15q E7.S1).</summary>
public sealed record AgentsReport(int SchemaVersion, string Side, DateTimeOffset AnsweredAt, AgentSizesSource Sizes, IReadOnlyList<AgentReport> Agents);

/// <summary>Where the sizes of one answer come from — a closed set.</summary>
public abstract record AgentSizes
{
    private AgentSizes()
    {
    }

    /// <summary>Measured by this very call (<c>agents list --measure</c>).</summary>
    public sealed record Now(AgentsSample Sample) : AgentSizes;

    /// <summary>The newest full run that walked the folders, with its age.</summary>
    public sealed record FullRun(AgedPart<AgentsSample> Recorded) : AgentSizes;

    /// <summary>No measurement, and why.</summary>
    public sealed record None(string Reason) : AgentSizes;

    public AgentsSample? Measured => this switch
    {
        Now now => now.Sample,
        FullRun run => run.Recorded.Value,
        _ => null,
    };
}

/// <summary>Discovery joined with a measured sample into the wire shape — one place where an unmeasured figure becomes
/// <c>available: false</c> with its reason, never 0.</summary>
public static class AgentsReports
{
    private const long Mb = 1024L * 1024;

    private const long Gb = 1024L * Mb;

    public static AgentsReport From(string side, DateTimeOffset now, IReadOnlyList<AgentPresence> found, AgentSizes sizes, AgentsSample? previous, EffectiveConfig config) =>
        new(SchemaVersion.Current, side, now, Source(sizes), [.. found.Select(p => Agent(p, sizes.Measured, previous, config))]);

    private static AgentSizesSource Source(AgentSizes sizes) => sizes switch
    {
        AgentSizes.Now now => new("now", now.Sample.SampledAt, 0, null, null),
        AgentSizes.FullRun run => new("fullRun", run.Recorded.SampledAt, run.Recorded.Age.TotalSeconds, run.Recorded.RunId.Text, null),
        AgentSizes.None none => new("none", null, null, null, none.Reason),
        _ => throw new System.Diagnostics.UnreachableException("AgentSizes is a closed set"),
    };

    private static AgentReport Agent(AgentPresence presence, AgentsSample? sample, AgentsSample? previous, EffectiveConfig config)
    {
        var size = sample?.Find(presence.Entry.Id);
        var before = previous?.Find(presence.Entry.Id);
        var total = size is null ? Unmeasured(sample) : Total(size);
        return new AgentReport(
            presence.Entry.Id,
            presence.Entry.Name,
            presence.Tracked,
            presence.Entry.Confirmed,
            presence.DetectedBy,
            [.. presence.Binaries.Select(b => new AgentBinaryReport(b.Name, b.Path))],
            presence.Version.Length > 0 ? new TextFigure(true, presence.Version, null) : new TextFigure(false, null, presence.VersionReason),
            [.. presence.Folders.Select(f => Folder(f, size, sample))],
            Sessions(size, sample),
            total,
            Growth(size, before),
            Warnings(presence.Entry, size, config));
    }

    /// <summary>The agent's bytes — or, when a folder of it was not measured (the budget did not reach it, it could not be read, the
    /// rules refused a manual agent), unavailable with that folder's reason: a part is never presented as the whole, nor 0 as
    /// a measurement. A folder that does not exist holds nothing and counts as 0.</summary>
    public static ByteFigure Total(AgentSize size) =>
        size.Folders.FirstOrDefault(f => !Counted(f)) is { } unmeasured ? new ByteFigure(false, null, unmeasured.Reason)
        : size.Folders.FirstOrDefault(f => f.Exists && !f.Complete) is { } cut ? new ByteFigure(true, size.TotalBytes, cut.Reason)
        : new ByteFigure(true, size.TotalBytes, null);

    /// <summary>Now minus the previous walk — only when BOTH walks were whole (review R2: a lower bound minus a whole figure is
    /// no growth).</summary>
    private static ByteFigure Growth(AgentSize? now, AgentSize? before) =>
        now is null || before is null ? new ByteFigure(false, null, "no earlier measurement of this agent to compare with")
        : !now.Whole || !before.Whole || Total(now).Reason is not null ? new ByteFigure(false, null, "growth is taken between two whole walks; one of them was not whole")
        : new ByteFigure(true, now.TotalBytes - before.TotalBytes, null);

    private static bool Counted(AgentFolderSize folder) =>
        folder.Exists
            ? folder.Reason.Length == 0 || folder.Reason.Contains("lower bound", StringComparison.Ordinal)
            : folder.Reason.EndsWith("does not exist", StringComparison.Ordinal);

    private static ByteFigure Unmeasured(AgentsSample? sample) =>
        new(false, null, sample is null ? "not measured yet: the daily full run walks the agents' folders, or ask agents list --measure" : "not tracked when the folders were walked");

    private static AgentFolderReport Folder(string path, AgentSize? size, AgentsSample? sample) =>
        size?.Folders.FirstOrDefault(f => f.Path == path) is { } measured
            ? FolderOf(measured)
            : new AgentFolderReport(path, false, Unmeasured(sample), null, null, []);

    /// <summary>One measured folder in the wire shape (also <c>agents probe</c>'s).</summary>
    public static AgentFolderReport FolderOf(AgentFolderSize measured) =>
        FolderBytes(measured) is { Available: true } bytes
            ? new(measured.Path, measured.Exists, bytes, measured.Files, measured.Complete, measured.Excluded)
            : new(measured.Path, measured.Exists, FolderBytes(measured), null, measured.Complete, measured.Excluded);

    /// <summary>A folder's bytes: whole; a lower bound with the ceiling that stopped the walk; or unavailable with the reason
    /// (missing, unreadable, not reached) — never 0 for a figure that was not taken.</summary>
    private static ByteFigure FolderBytes(AgentFolderSize measured) =>
        measured.Exists && (measured.Reason.Length == 0 || measured.Reason.Contains("lower bound", StringComparison.Ordinal))
            ? new ByteFigure(true, measured.Bytes, measured.Reason.Length == 0 ? null : measured.Reason)
            : new ByteFigure(false, null, measured.Reason);

    private static AgentSessionsReport Sessions(AgentSize? size, AgentsSample? sample) => size?.Sessions switch
    {
        { Counted: true } s => new(true, s.Count, s.Oldest, s.Newest, new ByteFigure(true, s.LargestBytes, null), size!.Largest?.Select(l => new AgentSessionReport(l.Name, l.Bytes)).ToList(), s.Complete, s.Reason.Length == 0 ? null : s.Reason),
        { } s => new(false, null, null, null, new ByteFigure(false, null, s.Reason), null, null, s.Reason),
        null => new(false, null, null, null, Unmeasured(sample), null, null, Unmeasured(sample).Reason),
    };

    /// <summary>Plan §4.6: an agent over <c>aiAgents.warnGb</c>, or a single session over <c>aiAgents.sessionWarnMb</c>.</summary>
    private static IReadOnlyList<string> Warnings(AgentEntry entry, AgentSize? size, EffectiveConfig config)
    {
        if (size is null)
        {
            return [];
        }

        var warnGb = config.Int(ConfigKeys.AiAgents.WarnGb);
        var sessionMb = config.Int(ConfigKeys.AiAgents.SessionWarnMb);
        IEnumerable<string> over = size.TotalBytes > warnGb * Gb ? [$"{entry.Name} holds {size.TotalBytes / (double)Gb:0.0} GiB, over aiAgents.warnGb ({warnGb})"] : [];
        IEnumerable<string> session = size.Sessions.Counted && size.Sessions.LargestBytes > sessionMb * Mb ? [$"one {entry.Name} session holds {size.Sessions.LargestBytes / (double)Mb:0} MiB, over aiAgents.sessionWarnMb ({sessionMb})"] : [];
        return [.. over, .. session];
    }
}
