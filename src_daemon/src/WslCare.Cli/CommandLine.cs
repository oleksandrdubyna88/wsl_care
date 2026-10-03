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

    /// <summary><c>collect [--json]</c>: the full run (plan §6) — measured, recorded when this process may write the state.</summary>
    internal sealed record Collect(bool Json) : Request;

    /// <summary><c>doctor [--json]</c>: is the installation doing its job (plan §6).</summary>
    internal sealed record Doctor(bool Json) : Request;

    /// <summary><c>events follow [--once]</c>: the container-start follower (plan §4.3); <c>--once</c> catches up and stops.</summary>
    internal sealed record EventsFollow(bool Once) : Request;

    /// <summary><c>logs [--period …] [--action &lt;A#&gt;] [--detail] [--json]</c> (plan §7.4): the period's totals and cleanups —
    /// the objects each removed only with <c>--detail</c> or one <c>--action</c> (gate finding #10); the period
    /// text is checked against the clock by the verb.</summary>
    internal sealed record Logs(string Period, Core.Actions.ActionId? Action, bool Json, bool Detail = false) : Request;

    /// <summary><c>runs [--period …] [--json]</c> (plan §7.4): every run of the period.</summary>
    internal sealed record Runs(string Period, bool Json) : Request;

    /// <summary><c>act &lt;A#&gt;[,&lt;A#&gt;…] (--preview or --confirm) [--manual] [--volume &lt;name&gt;]... [--only &lt;file&gt;] [--json]</c>
    /// (plan §6): preview the actions, or run them — a destructive run from the CLI needs <c>--confirm</c> (the button passes it
    /// after the person confirmed). <c>--manual</c> is the panel's mark (the run's trigger is <c>manual</c>); <c>--volume</c> and
    /// <c>--only</c> carry the volumes A4's preview SHOWED (E3.S2).</summary>
    internal sealed record Act(IReadOnlyList<Core.Actions.ActionId> Ids, bool Confirm, bool Json) : Request
    {
        /// <summary>The panel's button started it: recorded as <c>manual</c>, not <c>cli</c>.</summary>
        public bool Manual { get; init; }

        /// <summary>Every <c>--volume</c> given, each already a 64-hex anonymous volume name.</summary>
        public IReadOnlyList<string> Volumes { get; init; } = [];

        /// <summary>The <c>--only</c> file (one 64-hex name per line), read by the verb; empty when not given.</summary>
        public string OnlyFile { get; init; } = string.Empty;

        /// <summary>Whether a shown list was passed at all.</summary>
        public bool HasShownList => Volumes.Count > 0 || OnlyFile.Length > 0;
    }
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
/// documented and refused. The remaining verbs of plan §6 (<c>agents</c>, <c>runs show</c> / <c>runs log</c>, …) arrive in later
/// stories as entries here.</para>
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
    private const string VolumeFlag = "--volume";
    private const string OnlyFlag = "--only";
    private const string PeriodFlag = "--period";
    private const string ActionFlag = "--action";
    private const string DetailFlag = "--detail";

    /// <summary>The most names one <c>act</c> may carry through <c>--volume</c> and <c>--only</c> together — far above the 387
    /// volumes of 2026-10-02, low enough that a mistaken file cannot make a run of millions.</summary>
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
        new([["collect"]], "collect [--json]", "the full run: every collector, the thresholds, recorded as run detail + history line (as root; read-only otherwise)", ["collect", "--json"], rest => JsonOnly("collect", rest, json => new Request.Collect(json))),
        new([["doctor"]], "doctor [--json]", "is the installation doing its job: units, collectors, configuration, last run, versions", ["doctor", "--json"], rest => JsonOnly("doctor", rest, json => new Request.Doctor(json))),
        new([["events", "follow"]], "events follow [--once]", "record every container start under the state directory (the wsl-care-events unit); --once catches up and stops", ["events", "follow", "--once"], ParseEventsFollow),
        new([["act"]], "act <A#>[,<A#>...] (--preview or --confirm) [--manual] [--volume <name>]... [--only <file>] [--json]", "as root: preview the actions from live state, or run them (--confirm), one run at a time, recorded; --manual marks the panel's button, --volume / --only the volumes A4's preview showed", ["act", "A10", "--preview", "--json"], ParseAct),
        new([["logs"]], "logs [--period <today, yesterday, yyyy-MM-dd or from..to>] [--action <A#>] [--detail] [--json]", "what the runs of a period freed, per action; runs with and without a cleanup; max and min; every object removed with --detail or one --action (read-only, UTC days)", ["logs", "--period", "today", "--json"], ParseLogs),
        new([["runs"]], "runs [--period <today, yesterday, yyyy-MM-dd or from..to>] [--json]", "every run of a period: trigger, outcome, dry run, actions, freed (read-only, UTC days)", ["runs", "--period", "yesterday", "--json"], ParseRuns),
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

        if (Core.Actions.ActionId.Parse(rest[0]) is Core.Actions.ActionIdList.Refused refused)
        {
            return new Request.Failed($"\"{BinaryName} act\": {Printable(refused.Reason)}.");
        }

        var ids = ((Core.Actions.ActionIdList.Parsed)Core.Actions.ActionId.Parse(rest[0])).Ids;
        return SplitActOptions(rest.Skip(1).ToList()) switch
        {
            (_, _, _, { } failure) => failure,
            var (flags, volumes, only, _) when ActFlags(flags) is { } failure => failure,
            var (_, volumes, only, _) when ShownListFailure(ids, volumes, only) is { } failure => failure,
            var (flags, volumes, only, _) => new Request.Act(ids, flags.Contains(ConfirmFlag), flags.Contains(JsonFlag)) { Manual = flags.Contains(ManualFlag), Volumes = volumes, OnlyFile = only },
        };
    }

    /// <summary>The flags, the <c>--volume</c> values and the <c>--only</c> file, apart — or the first refusal.</summary>
    private static (List<string> Flags, List<string> Volumes, string Only, Request.Failed? Failure) SplitActOptions(IReadOnlyList<string> rest)
    {
        var (flags, volumes, only) = (new List<string>(), new List<string>(), string.Empty);
        for (var i = 0; i < rest.Count; i++)
        {
            if (rest[i] is not (VolumeFlag or OnlyFlag))
            {
                flags.Add(rest[i]);
                continue;
            }

            if (i + 1 >= rest.Count || rest[i + 1].StartsWith('-'))
            {
                return (flags, volumes, only, new Request.Failed($"\"{BinaryName} act\": {rest[i]} needs a value ({(rest[i] == VolumeFlag ? "a 64-hex anonymous volume name" : "a file of 64-hex names, one per line")})."));
            }

            if (rest[i] == OnlyFlag && only.Length > 0)
            {
                return (flags, volumes, only, new Request.Failed($"\"{BinaryName} act\" takes {OnlyFlag} once."));
            }

            (only, volumes) = rest[i] == OnlyFlag ? (rest[i + 1], volumes) : (only, [.. volumes, rest[i + 1]]);
            i++;
        }

        return (flags, volumes, only, null);
    }

    private static Request.Failed? ActFlags(IReadOnlyList<string> flags)
    {
        var unknown = flags.Where(f => f is not (PreviewFlag or ConfirmFlag or JsonFlag or ManualFlag)).ToList();
        if (unknown.Count > 0 || flags.Distinct(StringComparer.Ordinal).Count() != flags.Count)
        {
            return new Request.Failed($"\"{BinaryName} act\" takes {PreviewFlag} or {ConfirmFlag}, and {ManualFlag}, {JsonFlag}, each once, besides {VolumeFlag} <name> and {OnlyFlag} <file>; got \"{Printable(string.Join(' ', flags))}\".");
        }

        return flags.Contains(PreviewFlag) == flags.Contains(ConfirmFlag)
            ? new Request.Failed($"\"{BinaryName} act\" needs exactly one of {PreviewFlag} (show what it would do) and {ConfirmFlag} (do it; the panel's button passes it after you confirmed).")
            : null;
    }

    /// <summary>A shown list belongs to A4 alone, and every <c>--volume</c> is an anonymous volume's 64-hex name.</summary>
    private static Request.Failed? ShownListFailure(IReadOnlyList<Core.Actions.ActionId> ids, IReadOnlyList<string> volumes, string only)
    {
        if ((volumes.Count > 0 || only.Length > 0) && !ids.Any(id => id.Text == "A4"))
        {
            return new Request.Failed($"\"{BinaryName} act\": {VolumeFlag} and {OnlyFlag} name the volumes A4's preview showed; they need A4 among the actions.");
        }

        if (volumes.FirstOrDefault(v => !Core.Docker.DockerJson.IsFullId(v)) is { } bad)
        {
            return new Request.Failed($"\"{BinaryName} act\": {VolumeFlag} \"{Printable(bad)}\" is not an anonymous volume's name (64 lowercase hex digits).");
        }

        return volumes.Count > MaxShownVolumes
            ? new Request.Failed($"\"{BinaryName} act\" takes at most {MaxShownVolumes} volumes.")
            : null;
    }

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
        ReadOptions("logs", rest, [PeriodFlag, ActionFlag], [DetailFlag, JsonFlag]) switch
        {
            (_, { } failure) => failure,
            var (options, _) when options.Values.TryGetValue(ActionFlag, out var id) && Core.Actions.ActionId.Find(id) is null =>
                new Request.Failed($"\"{BinaryName} logs\": {ActionFlag} \"{Printable(id)}\" is not an action; the actions are {string.Join(", ", Core.Actions.ActionId.All.Select(a => a.Text))}."),
            var (options, _) => new Request.Logs(
                options.Values.GetValueOrDefault(PeriodFlag, Core.History.LogPeriod.Today),
                options.Values.TryGetValue(ActionFlag, out var action) ? Core.Actions.ActionId.Find(action) : null,
                options.Flags.Contains(JsonFlag),
                options.Flags.Contains(DetailFlag)),
        };

    private static Request ParseRuns(IReadOnlyList<string> rest) =>
        ReadOptions("runs", rest, [PeriodFlag], [JsonFlag]) switch
        {
            (_, { } failure) => failure,
            var (options, _) => new Request.Runs(options.Values.GetValueOrDefault(PeriodFlag, Core.History.LogPeriod.Today), options.Flags.Contains(JsonFlag)),
        };

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
        var flag when switches.Contains(flag) => flags.Contains(flag) ? $"{flag} is given twice" : string.Empty,
        var option when valued.Contains(option) && (i + 1 >= rest.Count || rest[i + 1].StartsWith('-')) => $"{option} needs a value",
        var option when valued.Contains(option) => values.ContainsKey(option) ? $"{option} is given twice" : string.Empty,
        var other => $"does not take \"{Printable(other)}\"; it takes {string.Join(", ", valued.Select(v => v + " <value>").Concat(switches))}, each once",
    };

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
