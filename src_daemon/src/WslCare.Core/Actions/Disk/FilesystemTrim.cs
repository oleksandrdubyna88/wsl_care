using System.Globalization;
using System.Text.RegularExpressions;

using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Config;
using WslCare.Core.Health;
using WslCare.Core.Hosting;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;
using WslCare.Core.Systemd;

namespace WslCare.Core.Actions.Disk;

/// <summary>
/// A15 (plan §5): <c>fstrim -av</c> — every mounted filesystem that supports it hands its freed blocks back to the virtual
/// disk, so a later compaction can shrink the VHDX. Auto trigger: WEEKLY — a week or more since the last A15 that ran —
/// and only when <c>/</c> is not mounted with <c>discard</c> and <c>fstrim.timer</c> is not enabled (either one already
/// returns the blocks). Heavy: it waits for an idle machine, a button too (plan §5; <see cref="IdleRule.Always"/>).
/// </summary>
/// <remarks>
/// <para><b>"Weekly"</b> is read from the history: the newest run whose A15 line says <c>ran</c>. No state file of its own.</para>
/// <para><b>Measured</b>: what <c>fstrim -v</c> reports per filesystem (<c>/: 1.2 GiB (1288490188 bytes) trimmed on …</c>),
/// one item each. Trimmed blocks are returned to the VHDX, not freed inside the filesystem: <see cref="ActionRun.FreedBytes"/>
/// stays unknown and the total is a note. Exit 64 (some filesystems trimmed, some not) is a success with the note; any other
/// non-zero exit is a failure.</para>
/// </remarks>
public sealed partial class FilesystemTrim : ICleanupAction
{
    public const string DiscardFact = "discard";

    public const string TimerEnabledFact = "fstrimTimerEnabled";

    public const string DaysSinceTrimFact = "daysSinceLastTrim";

    /// <summary>Plan §5 A15: weekly.</summary>
    public static TimeSpan Period => Tuning.Current.Days(ConfigKeys.Trim.PeriodDays);

    public static readonly CommandTemplate Trim = new(
        "fstrim-all-verbose",
        CommandScope.Machine,
        "fstrim",
        [new ArgPart.Literal("-av")],
        ConfigKeys.Trim.TimeoutSeconds,
        ConfigKeys.Commands.OutputCapBytes);

    /// <summary>util-linux fstrim: some filesystems were trimmed, some failed.</summary>
    private const int SomeTrimmed = 64;

    private const string Kind = "filesystem";

    public ActionId Id { get; } = ActionId.Find("A15")!;

    public string Summary => "fstrim -av: return the filesystems' freed blocks to the VHDX (weekly, when / is not mounted with discard)";

    public CommandScope Scope => CommandScope.Machine;

    public IdleRule Idle => IdleRule.Always;

    public IReadOnlyList<HostSide> Sides { get; } = [HostSide.Wsl];

    public IReadOnlyList<CommandTemplate> Commands { get; } = [Trim, ReadCommandTemplates.SystemctlShow];

    public async Task<ActionPreview> PreviewAsync(ActionContext context, ActionCommands commands, CancellationToken cancellationToken)
    {
        const string what = "fstrim -av: every mounted filesystem that supports it returns its freed blocks to the VHDX";
        if (context.Paths is not LinuxHostPaths linux)
        {
            return ActionPreview.Unavailable(what, "the filesystems are the WSL distro's");
        }

        var discard = ProcText.Read(context.Files, $"{linux.ProcRoot}/mounts").Bind(HealthParsers.RootHasDiscard);
        if (discard is not Reading<bool>.Available { Value: var mounted })
        {
            return ActionPreview.Unavailable(what, discard.ReasonOrEmpty);
        }

        var timer = await TimerAsync(commands, cancellationToken).ConfigureAwait(false);
        var last = LastTrim(context);
        return ActionPreview.Of(what, 0, null, Basis(mounted, timer, last), Facts(mounted, timer, last, context.Clock.GetUtcNow()), string.Empty, []);
    }

    /// <summary>Plan §5 A15: weekly, when <c>/</c> has no discard (and, E3.S3, when <c>fstrim.timer</c> is not already doing it).</summary>
    public TriggerDecision Trigger(ActionPreview preview, EffectiveConfig config) => NotForTheTimer(preview.Facts) ?? Weekly(preview.Facts);

    public async Task<ActionRun> RunAsync(ActionContext context, ActionPreview preview, ActionCommands commands, CancellationToken cancellationToken)
    {
        var outcome = await commands.RunAsync(Trim, [], cancellationToken).ConfigureAwait(false);
        var trimmed = outcome is CommandOutcome.Exited exited ? Trimmed(exited.Stdout.Text) : [];
        var failure = outcome is CommandOutcome.Exited { ExitCode: SomeTrimmed } ? string.Empty : CommandFailures.Of("fstrim -av", outcome);
        return new ActionRun(trimmed.Count, null, "trimmed blocks return to the VHDX, nothing inside a filesystem is freed: fstrim's own per-filesystem report", null, null, trimmed, commands.Ran, failure)
        {
            Notes = TrimNotes(outcome, trimmed),
        };
    }

    private static IReadOnlyList<string> TrimNotes(CommandOutcome outcome, IReadOnlyList<ActionItem> trimmed) =>
    [
        string.Create(CultureInfo.InvariantCulture, $"{trimmed.Sum(t => t.Bytes ?? 0)} bytes trimmed on {trimmed.Count} filesystem(s), as fstrim reported"),
        .. outcome is CommandOutcome.Exited { ExitCode: SomeTrimmed } e ? [$"fstrim exited 64: some filesystems were trimmed and some were not{ToolAnswers.Said(e.Stderr.Text)}"] : Array.Empty<string>(),
    ];

    /// <summary>Why the timer leaves A15 alone: discard on <c>/</c>, an unread or enabled <c>fstrim.timer</c>; <c>null</c> when
    /// none of these holds.</summary>
    private static TriggerDecision? NotForTheTimer(IReadOnlyDictionary<string, long> facts) => facts switch
    {
        _ when facts.GetValueOrDefault(DiscardFact) == 1 => new TriggerDecision(false, "/ is mounted with discard: freed blocks already reach the VHDX"),
        _ when !facts.ContainsKey(TimerEnabledFact) => new TriggerDecision(false, "fstrim.timer's state was not read, so a duplicate trim cannot be ruled out"),
        _ when facts[TimerEnabledFact] == 1 => new TriggerDecision(false, "fstrim.timer is enabled: it trims weekly itself"),
        _ => null,
    };

    private static TriggerDecision Weekly(IReadOnlyDictionary<string, long> facts) =>
        facts.TryGetValue(DaysSinceTrimFact, out var days)
            ? new TriggerDecision(days >= Period.TotalDays, string.Create(CultureInfo.InvariantCulture, $"the last A15 ran {days} day(s) ago; the trigger is weekly"))
            : new TriggerDecision(true, "A15 has never run here; the trigger is weekly");

    private static Dictionary<string, long> Facts(bool discard, Reading<SystemdUnit> timer, DateTimeOffset? last, DateTimeOffset now)
    {
        var facts = new Dictionary<string, long>(StringComparer.Ordinal) { [DiscardFact] = discard ? 1 : 0 };
        if (timer is Reading<SystemdUnit>.Available { Value: var unit })
        {
            facts[TimerEnabledFact] = IsEnabled(unit);
        }

        if (last is { } at)
        {
            facts[DaysSinceTrimFact] = (long)Math.Floor((now - at).TotalDays);
        }

        return facts;
    }

    private static long IsEnabled(SystemdUnit unit) => unit.UnitFileState == "enabled" ? 1 : 0;

    private static string Basis(bool discard, Reading<SystemdUnit> timer, DateTimeOffset? last) =>
        $"/proc/mounts (/ {(discard ? "is" : "is not")} mounted with discard), fstrim.timer {(timer is Reading<SystemdUnit>.Available a ? a.Value.UnitFileState : $"unknown ({timer.ReasonOrEmpty})")}, last A15 {(last is { } l ? l.UtcDateTime.ToString("yyyy-MM-dd HH:mm'Z'", CultureInfo.InvariantCulture) : "never")}; what it trims is known only when it runs";

    /// <summary>Each <c>&lt;mount&gt;: … (&lt;n&gt; bytes) trimmed[ on &lt;device&gt;]</c> line of <c>fstrim -v</c>, as an item.</summary>
    public static IReadOnlyList<ActionItem> Trimmed(string stdout) =>
        [.. ProcText.Lines(stdout).Select(l => TrimmedLine().Match(l)).Where(m => m.Success)
            .Select(m => new ActionItem(Kind, m.Groups["mount"].Value, long.Parse(m.Groups["bytes"].Value, CultureInfo.InvariantCulture), m.Groups["device"].Success ? $"trimmed on {m.Groups["device"].Value}" : "trimmed"))];

    /// <summary>The newest run in the history whose A15 line says it ran; <c>null</c> when none did.</summary>
    private static DateTimeOffset? LastTrim(ActionContext context) =>
        RunHistory.Read(context.Paths, context.Files).Records
            .Where(r => r.Actions.Any(a => a.Id == "A15" && a.Status == Engine.ActionStatus.Ran))
            .Select(r => (DateTimeOffset?)r.StartedAt)
            .Max();

    private static async Task<Reading<SystemdUnit>> TimerAsync(ActionCommands commands, CancellationToken cancellationToken) =>
        (await ToolAnswers.RunAsync(commands.AsRunner(), SystemdCommands.ShowUnit("fstrim.timer"), cancellationToken).ConfigureAwait(false)).Bind(SystemdUnit.Parse);

    [GeneratedRegex(@"^(?<mount>/.*?):.*\((?<bytes>\d{1,19}) bytes\) trimmed(?: on (?<device>\S+))?$", RegexOptions.CultureInvariant)]
    private static partial Regex TrimmedLine();
}
