using System.Collections.Immutable;
using System.Text;

namespace WslCare.Cli;

/// <summary>What the user asked for, or why the arguments could not be read.</summary>
/// <remarks>A closed set: the constructor is private, so every case is one of the nested records
/// and a <c>switch</c> over a request can be checked for completeness by reading this file.</remarks>
internal abstract record Request
{
    private Request()
    {
    }

    internal sealed record Help : Request;

    internal sealed record Version : Request;

    internal sealed record Failed(string Message) : Request;

    /// <summary><c>config get [key] [--json]</c>: every key, or one; as text, or as the JSON report.</summary>
    internal sealed record ConfigGet(string Key, bool Json) : Request;

    /// <summary><c>config set &lt;key&gt; &lt;value&gt;</c>: the value as typed; validated by the command.</summary>
    internal sealed record ConfigSet(string Key, string Value) : Request;

    /// <summary><c>config reset &lt;key&gt;</c>: remove the key from the user layer.</summary>
    internal sealed record ConfigReset(string Key) : Request;

    /// <summary><c>status [--json]</c>: the fast snapshot (plan §6), as text or as the JSON report.</summary>
    internal sealed record Status(bool Json) : Request;

    /// <summary><c>preview --all [--json]</c>: every cleanup row with count and reclaimable bytes (plan §6).</summary>
    internal sealed record Preview(bool Json) : Request;

    /// <summary><c>collect [--timer] [--json]</c>: the full run (plan §6) — measured, recorded when this process may write the
    /// state. <c>--timer</c> is the systemd timer's own mark in its unit's <c>ExecStart</c>: only then does the run act.</summary>
    internal sealed record Collect(bool Json) : Request
    {
        /// <summary>The timer started it (its unit passes <c>--timer</c>); never inferred from the environment.</summary>
        public bool Timer { get; init; }

        /// <summary><c>--detach</c> (E6.S1, §15j M9 — <i>Run full check now</i>): a request of kind <c>collect</c> and the template
        /// unit started; never with <c>--timer</c>.</summary>
        public bool Detach { get; init; }
    }

    /// <summary><c>agents list [--measure] [--json]</c> (plan §4.6, §15q E7.S1): the catalogue agents found here, their sizes from
    /// the newest full run — or measured now with <c>--measure</c>.</summary>
    internal sealed record AgentsList(bool Measure, bool Json) : Request;

    /// <summary><c>agents probe &lt;path&gt; [--json]</c> (plan §15q D4): what a CLI the person picked is, as this user — the path
    /// is checked for its shape here and never reaches a root process.</summary>
    internal sealed record AgentsProbe(string Path, bool Json) : Request;

    /// <summary><c>archive check-base &lt;path&gt; [--json]</c> (plan §15r D7, E9.S0): whether the archive may live at a folder, judged
    /// by THIS user's process — the one that will write there (D1); never as root.</summary>
    internal sealed record ArchiveCheckBase(string Path, bool Json) : Request;

    /// <summary><c>archive preview [--agent &lt;id&gt;] [--json]</c> (plan §15r E9.S1): what the archive would move on this side now,
    /// as this user — read-only; <paramref name="Agent"/> empty = every agent of <c>archive.agents</c>.</summary>
    internal sealed record ArchivePreview(string Agent, bool Json) : Request;

    /// <summary><c>units dropin &lt;unit&gt;</c> (E7.S2c): the drop-in install.sh writes for one unit, from the machine
    /// configuration — the timer's period, the services' Nice / MemoryMax / TimeoutStopSec, the follower's RestartSec.</summary>
    internal sealed record UnitsDropIn(string Unit) : Request;

    /// <summary><c>doctor [--json]</c>: is the installation doing its job (plan §6).</summary>
    internal sealed record Doctor(bool Json) : Request;

    /// <summary><c>busy [--json]</c> (E14 S6): is the machine too busy to START heavy work now — PSI against its keys; exit 0
    /// calm or unknown, <see cref="ExitCode.MachineBusy"/> busy.</summary>
    internal sealed record Busy(bool Json) : Request;

    /// <summary><c>watch [--timer] [--json]</c> (plan E14 S2b): as root, the watch timer's run — the MCP servers' CPU ledger and the
    /// agents' CPU history recorded; with <c>--timer</c> (the watch unit's mark) A19 may act.</summary>
    internal sealed record Watch(bool Json) : Request
    {
        /// <summary>The watch timer started it (its unit passes <c>--timer</c>); never inferred from the environment.</summary>
        public bool Timer { get; init; }
    }

    /// <summary><c>events follow [--once]</c>: the container-start follower (plan §4.3); <c>--once</c> catches up and stops.</summary>
    internal sealed record EventsFollow(bool Once) : Request;

    /// <summary><c>logs [--period … or --from … --to …] [--action &lt;A#&gt;] [--detail] [--json]</c> (plan §7.4): the period's
    /// totals and cleanups — the objects each removed only with <c>--detail</c> or one <c>--action</c> (gate finding #10); the
    /// period text is checked against the clock by the verb. INVARIANT (coai E6 code round #3): exactly one of the two modes is
    /// set — <see cref="Period"/> (default <c>today</c>) with <see cref="From"/> / <see cref="To"/> empty, or the instant range
    /// with <see cref="Period"/> EMPTY.</summary>
    internal sealed record Logs(string Period, Core.Actions.ActionId? Action, bool Json, bool Detail = false) : Request
    {
        /// <summary>The instant range's start (<c>--from</c>, §15j M7) as typed, checked by the verb; empty when not given.</summary>
        public string From { get; init; } = string.Empty;

        /// <summary>The instant range's end (<c>--to</c>); empty when not given.</summary>
        public string To { get; init; } = string.Empty;
    }

    /// <summary><c>runs [--period … or --from … --to …] [--json]</c> (plan §7.4): every run of the period. The same invariant as
    /// <see cref="Logs"/>: <see cref="Period"/> is empty in the instant-range mode.</summary>
    internal sealed record Runs(string Period, bool Json) : Request
    {
        public string From { get; init; } = string.Empty;

        public string To { get; init; } = string.Empty;
    }

    /// <summary><c>runs show &lt;runId&gt; [--json]</c> (plan §15j M3): one run — queued, running, done with its full detail,
    /// refused, interrupted or unknown. The run id is already well formed (a <see cref="Core.Records.RunId"/>).</summary>
    internal sealed record RunsShow(Core.Records.RunId RunId, bool Json) : Request;

    /// <summary><c>act &lt;A#&gt;[,&lt;A#&gt;…] (--preview or --confirm) [--manual] [--volume &lt;name&gt;]... [--only &lt;file&gt;] [--json]</c>
    /// (plan §6): preview the actions, or run them — a destructive run from the CLI needs <c>--confirm</c> (the button passes it
    /// after the person confirmed). <c>--manual</c> is the panel's mark (the run's trigger is <c>manual</c>); <c>--volume</c> and
    /// <c>--only</c> carry the volumes A4's preview SHOWED (E3.S2).</summary>
    internal sealed record Act(IReadOnlyList<Core.Actions.ActionId> Ids, bool Confirm, bool Json) : Request
    {
        /// <summary>The panel's button started it: recorded as <c>manual</c>, not <c>cli</c>.</summary>
        public bool Manual { get; init; }

        /// <summary>The timer started it (<c>--timer</c> in its unit): the auto switches, triggers and the dry-run week apply.</summary>
        public bool Timer { get; init; }

        /// <summary>Every <c>--volume</c> given, each already a 64-hex anonymous volume name.</summary>
        public IReadOnlyList<string> Volumes { get; init; } = [];

        /// <summary>The <c>--only</c> file (one 64-hex name per line), read by the verb; <c>-</c> is STDIN (E6.S1, §15j M2); empty
        /// when not given.</summary>
        public string OnlyFile { get; init; } = string.Empty;

        /// <summary><c>--detach</c> (E6.S1, §15j B2): write the request, start the template unit, answer <c>accepted</c> at once.</summary>
        public bool Detach { get; init; }

        /// <summary>Every <c>--process</c> given (E7.S2b review A-H1), each already a <c>pid:start</c> key: the processes A18's preview
        /// showed and the person confirmed.</summary>
        public IReadOnlyList<string> Processes { get; init; } = [];

        /// <summary>Whether a shown list was passed at all.</summary>
        public bool HasShownList => Volumes.Count > 0 || OnlyFile.Length > 0;

        /// <summary>Whether the shown list comes on stdin (<c>--only -</c>).</summary>
        public bool ShownOnStdin => OnlyFile == "-";
    }

    /// <summary><c>act --request &lt;runId&gt;</c> (E6.S1, §15j B2): the template unit's start — run the request <c>--detach</c>
    /// wrote, under its run id. The run id is already well formed.</summary>
    internal sealed record ActFromRequest(Core.Records.RunId RunId) : Request;

    /// <summary><c>act --stop &lt;runId&gt; [--json]</c> (E6.S1, §15j M4): stop a WEDGED run hosted by one of the units.</summary>
    internal sealed record ActStop(Core.Records.RunId RunId, bool Json) : Request;
}

/// <summary>One thing the command line accepts: how it is spelt, what it does, how it is parsed.</summary>
/// <param name="Spellings">Every word sequence that selects it (<c>["config","get"]</c>; <c>["-h"]</c>).</param>
/// <param name="Usage">The spelling shown in the help text, placeholders included.</param>
/// <param name="Summary">One line saying what it does.</param>
/// <param name="Example">A complete argv that must parse — what the derived test and the scenario register use.</param>
/// <param name="ParseRest">Reads the arguments after the spelling.</param>
internal sealed record Command(
    IReadOnlyList<IReadOnlyList<string>> Spellings,
    string Usage,
    string Summary,
    IReadOnlyList<string> Example,
    Func<IReadOnlyList<string>, Request> ParseRest);

/// <summary>One spelling and the command it selects — the unit the parser matches on.</summary>
internal sealed record Spelt(Command Command, IReadOnlyList<string> Spelling);

/// <summary>
/// Argument parsing, kept pure so the shapes are a unit test rather than something discovered by
/// running the binary with the wrong words. Mirrors the hand-rolled parser of the family's
/// <c>creds</c> CLI: no command-line package, nothing reflective, nothing for AOT to trip on.
/// </summary>
/// <remarks>
/// <para><see cref="Commands"/> is the ONE register of what this binary accepts. The parser and the
/// help text are both derived from it, so a command cannot be accepted and undocumented, or
/// documented and refused. The remaining verbs of plan §6 (<c>agents</c>, …) arrive in later stories as entries here;
/// <c>runs log</c> was cut by plan §15j M3 (<c>runs show</c> answers the commands and their exits).</para>
/// </remarks>
internal static class CommandLine
{
    internal const string BinaryName = "wsl-care";

    private const string JsonFlag = "--json";
    private const string AllFlag = "--all";
    private const string OnceFlag = "--once";
    private const string PreviewFlag = "--preview";
    private const string ConfirmFlag = "--confirm";
    private const string ManualFlag = "--manual";
    private const string TimerFlag = "--timer";
    private const string VolumeFlag = "--volume";
    private const string OnlyFlag = "--only";
    private const string ProcessFlag = "--process";
    private const string PeriodFlag = "--period";
    private const string ActionFlag = "--action";
    private const string DetailFlag = "--detail";
    private const string FromFlag = "--from";
    private const string ToFlag = "--to";
    private const string DetachFlag = "--detach";
    private const string RequestFlag = "--request";
    private const string StopFlag = "--stop";
    private const string StdinMarker = "-";
    private const string MeasureFlag = "--measure";
    private const string AgentFlag = "--agent";

    /// <summary>The most names one <c>act</c> may carry through <c>--volume</c> and <c>--only</c> together (plan §15j B1) — the
    /// COMPILE-TIME ceiling: the parser runs before any configuration is read, so it holds the range maximum of
    /// <c>act.maxShownNames</c> (a test keeps them equal), and the verb, once the configuration is loaded, refuses a list past the
    /// value IN FORCE (coai E7 code round #4).</summary>
    internal const int MaxShownVolumes = 10_000;

    internal static readonly IReadOnlyList<Command> Commands =
    [
        new([["--help"], ["-h"], ["help"]], "--help", "print this text", ["--help"], NoMore("--help", new Request.Help())),
        new([["--version"]], "--version", "print the version of this build", ["--version"], NoMore("--version", new Request.Version())),
        new([["config", "get"]], "config get [key] [--json]", "print the effective settings (or one) and the layer each came from", ["config", "get"], ParseConfigGet),
        new([["config", "set"]], "config set <key> <value>", "validate one setting and write it into the user layer", ["config", "set", "dryRun", "false"], ParseConfigSet),
        new([["config", "reset"]], "config reset <key>", "remove one setting from the user layer", ["config", "reset", "dryRun"], ParseConfigReset),
        new([["status"]], "status [--json]", "a fast snapshot: memory, top holders, containers, disk; slow parts from the last full run", ["status", "--json"], ParseStatus),
        new([["preview"]], "preview --all [--json]", "every cleanup row with its count and reclaimable bytes, the kept named volumes, Docker hygiene", ["preview", "--all", "--json"], ParsePreview),
        new([["collect"]], "collect [--timer or --detach] [--json]", "the full run: every collector, the thresholds, recorded as run detail + history line (as root; read-only otherwise); --timer is the systemd timer's mark, the only run that also acts; --detach (as root) starts it in its own unit and answers accepted at once", ["collect", "--json"], ParseCollect),
        new([["doctor"]], "doctor [--json]", "is the installation doing its job: units, collectors, configuration, last run, versions", ["doctor", "--json"], rest => JsonOnly("doctor", rest, json => new Request.Doctor(json))),
        new([["busy"]], "busy [--json]", "is the machine too busy to start heavy work now: cpu, io and memory pressure (PSI some avg60) against their keys; exit 83 busy (wait), 0 calm or unknown (go); reads /proc/pressure and /proc/loadavg only", ["busy", "--json"], rest => JsonOnly("busy", rest, json => new Request.Busy(json))),
        new([["watch"]], "watch [--timer] [--json]", "as root: the watch timer's run (wsl-care-watch.timer, every mcpWatchdog.periodMinutes): records the MCP servers' CPU ledger and the agents' CPU history under the run lock; with --timer, A19 may then stop the MCP servers idle or busy without a log write, under the dry-run rules, as a recorded act (a watch that stops nothing writes no history line)", ["watch", "--json"], ParseWatch),
        new([["events", "follow"]], "events follow [--once]", "record every container start under the state directory (the wsl-care-events unit); --once catches up and stops", ["events", "follow", "--once"], ParseEventsFollow),
        new([["act"]], "act <A#>[,<A#>...] (--preview or --confirm) [--manual or --timer] [--detach] [--volume <name>]... [--only <file or ->] [--process <pid:start>]... [--json]", "as root: preview the actions from live state, or run them (--confirm), one run at a time, recorded; --manual marks the panel's button, --timer the systemd timer, --detach runs a confirm in its own unit and answers accepted at once, --volume / --only (- = stdin) the volumes A4's preview showed, --process the processes A18's or A19's preview showed", ["act", "A10", "--preview", "--json"], ParseAct),
        new([["act", "--request"]], "act --request <runId>", "as root, the template unit's start: run the request --detach wrote, recorded under its run id (refused, recorded, when another run holds the lock)", ["act", "--request", "20261002T120000Z-123"], ParseActFromRequest),
        new([["act", "--stop"]], "act --stop <runId> [--json]", "as root: stop a WEDGED run through systemd, only when its process lives in wsl-care.service or that run's own unit", ["act", "--stop", "20261002T120000Z-123", "--json"], ParseActStop),
        new([["logs"]], "logs [--period <today, yesterday, yyyy-MM-dd or from..to> or --from <instant> --to <instant>] [--action <A#>] [--detail] [--json]", "what the runs of a period freed, per action; runs with and without a cleanup; max and min; every object removed with --detail or one --action (read-only; UTC days, or two RFC 3339 instants with their offsets)", ["logs", "--period", "today", "--json"], ParseLogs),
        new([["runs"]], "runs [--period <today, yesterday, yyyy-MM-dd or from..to> or --from <instant> --to <instant>] [--json]", "every run of a period: trigger, outcome, dry run, actions, freed (read-only; UTC days, or two RFC 3339 instants with their offsets)", ["runs", "--period", "yesterday", "--json"], ParseRuns),
        new([["agents", "list"]], "agents list [--measure] [--json]", "the AI agents found here (by binary, npm package or folder), their version read from disk, their folders' sizes and sessions from the newest full run — or measured now with --measure (read-only: nothing inside an agent's folder is opened, nothing is run)", ["agents", "list", "--json"], ParseAgentsList),
        new([["agents", "probe"]], "agents probe <path> [--json]", "what the CLI at <path> is, as this user and never as root: a file it may start (looked at, never run, never read), a name from its file name, and its conventional data folders with their sizes and whether they could be a manual agent's (read-only)", ["agents", "probe", "/home/me/.local/bin/mycli", "--json"], ParseAgentsProbe),
        new([["archive", "check-base"]], "archive check-base <path> [--json]", "whether the AI-session archive may live at <path>, as this user (never as root): an existing folder (never created), no link on the way, no root, nothing it moves or a cleanup removes, a filesystem that survives a shutdown, writable — a Windows drive path answered with the folder it is mounted at; who else may read it (read-only)", ["archive", "check-base", "/mnt/v/ai-archive", "--json"], ParseArchiveCheckBase),
        new([["archive", "preview"]], "archive preview [--agent <id>] [--json]", "what the AI-session archive would move on this side now, as this user (never as root): per agent the due sessions oldest first, their newest write and month, what is kept in place and why (open, an agent working there, a name NTFS refuses, what never moves), the age they are due at and the agent's own retention (read-only: no session file is opened, nothing is written)", ["archive", "preview", "--json"], ParseArchivePreview),
        new([["units", "dropin"]], "units dropin <unit>", "the systemd drop-in install.sh writes for one of wsl-care's units, from the machine configuration (the timer's period, the services' Nice, MemoryMax and TimeoutStopSec, the follower's RestartSec); doctor names an installed drop-in that no longer matches (read-only)", ["units", "dropin", "wsl-care.timer"], ParseUnitsDropIn),
        new([["runs", "show"]], "runs show <runId> [--json]", "one run: queued, running, done with every object it removed and did not remove and the commands it ran with their exits, refused, interrupted or unknown (read-only)", ["runs", "show", "20261002T120000Z-123", "--json"], ParseRunsShow),
    ];

    /// <summary>Every spelling of <see cref="Commands"/> with its command, longest first — ordered once,
    /// so a parse is a single pass that stops at the first prefix match. Declared after
    /// <see cref="Commands"/> on purpose: static fields initialise in textual order.</summary>
    private static readonly IReadOnlyList<Spelt> SpellingsLongestFirst = LongestFirst(Commands);

    internal static Request Parse(IReadOnlyList<string> argv)
    {
        if (argv.Count == 0)
        {
            return new Request.Help();
        }

        var (command, spelling) = Match(argv, SpellingsLongestFirst);
        if (command is not null)
        {
            return command.ParseRest(argv.Skip(spelling.Count).ToList());
        }

        var subVerbs = SubVerbsOf(argv[0]);
        return subVerbs.Count > 0
            ? new Request.Failed($"\"{BinaryName} {Printable(argv[0])}\" needs one of: {string.Join(", ", subVerbs)}.")
            : new Request.Failed($"unknown verb or option \"{Printable(argv[0])}\". Run \"{BinaryName} --help\" to see what exists.");
    }

    /// <summary>The help text, derived from <see cref="Commands"/>.</summary>
    internal static string HelpText { get; } = BuildHelpText();

    /// <summary>Every spelling of <paramref name="commands"/>, longest first; a stable sort, so spellings of
    /// equal length keep the register's order.</summary>
    internal static IReadOnlyList<Spelt> LongestFirst(IEnumerable<Command> commands) =>
        [.. commands.SelectMany(c => c.Spellings.Select(s => new Spelt(c, s))).OrderByDescending(spelt => spelt.Spelling.Count)];

    /// <summary>The longest spelling that is a prefix of <paramref name="argv"/>, and its command: the
    /// first match in <paramref name="longestFirst"/>, which is ordered by length already.</summary>
    internal static (Command? Command, IReadOnlyList<string> Spelling) Match(IReadOnlyList<string> argv, IReadOnlyList<Spelt> longestFirst)
    {
        foreach (var spelt in longestFirst)
        {
            if (StartsWith(argv, spelt.Spelling))
            {
                return (spelt.Command, spelt.Spelling);
            }
        }

        return (null, []);
    }

    private static bool StartsWith(IReadOnlyList<string> argv, IReadOnlyList<string> spelling) =>
        spelling.Count <= argv.Count && spelling.Zip(argv).All(pair => string.Equals(pair.First, pair.Second, StringComparison.Ordinal));

    /// <summary>The second words of every multi-word spelling that begins with <paramref name="verb"/>.</summary>
    private static IReadOnlyList<string> SubVerbsOf(string verb) =>
        [.. Commands.SelectMany(c => c.Spellings)
            .Where(s => s.Count > 1 && string.Equals(s[0], verb, StringComparison.Ordinal))
            .Select(s => s[1])
            .Distinct(StringComparer.Ordinal)];

    private static Func<IReadOnlyList<string>, Request> NoMore(string usage, Request answer) =>
        rest => rest.Count == 0 ? answer : new Request.Failed($"\"{BinaryName} {usage}\" takes no further arguments.");

    private static Request ParseConfigGet(IReadOnlyList<string> rest)
    {
        var key = string.Empty;
        var json = false;
        foreach (var token in rest)
        {
            switch (token)
            {
                case JsonFlag:
                    json = true;
                    break;
                case var option when option.StartsWith('-'):
                    return new Request.Failed($"\"{BinaryName} config get\" does not know the option \"{Printable(option)}\"; it takes an optional key and {JsonFlag}.");
                case var _ when key.Length > 0:
                    return new Request.Failed($"\"{BinaryName} config get\" takes at most one key; got \"{Printable(key)}\" and \"{Printable(token)}\".");
                default:
                    key = token;
                    break;
            }
        }

        return new Request.ConfigGet(key, json);
    }

    private static Request ParseConfigSet(IReadOnlyList<string> rest) =>
        rest.Count == 2
            ? new Request.ConfigSet(rest[0], rest[1])
            : new Request.Failed($"\"{BinaryName} config set\" needs exactly a key and a value: {BinaryName} config set <key> <value>.");

    private static Request ParseConfigReset(IReadOnlyList<string> rest) =>
        rest.Count == 1
            ? new Request.ConfigReset(rest[0])
            : new Request.Failed($"\"{BinaryName} config reset\" needs exactly one key: {BinaryName} config reset <key>.");

    private static Request ParseStatus(IReadOnlyList<string> rest) => rest switch
    {
        [] => new Request.Status(Json: false),
        [JsonFlag] => new Request.Status(Json: true),
        _ => new Request.Failed($"\"{BinaryName} status\" takes only {JsonFlag}; got \"{Printable(string.Join(' ', rest))}\"."),
    };

    private static Request JsonOnly(string verb, IReadOnlyList<string> rest, Func<bool, Request> make) => rest switch
    {
        [] => make(false),
        [JsonFlag] => make(true),
        _ => new Request.Failed($"\"{BinaryName} {verb}\" takes only {JsonFlag}; got \"{Printable(string.Join(' ', rest))}\"."),
    };

    /// <summary><c>collect</c> takes <c>--timer</c> and <c>--json</c>, each at most once, in any order.</summary>
    private static Request ParseCollect(IReadOnlyList<string> rest) =>
        rest.All(f => f is TimerFlag or JsonFlag or DetachFlag) && rest.Distinct(StringComparer.Ordinal).Count() == rest.Count && !(rest.Contains(TimerFlag) && rest.Contains(DetachFlag))
            ? new Request.Collect(rest.Contains(JsonFlag)) { Timer = rest.Contains(TimerFlag), Detach = rest.Contains(DetachFlag) }
            : new Request.Failed($"\"{BinaryName} collect\" takes {TimerFlag} or {DetachFlag} (not both) and {JsonFlag}, each once; got \"{Printable(string.Join(' ', rest))}\".");

    private static Request ParseWatch(IReadOnlyList<string> rest) =>
        rest.All(f => f is TimerFlag or JsonFlag) && rest.Distinct(StringComparer.Ordinal).Count() == rest.Count
            ? new Request.Watch(rest.Contains(JsonFlag)) { Timer = rest.Contains(TimerFlag) }
            : new Request.Failed($"\"{BinaryName} watch\" takes {TimerFlag} and {JsonFlag}, each once; got \"{Printable(string.Join(' ', rest))}\".");

    /// <summary><c>act --request &lt;runId&gt;</c>: exactly one well-formed run id.</summary>
    private static Request ParseActFromRequest(IReadOnlyList<string> rest) =>
        RunIdVerb("act --request", rest, takesJson: false, (runId, _) => new Request.ActFromRequest(runId));

    /// <summary><c>act --stop &lt;runId&gt; [--json]</c>: exactly one well-formed run id, then optionally <c>--json</c>.</summary>
    private static Request ParseActStop(IReadOnlyList<string> rest) =>
        RunIdVerb("act --stop", rest, takesJson: true, (runId, json) => new Request.ActStop(runId, json));

    /// <summary>The one parse of a verb that takes exactly one run id and maybe <c>--json</c> (coai E6 code round #0 / #2): the
    /// TYPED id travels in the request — never the raw text — and a bad one is refused naming the value and the shape.</summary>
    private static Request RunIdVerb(string verb, IReadOnlyList<string> rest, bool takesJson, Func<Core.Records.RunId, bool, Request> make) => rest switch
    {
        [var id] when Core.Records.RunId.TryParse(id) is { } runId => make(runId, false),
        [var id, JsonFlag] when takesJson && Core.Records.RunId.TryParse(id) is { } runId => make(runId, true),
        [var id, ..] when !id.StartsWith('-') && Core.Records.RunId.TryParse(id) is null =>
            new Request.Failed($"\"{BinaryName} {verb}\": \"{Printable(id)}\" is not a run id (yyyyMMddTHHmmssZ-<pid>, as runs and logs print it)."),
        _ => new Request.Failed(takesJson
            ? $"\"{BinaryName} {verb}\" needs exactly one <runId> and optionally {JsonFlag}: {BinaryName} {verb} <runId> [{JsonFlag}]."
            : $"\"{BinaryName} {verb}\" needs exactly one <runId>: {BinaryName} {verb} <runId>."),
    };

    private static Request ParseEventsFollow(IReadOnlyList<string> rest) => rest switch
    {
        [] => new Request.EventsFollow(Once: false),
        [OnceFlag] => new Request.EventsFollow(Once: true),
        _ => new Request.Failed($"\"{BinaryName} events follow\" takes only {OnceFlag}; got \"{Printable(string.Join(' ', rest))}\"."),
    };

    /// <summary>The ids first (one comma-separated word, every id known), then exactly one of <c>--preview</c> /
    /// <c>--confirm</c>, and optionally <c>--json</c>; nothing else.</summary>
    private static Request ParseAct(IReadOnlyList<string> rest)
    {
        if (rest.Count == 0 || rest[0].StartsWith('-'))
        {
            return new Request.Failed($"\"{BinaryName} act\" needs the actions first: {BinaryName} act <A#>[,<A#>...] {PreviewFlag}|{ConfirmFlag} [{JsonFlag}].");
        }

        return Core.Actions.ActionId.Parse(rest[0]) switch
        {
            Core.Actions.ActionIdList.Refused refused => new Request.Failed($"\"{BinaryName} act\": {Printable(refused.Reason)}."),
            Core.Actions.ActionIdList.Parsed parsed => ActOptions(parsed.Ids, [.. rest.Skip(1)]),
            _ => throw new System.Diagnostics.UnreachableException("ActionIdList is a closed set"),
        };
    }

    /// <summary>The act's options after its ids: its flags, then its shown list — or the first refusal.</summary>
    private static Request ActOptions(IReadOnlyList<Core.Actions.ActionId> ids, IReadOnlyList<string> rest) => SplitActOptions(rest) switch
    {
        ({ Failure: { } failure }) => failure,
        var split when ActFlags(split.Flags) is { } failure => failure,
        var split when (ShownListFailure(ids, split.Volumes, split.Only) ?? ShownProcessesFailure(ids, split.Processes)) is { } failure => failure,
        var split => new Request.Act(ids, split.Flags.Contains(ConfirmFlag), split.Flags.Contains(JsonFlag)) { Manual = split.Flags.Contains(ManualFlag), Timer = split.Flags.Contains(TimerFlag), Detach = split.Flags.Contains(DetachFlag), Volumes = split.Volumes, OnlyFile = split.Only, Processes = split.Processes },
    };

    /// <summary>An act's options apart: its flags, the <c>--volume</c> values, the <c>--only</c> file, the <c>--process</c> keys.</summary>
    /// <remarks>Retro gate over PR #17: immutable lists, so taking a value neither mutates the split nor copies the whole list
    /// (an <see cref="ImmutableList{T}"/> add shares its tree: ten thousand <c>--process</c> keys copied 400 MB before).</remarks>
    private sealed record ActSplit(ImmutableList<string> Flags, ImmutableList<string> Volumes, string Only, ImmutableList<string> Processes, Request.Failed? Failure);

    /// <summary>The flags, the <c>--volume</c> values, the <c>--only</c> file and the <c>--process</c> keys, apart — or the first refusal.</summary>
    private static ActSplit SplitActOptions(IReadOnlyList<string> rest)
    {
        var split = new ActSplit([], [], string.Empty, [], null);
        for (var i = 0; i < rest.Count && split.Failure is null; i += TakeOption(rest, i, ref split))
        {
        }

        return split;
    }

    /// <summary>One option of an act at <paramref name="i"/> taken into <paramref name="split"/> (coai E7 code round #1: the
    /// per-flag dispatch apart); how many arguments it used.</summary>
    private static int TakeOption(IReadOnlyList<string> rest, int i, ref ActSplit split)
    {
        if (rest[i] is not (VolumeFlag or OnlyFlag or ProcessFlag))
        {
            split = split with { Flags = split.Flags.Add(rest[i]) };
            return 1;
        }

        split = ShownValueProblem(rest, i, split.Only) is { } failure ? split with { Failure = failure } : WithValue(split, rest[i], rest[i + 1]);
        return 2;
    }

    /// <summary>A <c>--volume</c>, <c>--only</c> or <c>--process</c> value taken.</summary>
    private static ActSplit WithValue(ActSplit split, string flag, string value) => flag switch
    {
        ProcessFlag => split with { Processes = split.Processes.Add(value) },
        OnlyFlag => split with { Only = value },
        _ => split with { Volumes = split.Volumes.Add(value) },
    };

    /// <summary>Why <c>--volume</c> / <c>--only</c> at <paramref name="i"/> cannot be taken: no value, or a second <c>--only</c>.</summary>
    private static Request.Failed? ShownValueProblem(IReadOnlyList<string> rest, int i, string only) =>
        NeedsValue(rest, i) && !IsStdinOnly(rest, i) ? new Request.Failed($"\"{BinaryName} act\": {rest[i]} needs a value ({ShownValueKind(rest[i])}).")
        : rest[i] == OnlyFlag && only.Length > 0 ? new Request.Failed($"\"{BinaryName} act\" takes {OnlyFlag} once.")
        : null;

    private static string ShownValueKind(string flag) => flag switch
    {
        VolumeFlag => "a 64-hex anonymous volume name",
        ProcessFlag => "a process A18's or A19's preview showed, as <pid>:<start ticks>",
        _ => "a file of 64-hex names, one per line",
    };

    /// <summary>The option at <paramref name="i"/> has no value after it: the end, or another option.</summary>
    private static bool NeedsValue(IReadOnlyList<string> rest, int i) => i + 1 >= rest.Count || rest[i + 1].StartsWith('-');

    private static Request.Failed? ActFlags(IReadOnlyList<string> flags) => UnknownActFlag(flags) ?? ActMode(flags) ?? ActMark(flags) ?? ActDetach(flags);

    /// <summary><c>--only -</c>: the one value of <c>--only</c> that starts with a dash — stdin (E6.S1, §15j M2).</summary>
    private static bool IsStdinOnly(IReadOnlyList<string> rest, int i) => rest[i] == OnlyFlag && i + 1 < rest.Count && rest[i + 1] == StdinMarker;

    /// <summary><c>--detach</c> starts a CONFIRMED run in its unit (§15j B2): never a preview, never the timer's.</summary>
    private static Request.Failed? ActDetach(IReadOnlyList<string> flags) =>
        flags.Contains(DetachFlag) && (!flags.Contains(ConfirmFlag) || flags.Contains(TimerFlag))
            ? new Request.Failed($"\"{BinaryName} act\": {DetachFlag} runs a {ConfirmFlag} in its own unit; it takes neither {PreviewFlag} nor {TimerFlag}.")
            : null;

    private static Request.Failed? UnknownActFlag(IReadOnlyList<string> flags) =>
        flags.Any(f => f is not (PreviewFlag or ConfirmFlag or JsonFlag or ManualFlag or TimerFlag or DetachFlag)) || flags.Distinct(StringComparer.Ordinal).Count() != flags.Count
            ? new Request.Failed($"\"{BinaryName} act\" takes {PreviewFlag} or {ConfirmFlag}, and {ManualFlag} or {TimerFlag}, {DetachFlag}, {JsonFlag}, each once, besides {VolumeFlag} <name>, {OnlyFlag} <file or -> and {ProcessFlag} <pid:start>; got \"{Printable(string.Join(' ', flags))}\".")
            : null;

    private static Request.Failed? ActMode(IReadOnlyList<string> flags) =>
        flags.Contains(PreviewFlag) == flags.Contains(ConfirmFlag)
            ? new Request.Failed($"\"{BinaryName} act\" needs exactly one of {PreviewFlag} (show what it would do) and {ConfirmFlag} (do it; the panel's button passes it after you confirmed).")
            : null;

    /// <summary>The panel's mark and the timer's are exclusive (plan §15j m2): a run is started by one of them, never both.</summary>
    private static Request.Failed? ActMark(IReadOnlyList<string> flags) =>
        flags.Contains(ManualFlag) && flags.Contains(TimerFlag)
            ? new Request.Failed($"\"{BinaryName} act\" takes {ManualFlag} (the panel's button) or {TimerFlag} (the systemd timer), not both.")
            : null;

    /// <summary>A shown list belongs to A4 alone, and every <c>--volume</c> is an anonymous volume's 64-hex name.</summary>
    private static Request.Failed? ShownListFailure(IReadOnlyList<Core.Actions.ActionId> ids, IReadOnlyList<string> volumes, string only) => volumes switch
    {
        _ when ShownWithoutA4(ids, volumes, only) => new Request.Failed($"\"{BinaryName} act\": {VolumeFlag} and {OnlyFlag} name the volumes A4's preview showed; they need A4 among the actions."),
        _ when volumes.FirstOrDefault(v => !Core.Docker.DockerJson.IsFullId(v)) is { } bad => new Request.Failed($"\"{BinaryName} act\": {VolumeFlag} \"{Printable(bad)}\" is not an anonymous volume's name (64 lowercase hex digits)."),
        _ when volumes.Count > MaxShownVolumes => new Request.Failed($"\"{BinaryName} act\" takes at most {MaxShownVolumes} volumes."),
        _ => null,
    };

    /// <summary>A shown process list belongs to the actions bound to one (<see cref="Core.Actions.ActionId.ShownProcessIds"/>: A18,
    /// E7.S2b review A-H1; A19, plan E14 S2a), and every <c>--process</c> is <c>pid:start</c>.</summary>
    private static Request.Failed? ShownProcessesFailure(IReadOnlyList<Core.Actions.ActionId> ids, IReadOnlyList<string> processes) => processes switch
    {
        { Count: 0 } => null,
        _ when !ids.Any(Core.Actions.ActionId.ShownProcessIds.Contains) => new Request.Failed($"\"{BinaryName} act\": {ProcessFlag} names the processes a preview of {string.Join(" or ", Core.Actions.ActionId.ShownProcessIds)} showed; it needs one of them among the actions."),
        _ when processes.FirstOrDefault(p => !Core.Actions.Suspects.SuspectSignals.IsShownKey(p)) is { } bad => new Request.Failed($"\"{BinaryName} act\": {ProcessFlag} \"{Printable(bad)}\" is not a process as A18's or A19's preview shows it (<pid>:<start ticks>)."),
        _ when processes.Count > MaxShownVolumes => new Request.Failed($"\"{BinaryName} act\" takes at most {MaxShownVolumes} processes."),
        _ => null,
    };

    private static bool ShownWithoutA4(IReadOnlyList<Core.Actions.ActionId> ids, IReadOnlyList<string> volumes, string only) =>
        (volumes.Count > 0 || only.Length > 0) && !ids.Any(id => id.Text == "A4");

    /// <summary>The names of an <c>--only</c> file: one 64-hex name per non-empty line (a trailing CR tolerated), at most
    /// <see cref="MaxShownVolumes"/> — or why not, naming the LINE, never echoing what is on it.</summary>
    internal static (IReadOnlyList<string> Names, string Failure) ShownVolumesFile(string text)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r').Trim()).ToList();
        var bad = lines.Select((line, index) => (line, index)).FirstOrDefault(l => l.line.Length > 0 && !Core.Docker.DockerJson.IsFullId(l.line));
        if (bad.line is { Length: > 0 })
        {
            return ([], $"line {bad.index + 1} of the {OnlyFlag} file is not an anonymous volume's name (64 lowercase hex digits)");
        }

        var names = lines.Where(l => l.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        return names.Count > MaxShownVolumes ? ([], $"the {OnlyFlag} file names more than {MaxShownVolumes} volumes") : (names, string.Empty);
    }

    private static Request ParseLogs(IReadOnlyList<string> rest) =>
        ReadOptions("logs", rest, [PeriodFlag, FromFlag, ToFlag, ActionFlag], [DetailFlag, JsonFlag]) switch
        {
            (_, { } failure) => failure,
            var (options, _) when RangeFailure("logs", options) is { } failure => failure,
            var (options, _) when options.Values.TryGetValue(ActionFlag, out var id) && Core.Actions.ActionId.Find(id) is null =>
                new Request.Failed($"\"{BinaryName} logs\": {ActionFlag} \"{Printable(id)}\" is not an action; the actions are {string.Join(", ", Core.Actions.ActionId.All.Select(a => a.Text))}."),
            var (options, _) => new Request.Logs(
                PeriodOf(options),
                options.Values.TryGetValue(ActionFlag, out var action) ? Core.Actions.ActionId.Find(action) : null,
                options.Flags.Contains(JsonFlag),
                options.Flags.Contains(DetailFlag))
            {
                From = options.Values.GetValueOrDefault(FromFlag, string.Empty),
                To = options.Values.GetValueOrDefault(ToFlag, string.Empty),
            },
        };

    private static Request ParseAgentsList(IReadOnlyList<string> rest) =>
        ReadOptions("agents list", rest, [], [MeasureFlag, JsonFlag]) switch
        {
            (_, { } failure) => failure,
            var (options, _) => new Request.AgentsList(options.Flags.Contains(MeasureFlag), options.Flags.Contains(JsonFlag)),
        };

    /// <summary>Exactly one absolute distro path (plan §15q R2.1's shape: no control character, no leading '-', no '.' or '..'
    /// segment, at most 1 024 characters), then optionally <c>--json</c>.</summary>
    private static Request ParseArchivePreview(IReadOnlyList<string> rest) =>
        ReadOptions("archive preview", rest, [AgentFlag], [JsonFlag]) switch
        {
            (_, { } failure) => failure,
            var (options, _) when options.Values.TryGetValue(AgentFlag, out var agent) && (agent.Contains(',', StringComparison.Ordinal) || Core.Config.ConfigValidation.Parse(Core.Config.ConfigKeys.Archive.Agents, agent) is not Core.Config.ValueCheck.Ok) =>
                new Request.Failed($"\"{BinaryName} archive preview --agent\" takes one of {string.Join(", ", Core.Agents.AgentCatalogue.ArchivableIds)} or {Core.Agents.ExtraAgent.IdPrefix}<name>; got \"{Printable(agent)}\"."),
            var (options, _) => new Request.ArchivePreview(options.Values.GetValueOrDefault(AgentFlag, string.Empty), options.Flags.Contains(JsonFlag)),
        };

    private static Request ParseArchiveCheckBase(IReadOnlyList<string> rest) => rest switch
    {
        [var path] when IsPathArgument(path) => new Request.ArchiveCheckBase(path, false),
        [var path, JsonFlag] when IsPathArgument(path) => new Request.ArchiveCheckBase(path, true),
        _ => new Request.Failed($"\"{BinaryName} archive check-base\" needs exactly one <path> (not starting with -, no control character) and optionally {JsonFlag}: {BinaryName} archive check-base <path> [{JsonFlag}]."),
    };

    private static bool IsPathArgument(string path) => path.Length > 0 && !path.StartsWith('-') && !path.Any(char.IsControl);

    private static Request ParseAgentsProbe(IReadOnlyList<string> rest) => rest switch
    {
        [var path] when ProbePathProblem(path).Length == 0 => new Request.AgentsProbe(path, false),
        [var path, JsonFlag] when ProbePathProblem(path).Length == 0 => new Request.AgentsProbe(path, true),
        [var path, var extra, ..] when ProbePathProblem(path).Length == 0 => new Request.Failed($"\"{BinaryName} agents probe\" takes one path and optionally {JsonFlag}; got an extra argument \"{Printable(extra)}\"."),
        [var path, ..] when !path.StartsWith('-') => new Request.Failed($"\"{BinaryName} agents probe\": the path {ProbePathProblem(path)}; got \"{Printable(path)}\"."),
        _ => new Request.Failed($"\"{BinaryName} agents probe\" needs exactly one <path> and optionally {JsonFlag}: {BinaryName} agents probe <path> [{JsonFlag}]."),
    };

    private static string ProbePathProblem(string path) =>
        Core.Agents.ExtraAgentShape.PathProblem(path, Core.Agents.ExtraAgentShape.Wsl) is { Length: > 0 } problem ? problem : string.Empty;

    private static Request ParseRuns(IReadOnlyList<string> rest) =>
        ReadOptions("runs", rest, [PeriodFlag, FromFlag, ToFlag], [JsonFlag]) switch
        {
            (_, { } failure) => failure,
            var (options, _) when RangeFailure("runs", options) is { } failure => failure,
            var (options, _) => new Request.Runs(PeriodOf(options), options.Flags.Contains(JsonFlag))
            {
                From = options.Values.GetValueOrDefault(FromFlag, string.Empty),
                To = options.Values.GetValueOrDefault(ToFlag, string.Empty),
            },
        };

    /// <summary>The period text: empty in the instant-range mode (coai E6 code round #3), else <c>--period</c> or today.</summary>
    private static string PeriodOf(Options options) =>
        options.Values.ContainsKey(FromFlag) ? string.Empty : options.Values.GetValueOrDefault(PeriodFlag, Core.History.LogPeriod.Today);

    /// <summary>The instant range (plan §15j M7) comes whole — <c>--from</c> AND <c>--to</c> — and never beside <c>--period</c>;
    /// the instants themselves are checked by the verb (<see cref="Core.History.LogPeriod.ParseInstants"/>).</summary>
    private static Request.Failed? RangeFailure(string verb, Options options) =>
        (options.Values.ContainsKey(PeriodFlag), options.Values.ContainsKey(FromFlag), options.Values.ContainsKey(ToFlag)) switch
        {
            (true, true, _) or (true, _, true) => new Request.Failed($"\"{BinaryName} {verb}\" takes {PeriodFlag} or {FromFlag} with {ToFlag}, not both."),
            (_, true, false) => new Request.Failed($"\"{BinaryName} {verb}\": {FromFlag} needs {ToFlag} (the instant range is half-open: from inclusive, to exclusive)."),
            (_, false, true) => new Request.Failed($"\"{BinaryName} {verb}\": {ToFlag} needs {FromFlag} (the instant range is half-open: from inclusive, to exclusive)."),
            _ => null,
        };

    /// <summary><c>runs show &lt;runId&gt; [--json]</c>: exactly one well-formed run id, then optionally <c>--json</c>.</summary>
    private static Request ParseUnitsDropIn(IReadOnlyList<string> rest) => rest switch
    {
        [var unit] when Core.Systemd.UnitDropIns.Units.Contains(unit, StringComparer.Ordinal) => new Request.UnitsDropIn(unit),
        _ => new Request.Failed($"\"{BinaryName} units dropin\" takes exactly one unit: {string.Join(", ", Core.Systemd.UnitDropIns.Units)}."),
    };

    private static Request ParseRunsShow(IReadOnlyList<string> rest) =>
        RunIdVerb("runs show", rest, takesJson: true, (runId, json) => new Request.RunsShow(runId, json));

    /// <summary>The options a verb was given: each valued one with its value, each switch present.</summary>
    private sealed record Options(IReadOnlyDictionary<string, string> Values, IReadOnlySet<string> Flags);

    /// <summary>Each of <paramref name="valued"/> with its value and each of <paramref name="switches"/>, each at most once;
    /// nothing else.</summary>
    private static (Options Options, Request.Failed? Failure) ReadOptions(string verb, IReadOnlyList<string> rest, IReadOnlyList<string> valued, IReadOnlyList<string> switches)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < rest.Count; i = Take(rest, i, valued, values, flags))
        {
            if (OptionProblem(rest, i, valued, switches, values, flags) is { Length: > 0 } problem)
            {
                return (new Options(values, flags), new Request.Failed($"\"{BinaryName} {verb}\" {problem}."));
            }
        }

        return (new Options(values, flags), null);
    }

    /// <summary>Why the option at <paramref name="i"/> cannot be taken; empty when it can.</summary>
    private static string OptionProblem(IReadOnlyList<string> rest, int i, IReadOnlyList<string> valued, IReadOnlyList<string> switches, Dictionary<string, string> values, HashSet<string> flags) => rest[i] switch
    {
        var flag when switches.Contains(flag) => Twice(flag, flags.Contains(flag)),
        var option when valued.Contains(option) => NeedsValue(rest, i) ? $"{option} needs a value" : Twice(option, values.ContainsKey(option)),
        var other => $"does not take \"{Printable(other)}\"; it takes {string.Join(", ", valued.Select(v => v + " <value>").Concat(switches))}, each once",
    };

    private static string Twice(string option, bool given) => given ? $"{option} is given twice" : string.Empty;

    /// <summary>Takes the option at <paramref name="i"/> (already judged); the index of the next one.</summary>
    private static int Take(IReadOnlyList<string> rest, int i, IReadOnlyList<string> valued, Dictionary<string, string> values, HashSet<string> flags)
    {
        if (valued.Contains(rest[i]))
        {
            values[rest[i]] = rest[i + 1];
            return i + 2;
        }

        flags.Add(rest[i]);
        return i + 1;
    }

    private static Request ParsePreview(IReadOnlyList<string> rest) => rest switch
    {
        [AllFlag] => new Request.Preview(Json: false),
        [AllFlag, JsonFlag] or [JsonFlag, AllFlag] => new Request.Preview(Json: true),
        _ => new Request.Failed($"\"{BinaryName} preview\" needs {AllFlag} (and optionally {JsonFlag}); one action's preview is \"{BinaryName} act <A#> --preview\", which arrives with the actions; got \"{Printable(string.Join(' ', rest))}\"."),
    };

    private static string BuildHelpText()
    {
        var width = Commands.Max(c => c.Usage.Length);
        var text = new StringBuilder()
            // ASCII only: a Windows console on an OEM code page turns an em dash into '?'.
            .AppendLine($"{BinaryName}: keeps the WSL VM on this machine from degrading over the working day.")
            .AppendLine()
            .AppendLine("Usage:");
        foreach (var command in Commands)
        {
            text.AppendLine($"  {BinaryName} {command.Usage.PadRight(width)}  {command.Summary}");
        }

        return text
            .AppendLine()
            .AppendLine("Settings are read from three layers, each overriding the last: the embedded defaults, the machine")
            .AppendLine("file, and the user file that \"config set\" writes. \"config get\" names the layer behind every value.")
            .AppendLine()
            .Append($"This build answers only the commands above; \"act\" holds these actions: {string.Join(", ", Core.Actions.ActionRegistry.Product.Actions.Select(a => a.Id.Text))}.")
            .ToString();
    }

    /// <summary>
    /// User text echoed back in an error message, with control characters replaced, so a refusal
    /// stays ONE line on stderr whatever was typed — a newline or an escape sequence in an argument
    /// would otherwise split or repaint the message.
    /// </summary>
    internal static string Printable(string text) =>
        string.Create(text.Length, text, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = char.IsControl(source[i]) ? '?' : source[i];
            }
        });
}
