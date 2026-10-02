using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Docker;
using WslCare.Core.Hosting;
using WslCare.Core.Status;

namespace WslCare.Core.Preview;

// The wire shape of `preview --all --json` (plan §6): every §4.3 cleanup row with its count and reclaimable
// bytes, plus the kept named volumes. As in `status`, a figure is `"available": true` with its value or
// `"available": false` with a `reason` and NO value key (plan §15b #7) — the nullable members exist only here,
// at the JSON edge; the domain behind them holds Reading<T>.

/// <summary>The answer of <c>preview --all --json</c>.</summary>
/// <param name="Side"><c>wsl</c> or <c>windows</c>: which binary asked Docker.</param>
/// <param name="SampleMilliseconds">How long the Docker reads took.</param>
public sealed record PreviewReport(
    int SchemaVersion,
    string Side,
    DateTimeOffset SampledAt,
    long SampleMilliseconds,
    bool ObserveOnly,
    IReadOnlyList<ConfigErrorReport> ConfigError,
    DockerReport Docker,
    IReadOnlyList<PreviewRowReport> Rows,
    KeptReport Kept,
    TotalsReport Totals,
    HygieneReport Hygiene,
    VolumeSeenReport VolumeSeen);

/// <param name="Kind">Why Docker did not answer (<c>notInstalled</c>, <c>daemonStopped</c>, <c>socketRefused</c>,
/// <c>timedOut</c>, <c>commandFailed</c>, <c>refused</c>, <c>unparseable</c>); absent when it did.</param>
public sealed record DockerReport(bool Available, string? Kind, string? Reason, string? ServerVersion, string? Platform);

/// <param name="Id">The row, named as its switch (<c>A4</c>, <c>A5Testcontainers</c>, <c>A6Unused</c>, …).</param>
/// <param name="AutoSwitch">The configuration key that lets the timer run it (<c>auto.A4</c>).</param>
/// <param name="Unsized">Objects counted whose size Docker did not report — their bytes are not in the sum.</param>
/// <param name="Refusal">Why the action would refuse to run here, although the row counts; absent otherwise.</param>
public sealed record PreviewRowReport(
    string Id,
    string Action,
    string What,
    string AutoSwitch,
    bool Auto,
    string Basis,
    bool Available,
    string? Reason,
    int? Count,
    long? ReclaimableBytes,
    int? Unsized,
    IReadOnlyList<RowNoteReport>? Notes,
    string? Refusal);

public sealed record RowNoteReport(string What, int Count, ByteFigure Bytes);

/// <summary>Named volumes no container uses: kept, report-only — a person decides per volume (plan §4.3).</summary>
public sealed record KeptReport(bool Available, string? Reason, int? Count, long? Bytes, IReadOnlyList<KeptVolumeReport>? Volumes);

public sealed record KeptVolumeReport(string Name, ByteFigure Size);

/// <summary>Docker's own totals per type — the "Docker after" line.</summary>
public sealed record TotalsReport(bool Available, string? Reason, IReadOnlyList<TotalReport>? Types);

public sealed record TotalReport(string Type, int? TotalCount, int? Active, ByteFigure Size, ByteFigure Reclaimable);

/// <summary>Plan §4.5's Docker hygiene audit — report only.</summary>
public sealed record HygieneReport(UnboundedLogsReport UnboundedLogs, BuilderGcReport BuilderGc, BuildkitReport Buildkit);

public sealed record UnboundedLogsReport(bool Available, string? Reason, int? Count, IReadOnlyList<UnboundedLogReport>? Containers);

public sealed record UnboundedLogReport(string Container, string State, ByteFigure LogSize);

/// <param name="Present">Whether Docker Desktop's <c>daemon.json</c> has a <c>builder.gc</c> section.</param>
public sealed record BuilderGcReport(bool Available, string? Reason, bool? Present, string? Enabled, string? DefaultKeepStorage);

public sealed record BuildkitReport(bool Available, string? Reason, IReadOnlyList<BuildkitLeftoverReport>? Leftovers);

public sealed record BuildkitLeftoverReport(string Name, string Kind, string State, ByteFigure Size);

/// <summary>What became of the first-sighting record in this run (plan §15b #3: written only when this process
/// may write the state directory; read-only otherwise).</summary>
/// <param name="Recorded">The record was written by this run.</param>
/// <param name="Tracked">Unattached anonymous volumes the record holds after this look.</param>
/// <param name="ReadProblem">Why the stored record could not be read, when it could not.</param>
public sealed record VolumeSeenReport(string File, bool Recorded, string? Reason, int Tracked, string? ReadProblem);

/// <summary>The domain turned into the wire shape — the one place a <see cref="Reading{T}"/> of the preview
/// becomes <c>available</c> + value or <c>available: false</c> + reason.</summary>
public static class PreviewReports
{
    public static PreviewReport From(
        HostSide side,
        DockerSnapshot snapshot,
        CleanupPreview preview,
        DockerHygieneAudit hygiene,
        VolumeSeenOutcome seen,
        ConfigLoadResult loaded,
        TimeSpan elapsed) =>
        new(
            SchemaVersion.Current,
            side == HostSide.Wsl ? "wsl" : "windows",
            snapshot.SampledAt,
            (long)elapsed.TotalMilliseconds,
            loaded.IsObserveOnly,
            [.. loaded.Errors.Select(ConfigErrorReport.From)],
            Docker(snapshot.Reachability),
            [.. preview.Rows.Select(Row)],
            Kept(preview.Kept),
            Totals(snapshot.Totals),
            new HygieneReport(Logs(hygiene.UnboundedLogs), Gc(hygiene.BuilderGc), Buildkit(hygiene.Buildkit)),
            new VolumeSeenReport(seen.File, seen.Write is VolumeSeenWrite.Written, seen.Write is VolumeSeenWrite.NotWritten n ? n.Reason : null, seen.Record.Volumes.Count, seen.ReadProblem.Length == 0 ? null : seen.ReadProblem));

    private static DockerReport Docker(DockerReachability reachability) => reachability switch
    {
        DockerReachability.Reachable { Engine: var e } => new DockerReport(true, null, null, e.ServerVersion, e.Platform.Length == 0 ? null : e.Platform),
        DockerReachability.Unreachable { Problem: var p } => new DockerReport(false, Camel(p.Kind.ToString()), p.Reason, null, null),
        _ => throw new System.Diagnostics.UnreachableException("DockerReachability is a closed set"),
    };

    private static PreviewRowReport Row(CleanupRow row) => row.Figures switch
    {
        Reading<RowFigures>.Available { Value: var f } => new PreviewRowReport(
            row.Id, row.Action, row.What, row.AutoSwitch.Name, row.Auto, row.Basis, true, null, f.Count, f.Bytes, f.Unsized,
            [.. f.Notes.Select(n => new RowNoteReport(n.What, n.Count, StatusReports.Bytes(n.Bytes)))], Refusal(row.Refusal)),
        _ => new PreviewRowReport(row.Id, row.Action, row.What, row.AutoSwitch.Name, row.Auto, row.Basis, false, row.Figures.ReasonOrEmpty, null, null, null, null, Refusal(row.Refusal)),
    };

    private static KeptReport Kept(Reading<IReadOnlyList<KeptVolume>> kept) => kept switch
    {
        Reading<IReadOnlyList<KeptVolume>>.Available { Value: var list } => new KeptReport(
            true, null, list.Count, list.Sum(v => v.Bytes.ValueOr(0)), [.. list.Select(v => new KeptVolumeReport(v.Name, StatusReports.Bytes(v.Bytes)))]),
        _ => new KeptReport(false, kept.ReasonOrEmpty, null, null, null),
    };

    private static TotalsReport Totals(Reading<IReadOnlyList<DockerTotal>> totals) => totals switch
    {
        Reading<IReadOnlyList<DockerTotal>>.Available { Value: var list } => new TotalsReport(true, null, [.. list.Select(t => new TotalReport(
            t.Type, t.TotalCount.IsAvailable ? t.TotalCount.ValueOr(0) : null, t.Active.IsAvailable ? t.Active.ValueOr(0) : null, StatusReports.Bytes(t.SizeBytes), StatusReports.Bytes(t.ReclaimableBytes)))]),
        _ => new TotalsReport(false, totals.ReasonOrEmpty, null),
    };

    private static UnboundedLogsReport Logs(Reading<IReadOnlyList<UnboundedLog>> logs) => logs switch
    {
        Reading<IReadOnlyList<UnboundedLog>>.Available { Value: var list } => new UnboundedLogsReport(true, null, list.Count, [.. list.Select(l => new UnboundedLogReport(l.Container, l.State, StatusReports.Bytes(l.LogBytes)))]),
        _ => new UnboundedLogsReport(false, logs.ReasonOrEmpty, null, null),
    };

    private static BuilderGcReport Gc(Reading<BuilderGc> gc) => gc switch
    {
        Reading<BuilderGc>.Available { Value: var g } => new BuilderGcReport(true, null, g.Present, g.Enabled.Length == 0 ? null : g.Enabled, g.DefaultKeepStorage.Length == 0 ? null : g.DefaultKeepStorage),
        _ => new BuilderGcReport(false, gc.ReasonOrEmpty, null, null, null),
    };

    private static BuildkitReport Buildkit(Reading<IReadOnlyList<BuildkitLeftover>> leftovers) => leftovers switch
    {
        Reading<IReadOnlyList<BuildkitLeftover>>.Available { Value: var list } => new BuildkitReport(true, null, [.. list.Select(l => new BuildkitLeftoverReport(l.Name, l.Kind, l.State, StatusReports.Bytes(l.Bytes)))]),
        _ => new BuildkitReport(false, leftovers.ReasonOrEmpty, null),
    };

    private static string? Refusal(string refusal) => refusal.Length == 0 ? null : refusal;

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];
}

/// <summary>The first-sighting record as this run left it: what it holds now, whether it was written, and any
/// problem reading the stored one.</summary>
public sealed record VolumeSeenOutcome(string File, VolumeSeenRecord Record, VolumeSeenWrite Write, string ReadProblem);
