using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Docker;
using WslCare.Core.Health;
using WslCare.TestSupport;

using static System.FormattableString;

namespace WslCare.Scenarios;

/// <summary>One volatile JSON path and what it is replaced with.</summary>
/// <param name="Pattern">A path over property names and <c>[*]</c> array elements, dot-separated; a leading <c>**.</c> matches
/// at any depth (<c>**.sampledAt</c>), otherwise the whole path (<c>vm.disk.usedBytes</c>).</param>
/// <param name="Why">Why the value differs between two runs of the same build over the same fixtures.</param>
internal sealed record GoldenRule(string Pattern, string Why, Func<JsonNode, JsonNode> Replace)
{
    public bool Matches(string path) =>
        Pattern.StartsWith("**.", StringComparison.Ordinal)
            ? path == Pattern[3..] || path.EndsWith("." + Pattern[3..], StringComparison.Ordinal)
            : path == Pattern;
}

/// <summary>One object picked out by a key member (<c>"id": "disk.root"</c>), whose named members are volatile.</summary>
internal sealed record GoldenObjectRule(string KeyMember, string KeyValue, string Why, IReadOnlyDictionary<string, Func<string, string>> Members)
{
    public string Name => $"{KeyMember}:{KeyValue}";
}

/// <summary>A volatile fragment INSIDE a sentence (a run id quoted in a reason), rewritten in every string.</summary>
internal sealed record GoldenTextRule(string Name, string Why, Regex Fragment, string Replacement);

/// <summary>
/// The golden JSON of the read-only verbs the extension calls (plan §15f #10, §15g m7): the BUILT CLI's answers over the
/// captured fixtures — <c>collect</c> first, then <c>status --json</c>, <c>preview --all --json</c> and <c>doctor --json</c> —
/// with every value that differs between two runs replaced by a fixed value OF THE SAME TYPE, so the extension's client
/// tests can replay them as real answers. The files live in <c>contracts/golden/head/</c>; the set frozen at a daemon tag
/// (<c>contracts/golden/daemon-0.1.0/</c>) is made at the E5 live gate, never here.
/// </summary>
/// <remarks>Linux only: the Linux binary reads the captured procfs tree; the Windows binary answers for another side. Every
/// age limit is 0 (<see cref="PreviewFlows.AllAges"/>) so the cleanup rows do not move as the fixtures age.</remarks>
internal static partial class GoldenContracts
{
    /// <summary>Set to <c>1</c> to WRITE the goldens instead of only comparing them (run in WSL or on a Linux leg).</summary>
    public const string WriteVariable = "WSL_CARE_WRITE_GOLDENS";

    public const string FixedInstant = "2000-01-01T00:00:00+00:00";

    public const string FixedRunId = "20000101T000000Z-1";

    /// <summary>The fixed root every sandbox path is rewritten under.</summary>
    public const string FixedRoot = "/golden-root";

    /// <summary>What a moving version is replaced with: the contract's own value for a build with no stamped version (plan
    /// §6, §15g M2), which a client renders rather than refuses.</summary>
    public const string FixedVersion = "unknown";

    /// <summary>The answers, in the order the files are written: the file name, and the argv that produces it.</summary>
    public static IReadOnlyList<(string File, string[] Argv)> Answers =>
    [
        ("status.json", ["status", "--json"]),
        ("preview.json", ["preview", "--all", "--json"]),
        ("doctor.json", ["doctor", "--json"]),
    ];

    public static string HeadDirectory => Path.Combine(ReleaseFiles.Root, "contracts", "golden", "head");

    /// <summary>
    /// THE normalisation list, part 1 — paths. Every rule names a value that moves between two runs of one build over one
    /// fixture set (the clock, the CLI's pid, the temporary sandbox, the runner's own disk, the commit and the release
    /// number) and nothing else: a rule that matches nothing fails <c>GoldenContractTests</c>, and a value that moves without
    /// a rule makes the checked-in files stale, which fails it too.
    /// </summary>
    public static IReadOnlyList<GoldenRule> Rules { get; } =
    [
        new("**.sampledAt", "the instant the sample or a slow part was taken", _ => FixedInstant),
        new("**.sampleMilliseconds", "how long the fast sample took", _ => 0),
        new("**.ageSeconds", "the age of a slow part, the folder sample or a carried verdict at the time of the answer", AgeSeconds),
        new("**.ageSeconds.value", "a process's age is now minus its start", _ => 0),
        new("**.evaluatedAt", "when a verdict was evaluated (this sample, or the end of the full run)", _ => FixedInstant),
        new("**.runId", "a run's id is its start instant and the CLI's pid", _ => FixedRunId),
        new("checkedAt", "the instant doctor answered", _ => FixedInstant),
        new("productVersion", "the commit after +, and the release number, which every release-please bump moves (a golden pinned to it would turn the release pull request red)", _ => FixedVersion),
        new("vm.disk.path", "df / is the sandbox's filesystem", _ => FixedRoot),
        new("vm.disk.totalBytes", "df / is the runner's own disk", _ => 100_000_000_000L),
        new("vm.disk.usedBytes", "df / is the runner's own disk", _ => 13_000_000_000L),
        new("vm.disk.availableBytes", "df / is the runner's own disk", _ => 87_000_000_000L),
        new("vm.disk.usedPercent", "df / is the runner's own disk", _ => 13.0),
        new("slow.windowsClock.offsetSeconds", "the fake clock probe answers a captured instant, so the offset is that instant minus now", _ => 0),
        new("containerStarts.from", "the 24-hour window ends now", _ => FixedInstant),
        new("containerStarts.to", "the 24-hour window ends now", _ => FixedInstant),
        new("containerStarts.gaps[*].from", "the gap spans the window, which ends now", _ => FixedInstant),
        new("containerStarts.gaps[*].to", "the gap spans the window, which ends now", _ => FixedInstant),
    ];

    /// <summary>THE normalisation list, part 2 — objects picked out by a key member, whose figure is the runner's or the
    /// clock's rather than the fixtures'.</summary>
    public static IReadOnlyList<GoldenObjectRule> ObjectRules { get; } =
    [
        new("id", "disk.root", "df / of the runner's own disk — judged as the fixed disk above would be (13 % used: ok)", new Dictionary<string, Func<string, string>>
        {
            ["level"] = _ => "ok",
            ["value"] = _ => "13.0 %",
        }),
        new("id", "journal.history", "now minus the captured oldest journal entry: its days grow every day, and at 7 days the level turns ok — fixed as it was AT THE CAPTURE: the health capture's instant minus that entry (0.8 days, warn)", new Dictionary<string, Func<string, string>>
        {
            ["level"] = _ => "warn",
            ["value"] = value => LeadingDays().Replace(value, Invariant($"{JournalDaysAtCapture:0.0} days")),
        }),
        new("id", "clock.drift", "the captured Windows clock minus now — fixed as it was AT THE CAPTURE: the clock probe's process start minus the capture's instant (+0.19 s, within the limit, so ok with the product's own sentence for that case)", new Dictionary<string, Func<string, string>>
        {
            ["level"] = _ => "ok",
            ["value"] = value => LeadingOffset().Replace(value, Invariant($"{ClockOffsetAtCapture:+0.00;-0.00} s")),
            ["reason"] = _ => "the distro's clock agrees with Windows'",
        }),
        new("component", "wsl-care", "doctor's own version: the release number every release-please bump moves", new Dictionary<string, Func<string, string>>
        {
            ["version"] = _ => FixedVersion,
        }),
    ];

    /// <summary>THE normalisation list, part 3 — fragments inside sentences.</summary>
    public static IReadOnlyList<GoldenTextRule> TextRules { get; } =
    [
        new("runIdInText", "a run id quoted in a reason or a basis (its start instant and the CLI's pid)", RunIdInText(), FixedRunId),
    ];

    /// <summary>
    /// THE normalisation list, part 4 — identity: <see cref="FixtureIdentity.Rules"/>, the SAME list the captured fixtures
    /// were anonymised with, applied to every string of every answer (after the sandbox root is rewritten), so a golden
    /// regenerated from a new capture is anonymised by the code that anonymised the capture — the sandbox's own home
    /// (<c>/golden-root/home/me</c>) included. Its mappings are learnt from the checked-in captured fixtures. Unlike parts
    /// 1–3, a part-4 rule need not match: it guards a future capture, and <c>FixturePrivacyTests</c> is what proves no
    /// identity is left in the files.
    /// </summary>
    public static IReadOnlyList<IdentityRule> IdentityRules => FixtureIdentity.Rules;

    /// <summary>The identity mappings, learnt once from the checked-in captured fixtures.</summary>
    public static FixtureIdentity Identity => IdentityOnce.Value;

    private static readonly Lazy<FixtureIdentity> IdentityOnce = new(() => FixtureIdentity.LearnFrom(Path.Combine(ReleaseFiles.Root, "src_daemon", "tests", "fixtures")));

    /// <summary>The journal's span at the health capture: its instant minus the oldest entry the captured boots list names.</summary>
    private static double JournalDaysAtCapture =>
        (HealthFixture.CapturedAt - HealthParsers.OldestJournalEntry(HealthFixture.Read("journalctl-list-boots.out")).ValueOr(HealthFixture.CapturedAt)).TotalDays;

    /// <summary>The Windows clock offset at the health capture: the captured probe's process start minus the capture's
    /// instant (the launch instant the live collection used).</summary>
    private static double ClockOffsetAtCapture =>
        HealthParsers.WindowsClock(HealthFixture.Read("powershell-clock.out")).ValueOr(new WindowsClockAnswer(HealthFixture.CapturedAt, HealthFixture.CapturedAt, string.Empty))
            .ProcessStartedAt.Subtract(HealthFixture.CapturedAt).TotalSeconds;

    private static JsonNode AgeSeconds(JsonNode node) => node is JsonObject ? node : 0;

    /// <summary>Runs the built CLI over the captured fixtures and answers each file's normalised text, plus which rules
    /// matched.</summary>
    public static async Task<(IReadOnlyList<(string File, string Text)> Files, IReadOnlySet<string> Matched)> ProduceAsync()
    {
        using var home = CollectFlows.Captured("golden");
        home.Script(DockerCommands.Executable, DockerCommands.Version.Arguments, 0, $"docker/{DockerFixture.Name}/version.out");
        Directory.CreateDirectory(Path.GetDirectoryName(home.Paths.UserConfigFile)!);
        await File.WriteAllTextAsync(home.Paths.UserConfigFile, PreviewFlows.AllAges, TestContext.Current.CancellationToken);
        ProcfsFixture.CopyTo(home.SandboxRoot);
        (await home.RunAsync("collect")).Exit.Should().Be((int)ExitCode.Ok, "the goldens follow one recorded full run");

        var matched = new HashSet<string>(StringComparer.Ordinal);
        var files = new List<(string, string)>();
        foreach (var (file, argv) in Answers)
        {
            var result = await home.RunAsync(argv);
            result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
            files.Add((file, Normalise(result.Stdout, home.SandboxRoot, matched)));
        }

        return (files, matched);
    }

    /// <summary>Every rule's name as <see cref="Normalise"/> records a match of it.</summary>
    public static IReadOnlyList<string> AllRuleNames =>
        [.. Rules.Select(r => r.Pattern), .. ObjectRules.Select(r => r.Name), .. TextRules.Select(r => r.Name)];

    /// <summary>The answer with the rules applied and every sandbox path rewritten under <see cref="FixedRoot"/>, indented
    /// with LF line ends and a final newline; <paramref name="matched"/> collects the name of every rule that applied.</summary>
    public static string Normalise(string json, string sandboxRoot, ISet<string> matched)
    {
        var root = JsonNode.Parse(json) ?? throw new InvalidOperationException("the answer is JSON null");
        var normalised = Walk(root, string.Empty, new Context(sandboxRoot, matched));
        var text = normalised.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return text.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    private sealed record Context(string SandboxRoot, ISet<string> Matched);

    private static JsonNode Walk(JsonNode node, string path, Context context) => node switch
    {
        JsonObject o => Object(o, path, context),
        JsonArray a => new JsonArray([.. a.Select(item => item is null ? null : Walk(item, path + "[*]", context))]),
        JsonValue v => Value(v, context),
        _ => node.DeepClone(),
    };

    private static JsonObject Object(JsonObject source, string path, Context context)
    {
        var result = new JsonObject();
        foreach (var (name, child) in source)
        {
            var childPath = path.Length == 0 ? name : $"{path}.{name}";
            result[name] = child is null ? null : Member(child, childPath, context);
        }

        return ObjectRules.Aggregate(result, (o, rule) => Keyed(o, rule, context.Matched));
    }

    private static JsonNode Member(JsonNode child, string path, Context context)
    {
        var rule = Rules.FirstOrDefault(r => r.Matches(path));
        if (rule is null)
        {
            return Walk(child, path, context);
        }

        context.Matched.Add(rule.Pattern);
        var replaced = rule.Replace(child);
        return ReferenceEquals(replaced, child) ? Walk(child, path, context) : replaced;
    }

    private static bool IsKeyed(JsonObject candidate, GoldenObjectRule rule) =>
        candidate[rule.KeyMember] is JsonValue key && key.TryGetValue<string>(out var value) && value == rule.KeyValue;

    private static JsonObject Keyed(JsonObject candidate, GoldenObjectRule rule, ISet<string> matched)
    {
        if (!IsKeyed(candidate, rule))
        {
            return candidate;
        }

        matched.Add(rule.Name);
        foreach (var (member, replace) in rule.Members.Where(m => candidate[m.Key] is JsonValue))
        {
            candidate[member] = replace(candidate[member]!.GetValue<string>());
        }

        return candidate;
    }

    private static JsonNode Value(JsonValue value, Context context)
    {
        if (!value.TryGetValue<string>(out var text))
        {
            return value.DeepClone();
        }

        var rewritten = TextRules.Aggregate(text, (current, rule) => Rewrite(current, rule, context.Matched));
        return Identity.Apply(rewritten.Replace(context.SandboxRoot, FixedRoot, StringComparison.Ordinal));
    }

    private static string Rewrite(string text, GoldenTextRule rule, ISet<string> matched)
    {
        if (!rule.Fragment.IsMatch(text))
        {
            return text;
        }

        matched.Add(rule.Name);
        return rule.Fragment.Replace(text, rule.Replacement);
    }

    /// <summary>The first line where two texts differ, for a failure message a person can act on.</summary>
    public static string FirstDifference(string expected, string actual)
    {
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        var line = Enumerable.Range(0, Math.Max(e.Length, a.Length)).FirstOrDefault(i => i >= e.Length || i >= a.Length || e[i] != a[i], -1);
        return line < 0
            ? "no difference"
            : $"line {line + 1}: checked in '{(line < e.Length ? e[line] : "<end>")}', the CLI answers '{(line < a.Length ? a[line] : "<end>")}'";
    }

    [GeneratedRegex(@"\b\d{8}T\d{6}Z-\d+\b")]
    private static partial Regex RunIdInText();

    [GeneratedRegex(@"^\d+(\.\d+)? days")]
    private static partial Regex LeadingDays();

    [GeneratedRegex(@"^[+-]\d+(\.\d+)? s")]
    private static partial Regex LeadingOffset();
}
