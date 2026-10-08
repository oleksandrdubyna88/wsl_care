using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using WslCare.Core.Config;
using WslCare.Core.Json;

namespace WslCare.Core.Archive;

/// <summary>A child's answer as root judged it: well formed and in range, or why not — a closed choice.</summary>
public abstract record ChildAnswer<T>
{
    private ChildAnswer()
    {
    }

    public sealed record Valid(T Value) : ChildAnswer<T>;

    public sealed record Invalid(string Why) : ChildAnswer<T>;
}

/// <summary>
/// The archive children's answers, judged before root takes ONE figure from them (plan §15r D8: "it is the user's process, its words
/// are data"; risk consult 9/9.4 #3): the schema version, the closed sets (outcome, stop kind, agent ids), every count and size not
/// negative and within the bounds the run itself keeps. Root reads COUNTS only — never a key, a note or a first-skipped name — so
/// nothing a child says reaches root's world-readable run detail as a session's name, a path or a process to act on.
/// </summary>
public static class ArchiveChildAnswers
{
    private static readonly IReadOnlySet<string> Outcomes = new HashSet<string>(StringComparer.Ordinal)
    {
        RunOutcomes.Done, RunOutcomes.Stopped, RunOutcomes.NoBase, RunOutcomes.Refused, RunOutcomes.Unreachable, RunOutcomes.Busy,
    };

    private static readonly IReadOnlySet<string> StopKindSet = new HashSet<string>(StringComparer.Ordinal)
    {
        StopKinds.None, StopKinds.Budget, StopKinds.SessionCap, StopKinds.FreeSpace, StopKinds.Cancelled, StopKinds.Verification, StopKinds.StateWrite, StopKinds.IndexWrite, StopKinds.BaseFailed,
    };

    public static ChildAnswer<ArchiveRunReport> Run(string json, EffectiveConfig config) =>
        Parsed(json, WslCareJsonContext.Default.ArchiveRunReport) switch
        {
            ChildAnswer<ArchiveRunReport>.Valid { Value: var report } when RunProblem(report, config) is { Length: > 0 } problem => new ChildAnswer<ArchiveRunReport>.Invalid(problem),
            var parsed => parsed,
        };

    public static ChildAnswer<ArchivePreviewReport> Preview(string json, EffectiveConfig config) =>
        Parsed(json, WslCareJsonContext.Default.ArchivePreviewReport) switch
        {
            ChildAnswer<ArchivePreviewReport>.Valid { Value: var report } when PreviewProblem(report) is { Length: > 0 } problem => new ChildAnswer<ArchivePreviewReport>.Invalid(problem),
            var parsed => parsed,
        };

    public static ChildAnswer<ArchiveListReport> List(string json, EffectiveConfig config) =>
        Parsed(json, WslCareJsonContext.Default.ArchiveListReport) switch
        {
            ChildAnswer<ArchiveListReport>.Valid { Value: var report } when ListProblem(report) is { Length: > 0 } problem => new ChildAnswer<ArchiveListReport>.Invalid(problem),
            var parsed => parsed,
        };

    private static ChildAnswer<T> Parsed<T>(string json, JsonTypeInfo<T> type)
    {
        try
        {
            return JsonSerializer.Deserialize(json, type) is { } value && SchemaOf(value) == SchemaVersion.Current
                ? new ChildAnswer<T>.Valid(value)
                : new ChildAnswer<T>.Invalid($"it is not an answer of schema version {SchemaVersion.Current}");
        }
        catch (JsonException e)
        {
            return new ChildAnswer<T>.Invalid($"it is not valid JSON ({e.Message})");
        }
    }

    private static int SchemaOf<T>(T value) => value switch
    {
        ArchiveRunReport run => run.SchemaVersion,
        ArchivePreviewReport preview => preview.SchemaVersion,
        ArchiveListReport list => list.SchemaVersion,
        _ => -1,
    };

    /// <summary>Why a run's answer cannot be believed; empty when every figure root reads is in shape.</summary>
    private static string RunProblem(ArchiveRunReport report, EffectiveConfig config)
    {
        var sessions = config.Int(ConfigKeys.Archive.MaxSessionsPerRun);
        return FirstOf(
            () => report.Agents is null || report.Restore?.Sessions is null ? "a part of it is missing" : string.Empty,
            () => Outcomes.Contains(report.Outcome ?? string.Empty) && StopKindSet.Contains(report.StopKind ?? string.Empty) ? string.Empty : "its outcome or stop kind is not one the archive answers",
            () => report.Agents.Select(a => AgentProblem(a, sessions)).FirstOrDefault(p => p.Length > 0, string.Empty),
            () => new[] { report.FilesPerSecond, report.MegabytesPerSecond }.Any(r => !double.IsFinite(r) || r < 0) ? "a rate is not a finite, non-negative number" : string.Empty,
            () => RestoreProblem(report.Restore, config));
    }

    private static string AgentProblem(AgentRunReport? agent, int sessions) =>
        agent is null || !IsAgentId(agent.Id)
            ? "an agent is not one the archive moves"
            : FirstOf(
                () => new long[] { agent.Copied, agent.Removed, agent.GoneAtSource, agent.Superseded, agent.Damaged, agent.Waiting }.Any(n => n < 0 || n > sessions) ? $"{agent.Id}: a session count is outside 0..{sessions}" : string.Empty,
                () => agent.CopiedFiles < 0 || agent.CopiedBytes < 0 || agent.RemovedBytes < 0 ? $"{agent.Id}: a file count or a size is negative" : string.Empty,
                () => agent.Skipped is null || agent.Skipped.Any(s => s is null || s.Count < 0 || s.Count > sessions) ? $"{agent.Id}: a skip count is out of shape" : string.Empty);

    private static string RestoreProblem(RestoreReport restore, EffectiveConfig config)
    {
        var most = config.Int(ConfigKeys.Archive.MaxRestoreEntries);
        return FirstOf(
            () => new[] { restore.Restored, restore.AlreadyThere, restore.Refused }.Any(n => n < 0 || n > most) || restore.Sessions.Count > most ? $"a restore count is outside 0..{most}" : string.Empty,
            () => restore.Sessions.Any(s => s is null || !ArchiveIndex.IsEntryId(s.EntryId ?? string.Empty) || s.Files < 0 || s.Bytes < 0) ? "a restored session is out of shape" : string.Empty);
    }

    private static string PreviewProblem(ArchivePreviewReport report) =>
        FirstOf(
            () => report.Agents is null || report.Agents.Any(a => a is null || !IsAgentId(a.Id) || a.Retention is null) ? "an agent is missing or is not one the archive moves" : string.Empty,
            () => report.RemovalsDue < 0 || report.Agents.Any(a => a.DueUnits < 0 || a.DueFiles < 0 || a.DueBytes < 0 || a.Quarantined < 0 || a.Younger < 0 || a.Retention.Days < 0 || a.EffectiveAgeDays < 0) ? "a count or a size is negative" : string.Empty);

    private static string ListProblem(ArchiveListReport report) =>
        report.Entries is null || report.Entries.Any(e => e is null || !ArchiveIndex.IsEntryId(e.EntryId ?? string.Empty) || !IsAgentId(e.Agent) || e.Files < 0 || e.Bytes < 0 || e.Status is null || e.Month is null) ? "an entry is out of shape" : string.Empty;

    /// <summary>The first problem of <paramref name="checks"/>, in order — each asked only once those before it found none, so a check
    /// may rely on the ones before it (a part that is not missing); empty when none finds one.</summary>
    private static string FirstOf(params Func<string>[] checks) =>
        checks.Select(check => check()).FirstOrDefault(problem => problem.Length > 0, string.Empty);

    /// <summary>One of the agents the archive may move: an archivable catalogue id, or a manual agent's <c>extra:&lt;name&gt;</c>.</summary>
    private static bool IsAgentId(string? id) =>
        id is { Length: > 0 } && !id.Contains(',', StringComparison.Ordinal) && ConfigValidation.Parse(ConfigKeys.Archive.Agents, id) is ValueCheck.Ok;
}
