using static WslCare.Cli.CommandLine;

namespace WslCare.Cli;

/// <summary>
/// How the archive verbs' arguments are read and judged (plan §15r E9.S0–E9.S4): <c>archive check-base</c>, <c>preview</c>, <c>run</c>,
/// <c>restore</c>, <c>list</c> and <c>reconcile</c> — each answers its <see cref="Request"/> or the usage error naming the rule. The
/// register of every verb stays in <see cref="CommandLine"/>; this unit holds the archive's own flags and rules (the E9.S4 own review
/// round C-9 and its gate round: <c>CommandLine.cs</c> had passed 800 lines, and a <c>partial</c> split is not a unit).
/// </summary>
internal static class ArchiveArguments
{
    /// <summary>Optionally <c>--agent &lt;id&gt;</c> — one value <c>archive.agents</c> could hold: an archivable catalogue id or
    /// <c>manual:&lt;name&gt;</c> — and <c>--json</c>.</summary>
    internal static Request ParseArchivePreview(IReadOnlyList<string> rest) =>
        ReadOptions("archive preview", rest, [AgentFlag], [JsonFlag]) switch
        {
            (_, { } failure) => failure,
            var (options, _) when options.Values.TryGetValue(AgentFlag, out var agent) && (agent.Contains(',', StringComparison.Ordinal) || Core.Config.ConfigValidation.Parse(Core.Config.ConfigKeys.Archive.Agents, agent) is not Core.Config.ValueCheck.Ok) =>
                new Request.Failed($"\"{BinaryName} archive preview --agent\" takes one of {string.Join(", ", Core.Agents.AgentCatalogue.ArchivableIds)} or {Core.Agents.ExtraAgent.IdPrefix}<name>; got \"{Printable(agent)}\"."),
            var (options, _) => new Request.ArchivePreview(options.Values.GetValueOrDefault(AgentFlag, string.Empty), options.Flags.Contains(JsonFlag)),
        };

    private const string BudgetFlag = "--budget-seconds";
    private const string RunIdFlag = "--run-id";
    private const string RestorableFlag = "--restorable";
    private const string ScanFlag = "--scan";

    /// <summary>Optionally <c>--agent &lt;id&gt;</c> (as <c>archive preview</c>), <c>--budget-seconds &lt;n&gt;</c> (1 to the most
    /// <c>archive.runBudgetMinutes</c> allows) and <c>--json</c>.</summary>
    internal static Request ParseArchiveRun(IReadOnlyList<string> rest) =>
        ReadOptions("archive run", rest, [AgentFlag, BudgetFlag, RunIdFlag], [JsonFlag]) switch
        {
            (_, { } failure) => failure,
            var (options, _) when options.Values.TryGetValue(AgentFlag, out var agent) && AgentValueProblem(agent) is { Length: > 0 } bad => new Request.Failed($"\"{BinaryName} archive run\" {bad}."),
            var (options, _) when options.Values.TryGetValue(BudgetFlag, out var budget) && !ValidBudget(budget) =>
                new Request.Failed($"\"{BinaryName} archive run {BudgetFlag}\" takes a whole number of seconds from 1 to {Core.Config.ConfigKeys.Archive.RunBudgetMinutes.Max * 60}; got \"{Printable(options.Values[BudgetFlag])}\"."),
            var (options, _) when options.Values.TryGetValue(RunIdFlag, out var runId) && Core.Records.RunId.TryParse(runId) is null =>
                new Request.Failed($"\"{BinaryName} archive run {RunIdFlag}\" takes a run id as act names it (yyyyMMddTHHmmssZ-<pid>); got \"{Printable(runId)}\"."),
            var (options, _) => new Request.ArchiveRun(options.Values.GetValueOrDefault(AgentFlag, string.Empty), options.Values.TryGetValue(BudgetFlag, out var b) ? int.Parse(b, System.Globalization.CultureInfo.InvariantCulture) : 0, options.Flags.Contains(JsonFlag))
            {
                RunId = options.Values.GetValueOrDefault(RunIdFlag, string.Empty),
            },
        };

    private static bool ValidBudget(string text) =>
        int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds >= 1 && seconds <= Core.Config.ConfigKeys.Archive.RunBudgetMinutes.Max * 60;

    internal const string EntryFlag = "--entry";
    private const string MonthFlag = "--month";
    private const string SessionFlag = "--session";
    private const string RunFlag = "--run";
    private const string AcceptUnverifiedFlag = "--accept-unverified";

    /// <summary>Exactly one of: <c>--entry</c> ids (16 hex each, comma-separated), <c>--agent</c> with <c>--month</c>, <c>--agent</c> with
    /// <c>--session</c> (a plain relative path); then optionally <c>--accept-unverified</c> and <c>--json</c>.</summary>
    internal static Request ParseArchiveRestore(IReadOnlyList<string> rest) =>
        ReadOptions("archive restore", rest, [EntryFlag, AgentFlag, MonthFlag, SessionFlag], [AcceptUnverifiedFlag, JsonFlag]) switch
        {
            (_, { } failure) => failure,
            var (options, _) when RestoreShapeProblem(options) is { Length: > 0 } problem => new Request.Failed($"\"{BinaryName} archive restore\" {problem}."),
            var (options, _) => new Request.ArchiveRestore(
                options.Values.TryGetValue(EntryFlag, out var ids) ? ids.Split(',') : [],
                options.Values.GetValueOrDefault(AgentFlag, string.Empty),
                options.Values.GetValueOrDefault(MonthFlag, string.Empty),
                options.Values.GetValueOrDefault(SessionFlag, string.Empty),
                options.Flags.Contains(AcceptUnverifiedFlag),
                options.Flags.Contains(JsonFlag)),
        };

    /// <summary>The restore's options, each rule its own check (complexity ≤ 4, the coai code round): one way to name what is restored,
    /// --accept-unverified with --entry only, an agent always validated when given, then the named way's own value.</summary>
    private static string RestoreShapeProblem(Options options) =>
        ModeProblem(options) is { Length: > 0 } mode ? mode
        : options.Values.TryGetValue(AgentFlag, out var agent) && AgentValueProblem(agent) is { Length: > 0 } badAgent ? badAgent
        : NamedProblem(options.Values);

    private static string ModeProblem(Options options)
    {
        var v = options.Values;
        var modes = new[] { EntryFlag, MonthFlag, SessionFlag }.Count(v.ContainsKey);
        return modes != 1 ? $"takes exactly one of {EntryFlag} <id>[,<id>...], {AgentFlag} <id> {MonthFlag} <yyyy-MM>, {AgentFlag} <id> {SessionFlag} <path>"
            : options.Flags.Contains(AcceptUnverifiedFlag) && !v.ContainsKey(EntryFlag) ? $"takes {AcceptUnverifiedFlag} only with {EntryFlag} <id>[,<id>...]: name each unverified entry archive list showed — a month or a session could take an entry planted on the share"
            : string.Empty;
    }

    private static string NamedProblem(IReadOnlyDictionary<string, string> v) =>
        v.TryGetValue(EntryFlag, out var ids) ? EntryProblem(ids)
        : !v.ContainsKey(AgentFlag) ? $"needs {AgentFlag} <id> with {(v.ContainsKey(MonthFlag) ? MonthFlag : SessionFlag)}"
        : v.TryGetValue(MonthFlag, out var month) ? MonthProblem(month)
        : SessionProblem(v[SessionFlag]);

    private static string SessionProblem(string session) =>
        Core.Archive.ArchiveIndex.IsPlainRelative(session) ? string.Empty : $"{SessionFlag} takes the session's path relative to the agent's folder (plain names joined by /); got \"{Printable(session)}\"";

    private static string EntryProblem(string ids) =>
        ids.Split(',').FirstOrDefault(id => !Core.Archive.ArchiveIndex.IsEntryId(id)) is { } bad ? $"{EntryFlag} takes entry ids of 16 hex, comma-separated; got \"{Printable(bad)}\"" : string.Empty;

    private static string AgentValueProblem(string agent) =>
        agent.Contains(',', StringComparison.Ordinal) || Core.Config.ConfigValidation.Parse(Core.Config.ConfigKeys.Archive.Agents, agent) is not Core.Config.ValueCheck.Ok
            ? $"{AgentFlag} takes one of {string.Join(", ", Core.Agents.AgentCatalogue.ArchivableIds)} or {Core.Agents.ExtraAgent.IdPrefix}<name>; got \"{Printable(agent)}\""
            : string.Empty;

    private static string MonthProblem(string month) =>
        DateTime.TryParseExact(month, "yyyy-MM", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _)
            ? string.Empty
            : $"{MonthFlag} takes a month as yyyy-MM; got \"{Printable(month)}\"";

    /// <summary>Optionally <c>--agent &lt;id&gt;</c>, <c>--month &lt;yyyy-MM&gt;</c>, <c>--run &lt;runId&gt;</c>, <c>--restorable</c> (with it, optionally
    /// <c>--entry &lt;id&gt;[,&lt;id&gt;…]</c> — the E10.S0 own review, finding 1), <c>--json</c>.</summary>
    internal static Request ParseArchiveList(IReadOnlyList<string> rest) =>
        ReadOptions("archive list", rest, [AgentFlag, MonthFlag, RunFlag, EntryFlag], [JsonFlag, RestorableFlag]) switch
        {
            (_, { } failure) => failure,
            var (options, _) when options.Values.TryGetValue(EntryFlag, out var ids) && ListEntriesProblem(ids, options.Flags.Contains(RestorableFlag)) is { Length: > 0 } bad => new Request.Failed($"\"{BinaryName} archive list\" {bad}."),
            var (options, _) when options.Values.TryGetValue(AgentFlag, out var agent) && AgentValueProblem(agent) is { Length: > 0 } bad => new Request.Failed($"\"{BinaryName} archive list\" {bad}."),
            var (options, _) when options.Values.TryGetValue(MonthFlag, out var month) && MonthProblem(month) is { Length: > 0 } bad => new Request.Failed($"\"{BinaryName} archive list\" {bad}."),
            var (options, _) when options.Values.TryGetValue(RunFlag, out var run) && Core.Records.RunId.TryParse(run) is null => new Request.Failed($"\"{BinaryName} archive list\" {RunFlag} takes a run id; got \"{Printable(run)}\"."),
            var (options, _) => new Request.ArchiveList(options.Values.GetValueOrDefault(AgentFlag, string.Empty), options.Values.GetValueOrDefault(MonthFlag, string.Empty), options.Values.GetValueOrDefault(RunFlag, string.Empty), options.Flags.Contains(JsonFlag)) { Restorable = options.Flags.Contains(RestorableFlag), EntryIds = options.Values.TryGetValue(EntryFlag, out var ids) ? ids.Split(',') : [] },
        };

    /// <summary>A list's <c>--entry</c>: only with <c>--restorable</c> (what A20's preview asks), under the act's entry rules.</summary>
    private static string ListEntriesProblem(string ids, bool restorable) =>
        restorable ? EntryListProblem(ids.Split(',')) : $"takes {EntryFlag} only with {RestorableFlag}: it names the entries A20's preview asks for";

    internal static Request ParseArchiveReconcile(IReadOnlyList<string> rest) => rest switch
    {
        [ScanFlag] => new Request.ArchiveReconcileScan(false),
        [ScanFlag, JsonFlag] or [JsonFlag, ScanFlag] => new Request.ArchiveReconcileScan(true),
        _ => new Request.Failed($"\"{BinaryName} archive reconcile\" needs {ScanFlag} (the reconcile itself runs at the start of every archive run) and optionally {JsonFlag}."),
    };

    internal static Request ParseArchiveCheckBase(IReadOnlyList<string> rest) => rest switch
    {
        [var path] when IsPathArgument(path) => new Request.ArchiveCheckBase(path, false),
        [var path, JsonFlag] when IsPathArgument(path) => new Request.ArchiveCheckBase(path, true),
        [JsonFlag, var path] when IsPathArgument(path) => new Request.ArchiveCheckBase(path, true),
        _ => new Request.Failed($"\"{BinaryName} archive check-base\" needs exactly one <path> (not starting with -, no control character) and optionally {JsonFlag}, before or after it: {BinaryName} archive check-base [{JsonFlag}] <path> [{JsonFlag}]."),
    };

    private static bool IsPathArgument(string path) => path.Length > 0 && !path.StartsWith('-') && !path.Any(char.IsControl);

    private static int MostEntries => Core.Config.ConfigKeys.Archive.MaxRestoreEntries.Max;

    /// <summary>A shown entry list belongs to A20 (plan §15r E9.S4): every <c>--entry</c> an archived entry's 16-hex id, none twice, at
    /// most <c>archive.maxRestoreEntries</c>' ceiling. <c>--entry -</c> (plan §15s D7) is the whole list, on stdin: never beside an
    /// <c>--entry &lt;id&gt;</c>, never beside <c>--only -</c> — stdin carries one list.</summary>
    internal static Request.Failed? ShownEntriesFailure(IReadOnlyList<Core.Actions.ActionId> ids, IReadOnlyList<string> entries, bool onStdin, bool onlyOnStdin) =>
        EntriesProblem(ids, entries, onStdin, onlyOnStdin) is { Length: > 0 } problem ? new Request.Failed($"\"{BinaryName} act\": {problem}.") : null;

    /// <summary>What is wrong with an act's entry list — where it stands, then what it holds; empty when none was given or it is right
    /// (the code round: each check its own method, complexity ≤ 4).</summary>
    private static string EntriesProblem(IReadOnlyList<Core.Actions.ActionId> ids, IReadOnlyList<string> entries, bool onStdin, bool onlyOnStdin) =>
        entries.Count == 0 && !onStdin ? string.Empty : FirstProblem(PlacementProblem(ids, entries, onStdin, onlyOnStdin), entries);

    private static string FirstProblem(string placement, IReadOnlyList<string> entries) => placement.Length > 0 ? placement : EntryListProblem(entries);

    /// <summary>Where an entry list may stand (the E10.S0 own review, finding 5: apart from what it holds): with A20 among the actions,
    /// either on stdin or as ids — never both — and never beside <c>--only -</c>; empty when it stands right.</summary>
    private static string PlacementProblem(IReadOnlyList<Core.Actions.ActionId> ids, IReadOnlyList<string> entries, bool onStdin, bool onlyOnStdin) =>
        (ids.Any(id => id.Text == "A20"), onStdin, entries.Count > 0, onlyOnStdin) switch
        {
            (false, _, _, _) => $"{EntryFlag} names the archived entries A20's preview showed; it needs A20 among the actions",
            (true, true, true, _) => $"{EntryFlag} - is the whole entry list, on stdin; give {EntryFlag} once, either - or the ids",
            (true, true, _, true) => $"stdin carries one list — {OnlyFlag} - and {EntryFlag} - cannot both read it",
            _ => string.Empty,
        };

    /// <summary>What is wrong with a list of entry ids — the flag's or stdin's — empty when nothing is: not an id (named), twice, past
    /// the ceiling.</summary>
    private static string EntryListProblem(IReadOnlyList<string> entries) =>
        entries.FirstOrDefault(e => !Core.Archive.ArchiveIndex.IsEntryId(e)) is { } bad ? $"{EntryFlag} \"{Printable(bad)}\" is not an archived entry's id (16 lowercase hex digits)"
        : CountProblem(entries);

    private static string CountProblem(IReadOnlyList<string> entries) =>
        entries.Distinct(StringComparer.Ordinal).Count() != entries.Count ? $"{EntryFlag} names an entry twice"
        : entries.Count > MostEntries ? $"{EntryFlag} names more than {MostEntries} entries (the ceiling of {Core.Config.ConfigKeys.Archive.MaxRestoreEntries.Name})"
        : string.Empty;

    /// <summary>The entry ids of <c>--entry -</c>'s stdin (plan §15s D7): one per non-empty line (a trailing CR tolerated), under the
    /// flag's checks — or why not, naming the LINE, never echoing what is on it (it is read as root).</summary>
    internal static (IReadOnlyList<string> Ids, string Failure) StdinEntries(string text)
    {
        var (ids, bad) = StdinList.Lines(text, Core.Archive.ArchiveIndex.IsEntryId);
        var problem = bad > 0 ? $"line {bad} of the entry list on stdin is not an archived entry's id (16 lowercase hex digits)"
            : ids.Count == 0 ? "the entry list on stdin names no entry"
            : CountProblem(ids);
        return problem.Length > 0 ? ([], problem) : (ids, string.Empty);
    }
}
