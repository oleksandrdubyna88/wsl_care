using System.Globalization;
using System.Text.Json;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Health;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;
using WslCare.Core.Systemd;
using WslCare.Core.Thresholds;

namespace WslCare.Core.Actions.Clock;

/// <summary><c>{state}/clock-fix.json</c>: the last correction A16 made — when, the offset it corrected, and the tool.</summary>
public sealed record ClockFixRecord(int SchemaVersion, DateTimeOffset CorrectedAt, double OffsetSeconds, string Tool);

/// <summary>
/// A16 (plan §5, §4.5, §15 #10): the clock fix — <c>chronyc makestep</c> when <c>chronyd</c> runs, otherwise
/// <c>hwclock -s</c> (the system clock set from the RTC, which inside WSL is the Hyper-V host's clock) — ONCE per detected
/// drift, never from cron. A drift is the ONE rule the health report uses (<see cref="ThresholdRules.IsDrift"/>): the
/// distro's clock more than <c>clock.maxDriftSeconds</c> off Windows' on two observations at least 5 minutes apart — the
/// last full run's and a LIVE one taken by the preview.
/// </summary>
/// <remarks>
/// <para><b>Gates, in order.</b> A clock timesyncd/chrony reports synchronised is a SKIP (§15 #10), and so is a live
/// observation within the limit (nothing to fix). A correction less than an hour ago REFUSES, a button too (§15 #10: at most
/// one per hour). The TIMER fires only on a drift (two observations) that has not been corrected yet: a correction is
/// recorded in <c>clock-fix.json</c>, and the drift event it corrected lasts until a full run records an observation within
/// the limit after it — so a drift the correction did not cure is NOT corrected again every run (E3.S3: "once per drift
/// event"). A button corrects on its one live observation.</para>
/// <para><b>Measured</b>: the offset is observed again after the command (one more probe) and both are notes. Nothing is
/// freed.</para>
/// </remarks>
public sealed class ClockFix : ICleanupAction
{
    public const string FileName = "clock-fix.json";

    public const string OffsetMillisFact = "offsetMillis";

    public const string DriftFact = "drift";

    public const string AlreadyCorrectedFact = "alreadyCorrected";

    public const string ChronyFact = "chrony";

    /// <summary>Plan §15 #10: at most one correction per <c>clock.minimumGapMinutes</c> (an hour by default).</summary>
    public static TimeSpan MinimumGap => Tuning.Current.Minutes(ConfigKeys.Clock.MinimumGapMinutes);

    public static readonly CommandTemplate WindowsClock = CommandTemplate.Fixed(() => HealthCommands.WindowsClock);

    public static readonly CommandTemplate TimeSync = CommandTemplate.Fixed(() => SystemdCommands.TimeSync);

    public static readonly CommandTemplate Hwclock = new("hwclock-hctosys", CommandScope.Machine, "hwclock", [new ArgPart.Literal("-s")], ConfigKeys.Clock.StepTimeoutSeconds, ConfigKeys.Commands.OutputCapBytes);

    public static readonly CommandTemplate ChronyMakestep = new("chronyc-makestep", CommandScope.Machine, "chronyc", [new ArgPart.Literal("makestep")], ConfigKeys.Clock.StepTimeoutSeconds, ConfigKeys.Commands.OutputCapBytes);

    public ActionId Id { get; } = ActionId.Find("A16")!;

    public string Summary => "the clock fix: chronyc makestep (chronyd running) or hwclock -s, once per detected drift, at most once an hour";

    public CommandScope Scope => CommandScope.Machine;

    public IdleRule Idle => IdleRule.Never;

    public IReadOnlyList<HostSide> Sides { get; } = [HostSide.Wsl];

    public IReadOnlyList<CommandTemplate> Commands { get; } =
        [WindowsClock, TimeSync, Hwclock, ChronyMakestep, ReadCommandTemplates.ClockReference, ReadCommandTemplates.TimesyncStatus];

    public static string File(IHostPaths paths) => paths.Rules.Join(paths.StateDirectory, FileName);

    public async Task<ActionPreview> PreviewAsync(ActionContext context, ActionCommands commands, CancellationToken cancellationToken)
    {
        var max = context.Config.Int(ConfigKeys.Clock.MaxDriftSeconds);
        var chrony = ChronydRuns(context, cancellationToken);
        var what = string.Create(CultureInfo.InvariantCulture, $"{Tool(chrony).Name}: step the distro's clock to the host's, once per drift of more than {max} s (clock.maxDriftSeconds)");
        var mark = ClockMark.Now(context.Clock);
        var now = await HealthCollector.MeasureWindowsClockAsync(commands.AsRunner(), context.Clock, cancellationToken).ConfigureAwait(false);
        if (!now.Measured)
        {
            return ActionPreview.Unavailable(what, $"the Windows clock could not be observed: {now.Unavailable}");
        }

        var judgement = await JudgeAsync(context, commands, mark, now, max, cancellationToken).ConfigureAwait(false);

        var previous = LastFullRun.Read(context.Paths, context.Files, context.Clock).WindowsClock.Map(a => a.Value);
        var last = Read(context.Paths, context.Files);
        var facts = Facts(now, ThresholdRules.IsDrift(now, previous, max), Corrected(context, last, max), chrony);
        var item = new ActionItem("clock", "the distro's clock", null, string.Create(CultureInfo.InvariantCulture, $"{now.OffsetSeconds:+0.00;-0.00} s off Windows' (launch latency {now.LaunchLatencySeconds:0.00} s subtracted)"));
        var preview = ActionPreview.Of(what, 1, null, "a live observation of the Windows clock, and the last full run's", facts, Refusal(context, last), [item]);
        return await SkipAsync(preview, now, judgement, max, commands, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Which clock is wrong — asked only when the clocks disagree beyond <c>clock.maxDriftSeconds</c>: a preview whose
    /// clocks agree sends no request to the network (code round, coai #12).</summary>
    private static async Task<ClockJudgement> JudgeAsync(ActionContext context, ActionCommands commands, ClockMark mark, WindowsClockSample now, int max, CancellationToken cancellationToken)
    {
        if (Math.Abs(now.OffsetSeconds) <= max)
        {
            return ClockStandings.Unmeasured("the clocks agree: no reference was asked");
        }

        var reference = await ClockReferences.MeasureAsync(commands.AsRunner(), context.Clock, mark, cancellationToken).ConfigureAwait(false);
        return ClockStandings.Judge(now, reference, context.Config.Int(ConfigKeys.Clock.ReferenceToleranceSeconds));
    }

    /// <summary>The timer: a drift on two observations that has not been corrected yet.</summary>
    public TriggerDecision Trigger(ActionPreview preview, EffectiveConfig config)
    {
        var max = config.Int(ConfigKeys.Clock.MaxDriftSeconds);
        if (preview.Facts.GetValueOrDefault(DriftFact) != 1)
        {
            return new TriggerDecision(false, string.Create(CultureInfo.InvariantCulture, $"no drift on two observations at least {ThresholdRules.ApartText} apart (more than {max} s each); one observation is not enough (plan 15 #10)"));
        }

        return preview.Facts.GetValueOrDefault(AlreadyCorrectedFact) == 1
            ? new TriggerDecision(false, "this drift was already corrected once and no full run has seen the clock agree since; it is not corrected again (once per drift)")
            : new TriggerDecision(true, string.Create(CultureInfo.InvariantCulture, $"drift of {preview.Facts.GetValueOrDefault(OffsetMillisFact) / 1000.0:+0.00;-0.00} s on two observations at least {ThresholdRules.ApartText} apart"));
    }

    public async Task<ActionRun> RunAsync(ActionContext context, ActionPreview preview, ActionCommands commands, CancellationToken cancellationToken)
    {
        var (template, tool) = Tool(preview.Facts.GetValueOrDefault(ChronyFact) == 1);
        var failure = CommandFailures.Of(tool, await commands.RunAsync(template, [], cancellationToken).ConfigureAwait(false));
        if (failure.Length > 0)
        {
            return new ActionRun(0, null, "the clock was not stepped", null, null, [], commands.Ran, failure);
        }

        var before = OffsetBefore(preview);
        var recorded = Write(context, before, tool);
        var after = await HealthCollector.MeasureWindowsClockAsync(commands.AsRunner(), context.Clock, cancellationToken).ConfigureAwait(false);
        return new ActionRun(1, null, "a clock step frees nothing: the offset observed before and after", null, null, [new ActionItem("clock", "the distro's clock", null, $"stepped by {tool}")], commands.Ran, string.Empty)
        {
            Notes = [Observed(before, after, tool), .. Unrecorded(recorded)],
        };
    }

    /// <summary>The step's command: <c>chronyc makestep</c> while <c>chronyd</c> runs, else <c>hwclock -s</c>.</summary>
    private static (CommandTemplate Template, string Name) Tool(bool chrony) => chrony ? (ChronyMakestep, "chronyc makestep") : (Hwclock, "hwclock -s");

    private static double OffsetBefore(ActionPreview preview) =>
        preview.Facts.TryGetValue(OffsetMillisFact, out var millis) ? millis / 1000.0 : double.NaN;

    private static string Observed(double before, WindowsClockSample after, string tool) =>
        after.Measured
            ? string.Create(CultureInfo.InvariantCulture, $"offset {before:+0.00;-0.00} s before, {after.OffsetSeconds:+0.00;-0.00} s after {tool}")
            : $"the offset after {tool} was not observed: {after.Unavailable}";

    private static IEnumerable<string> Unrecorded(string recorded) =>
        recorded.Length > 0 ? [$"the correction could not be recorded in {FileName}: {recorded} - the next run may correct again"] : [];

    private static Dictionary<string, long> Facts(WindowsClockSample now, bool drift, bool corrected, bool chrony) =>
        new(StringComparer.Ordinal)
        {
            [OffsetMillisFact] = (long)Math.Round(now.OffsetSeconds * 1000),
            [DriftFact] = drift ? 1 : 0,
            [AlreadyCorrectedFact] = corrected ? 1 : 0,
            [ChronyFact] = chrony ? 1 : 0,
        };

    /// <summary>The drift <paramref name="last"/> corrected is still open (once per drift).</summary>
    private static bool Corrected(ActionContext context, ClockFixRecord? last, int max) => last is { } fix && EventStillOpen(context, fix, max);

    /// <summary>The last correction, or <c>null</c> when none is recorded (or the record cannot be read: then the per-hour
    /// and once-per-drift guards fall back to the history-free answer — the record is root's and rewritten at each fix).</summary>
    public static ClockFixRecord? Read(IHostPaths paths, IFileSystem files) =>
        files.ReadFile(File(paths), RootFileCaps.State) is FileReadResult.Content content ? Parse(content.Bytes) : null;

    private static ClockFixRecord? Parse(byte[] json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, WslCareJsonContext.Default.ClockFixRecord) is { CorrectedAt: var at } record && at != default ? record : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The drift event <paramref name="fix"/> corrected is still open: no full run since has recorded an observation
    /// within the limit.</summary>
    private static bool EventStillOpen(ActionContext context, ClockFixRecord fix, int max) =>
        !RunHistory.Read(context.Paths, context.Files).Records
            .Select(r => r.Slow?.WindowsClock)
            .Any(c => c is { Measured: true } observed && observed.SampledAt > fix.CorrectedAt && Math.Abs(observed.OffsetSeconds) <= max);

    private static string Refusal(ActionContext context, ClockFixRecord? last) =>
        last is { } fix && context.Clock.GetUtcNow() - fix.CorrectedAt < MinimumGap
            ? string.Create(CultureInfo.InvariantCulture, $"the clock was corrected at {fix.CorrectedAt.UtcDateTime:yyyy-MM-dd HH:mm:ss}Z by {fix.Tool}, less than {MinimumGap.TotalMinutes:0} minutes ago: at most one correction per {MinimumGap.TotalMinutes:0} minutes (plan 15 #10)")
            : string.Empty;

    /// <summary>
    /// The skips, in order (PLAN_windows_time_guard.md D6): timesyncd keeps the distro on NTP (and, when Windows is off, the
    /// Windows clock is the wrong one); the clock agrees with Windows' (nothing to fix); then the step goes toward the host
    /// only when an independent reference shows it brings the distro CLOSER to true time — a wrong Windows clock, no reference
    /// at all, or a step that would not help are each a skip. The reference is a brake, never a trigger.
    /// </summary>
    private static async Task<ActionPreview> SkipAsync(ActionPreview preview, WindowsClockSample now, ClockJudgement judgement, int max, ActionCommands commands, CancellationToken cancellationToken)
    {
        var sync = (await ToolAnswers.RunAsync(commands.AsRunner(), SystemdCommands.TimeSync, cancellationToken).ConfigureAwait(false)).Bind(HealthParsers.TimeSync);
        var skip = SkipReason(now, judgement, max, sync is Reading<Health.TimeSync>.Available { Value.Synchronized: true });
        return skip.Length > 0 ? preview with { Skip = skip } : preview;
    }

    /// <summary>Why A16 does not step now; empty when it may.</summary>
    public static string SkipReason(WindowsClockSample now, ClockJudgement judgement, int max, bool synchronized) => (synchronized, Math.Abs(now.OffsetSeconds) <= max) switch
    {
        (true, true) => "timesyncd/chrony reports the clock synchronised: it is not stepped (plan 15 #10)",
        (true, false) => SynchronisedSkip(now, judgement),
        (false, true) => Invariant($"the clock agrees with Windows' ({now.OffsetSeconds:+0.00;-0.00} s, the limit is {max} s)"),
        _ => ReferenceSkip(judgement, max),
    };

    /// <summary>timesyncd keeps the distro on NTP, so it is not stepped (plan §15 #10) — and the sentence follows the reference:
    /// it says "not WSL's" only when the reference does not say the distro is off too (code round coai #7, own review #5).</summary>
    private static string SynchronisedSkip(WindowsClockSample now, ClockJudgement judgement) =>
        judgement.Standing == ClockStanding.WslWrong || judgement.DistroAlsoOff
            ? $"timesyncd/chrony reports the distro's clock synchronised, so it is not stepped (plan 15 #10) — yet {judgement.Reason}"
            : Invariant($"the Windows clock is wrong, not WSL's: timesyncd/chrony reports the distro's clock synchronised to NTP and Windows is {now.OffsetSeconds:+0.00;-0.00} s off it{Unconfirmed(judgement)} — {ClockStandings.WindowsFix}");

    /// <summary>Final code round (coai): with no reference answering, the diagnosis rests on timesyncd alone — and says so.</summary>
    private static string Unconfirmed(ClockJudgement judgement) =>
        judgement.Standing == ClockStanding.Unknown ? $" (no other reference could confirm it: {judgement.Reason})" : string.Empty;

    private static string ReferenceSkip(ClockJudgement judgement, int max) => judgement switch
    {
        { WindowsIsWrong: true } => $"{judgement.Reason}; stepping the distro to the host's clock would set it wrong",
        { Standing: ClockStanding.Unknown } => $"the clocks disagree and no independent reference can say which is wrong ({judgement.Reason}); A16 steps only when a reference shows the distro is the wrong one",
        _ when !StepHelps(judgement, max) => Invariant($"the distro is {judgement.WslMinusReferenceSeconds:+0.00;-0.00} s off {judgement.Source} and Windows {judgement.WindowsMinusReferenceSeconds:+0.00;-0.00} s: stepping to Windows' clock would not bring the distro closer"),
        _ => string.Empty,
    };

    /// <summary>After a step the distro stands where Windows does (W − R off the reference); before it, −R. The step helps
    /// only when that is closer AND the distro is off by more than <c>clock.maxDriftSeconds</c>.</summary>
    private static bool StepHelps(ClockJudgement judgement, int max) =>
        Math.Abs(judgement.WindowsMinusReferenceSeconds) < Math.Abs(judgement.WslMinusReferenceSeconds) && Math.Abs(judgement.WslMinusReferenceSeconds) > max;

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static bool ChronydRuns(ActionContext context, CancellationToken cancellationToken) =>
        context.Processes(cancellationToken) is Reading<ProcessSnapshot>.Available { Value: var snapshot } && snapshot.All.Any(p => p.Name == "chronyd");

    /// <summary>Empty when the correction was recorded; otherwise why not.</summary>
    private static string Write(ActionContext context, double offset, string tool)
    {
        try
        {
            var record = new ClockFixRecord(Core.SchemaVersion.Current, context.Clock.GetUtcNow(), double.IsNaN(offset) ? 0 : offset, tool);
            context.Files.CreateDirectory(context.Paths.StateDirectory);
            var json = JsonSerializer.SerializeToUtf8Bytes(record, WslCareJsonContext.Default.ClockFixRecord);
            return context.Files.WriteFileAtomically(File(context.Paths), json, new DeletionScope(context.Paths.StateDirectory, "clock-fix")) is DeletionVerdict.Refused refused ? refused.Reason : string.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }
}
