using WslCare.Core.Config;
using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Processes;
using WslCare.Core.Records;
using WslCare.Core.Systemd;

namespace WslCare.Core.Health;

/// <summary>
/// The health collectors of plan §4.5 for one full run — read-only commands through <see cref="ICommandRunner"/>
/// (each with its ceiling, killed with its tree past it) and files through <see cref="IFileSystem"/>. No
/// <c>dmesg</c>: the kernel's signals come from journald's copy of the kernel log (<c>journalctl --dmesg</c>), which
/// an unprivileged member of <c>adm</c> may read too.
/// </summary>
/// <remarks>Each part is independent: a tool that is missing or fails makes ITS part unavailable with the reason and
/// the rest still answer (plan §5: one failure never ends a run).</remarks>
public sealed class HealthCollector(ICommandRunner commands, IFileSystem files, IHostPaths paths, TimeProvider clock)
{
    public const string WindowsSide = "the WSL distro's health (systemd, the journal, the kernel's signals) is read by wsl-care inside the distro, not by wsl-care.exe";
    public const string WindowsIsTheReference = "this binary runs on Windows: its clock is the one the distro's is compared with";

    /// <summary>Why the Windows binary has no Windows disk sample (E15.S1): the full run in the distro reads it.</summary>
    public const string WindowsDiskIsTheDistros = "the Windows disk facts are read by wsl-care's full run inside the distro, through its second fixed script; wsl-care.exe does not run it";

    /// <summary>
    /// The Windows disk facts (E15.S1): the second fixed interop script, once, under its own ceiling — its tree killed past it by
    /// the runner — parsed by tag. A script that could not start, timed out, exited non-zero, was refused or printed a cut
    /// answer leaves the WHOLE sample unavailable with that reason; its run time is kept only for an answer that was read.
    /// E15.S1b: the script runs under <paramref name="engine"/> (<see cref="WindowsDiskCommands.Under"/>); the sample keeps the
    /// engine — chosen before the run, so it is kept also when the script failed — and the file the runner really started.
    /// Code round: a verified MSI pwsh that could not be STARTED (the fixed-folder lookup refused it or did not answer) falls
    /// back to Windows PowerShell 5.1 once, and the engine says why — before the fixed-folder rule 5.1 would have run.
    /// </summary>
    public static async Task<WindowsDiskSample> MeasureWindowsDiskAsync(ICommandRunner runner, TimeProvider clock, InteropEngine engine, CancellationToken cancellationToken)
    {
        var at = clock.GetUtcNow();
        var (used, outcome) = await RunDiskAsync(runner, engine, cancellationToken).ConfigureAwait(false);
        var facts = ToolAnswers.Read(WindowsDiskCommands.Under(used), outcome).Bind(stdout => WindowsDiskParsers.Parse(stdout, WindowsDiskCommands.EventsMaxRead, WindowsDiskCommands.EventsSinceDays));
        return new WindowsDiskSample(at, Elapsed(outcome, facts), facts) { Engine = Reading.Of(used), StartedFrom = outcome.StartedFrom };
    }

    /// <summary>The script under <paramref name="engine"/>; when that is the MSI pwsh and it failed to START, the fallback engine's
    /// script once instead. The command RUN is always <c>Under(the engine returned)</c> — the command whose answer is read (final
    /// code round, finding 4).</summary>
    private static async Task<(InteropEngine Used, CommandOutcome Outcome)> RunDiskAsync(ICommandRunner runner, InteropEngine engine, CancellationToken cancellationToken)
    {
        var outcome = await RunUnderAsync(runner, engine, cancellationToken).ConfigureAwait(false);
        if (outcome is not CommandOutcome.FailedToStart failed || engine.Kind != InteropEngineKind.Msi)
        {
            return (engine, outcome);
        }

        var fallback = PwshEngine.NotStartable(failed.Reason);
        return (fallback, await RunUnderAsync(runner, fallback, cancellationToken).ConfigureAwait(false));
    }

    private static Task<CommandOutcome> RunUnderAsync(ICommandRunner runner, InteropEngine engine, CancellationToken cancellationToken) =>
        runner.RunAsync(WindowsDiskCommands.Under(engine).ToRequest(), cancellationToken);

    private static Reading<TimeSpan> Elapsed<T>(CommandOutcome outcome, Reading<T> facts) =>
        outcome is CommandOutcome.Exited exited && facts.IsAvailable ? Reading.Of(exited.Elapsed) : Reading.Missing<TimeSpan>(facts.ReasonOrEmpty);

    /// <summary>Why the Windows binary has no Defender sample (E15.S10): the full run in the distro reads it.</summary>
    public const string DefenderCpuIsTheDistros = "Windows Defender's CPU is read by wsl-care's full run inside the distro, through its third fixed script; wsl-care.exe does not run it";

    /// <summary>
    /// Windows Defender's engine (E15.S10): the third fixed interop script, once, under its own ceiling — its tree killed past it by the
    /// runner — parsed by tag. A script that could not start, timed out, exited non-zero or printed no reading leaves THIS sample
    /// unavailable with that reason and touches nothing else: the disk script has already answered.
    /// </summary>
    public static async Task<DefenderSample> MeasureDefenderAsync(ICommandRunner runner, TimeProvider clock, CancellationToken cancellationToken)
    {
        var at = clock.GetUtcNow();
        var command = DefenderCpuCommands.DefenderCpu;
        var outcome = await runner.RunAsync(command.ToRequest(), cancellationToken).ConfigureAwait(false);
        var reading = ToolAnswers.Read(command, outcome).Bind(DefenderParsers.Parse);
        return new DefenderSample(at, Elapsed(outcome, reading), reading);
    }

    private const string ClockChange = "Clock change detected";
    /// <summary>The kernel's words for a failed allocation (plan §4.1) — also A2's event (E3.S3).</summary>
    public const string AllocationFailure = "page allocation failure";
    private const string OomKiller = "invoked oom-killer";
    private static int KernelLinesKept => Tuning.Current.Int(ConfigKeys.Health.KernelLinesKept);
    private static int KernelLineChars => Tuning.Current.Int(ConfigKeys.Health.KernelLineChars);

    public async Task<HealthSample> CollectAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        if (paths is not LinuxHostPaths linux)
        {
            var home = Reading.Of(paths.Home);
            var file = paths is WindowsHostPaths windows ? windows.WslConfigFile : paths.Rules.Join(paths.Home, ".wslconfig");
            return HealthSample.Unavailable(since, WindowsSide, ClockUnavailable(WindowsIsTheReference), home, Reading.Of(AuditWslConfig(file))) with
            {
                WindowsDisk = WindowsDiskSample.Unavailable(clock.GetUtcNow(), WindowsDiskIsTheDistros),
                Defender = DefenderSample.Unavailable(clock.GetUtcNow(), DefenderCpuIsTheDistros),
            };
        }

        var mark = ClockMark.Now(clock);
        var (clockSample, profile) = await WindowsClockAsync(linux, cancellationToken).ConfigureAwait(false);
        var reference = await ClockReferences.MeasureAsync(commands, clock, mark, cancellationToken).ConfigureAwait(false);
        // E15.S1: after the clock probe and its reference, so its seconds never sit between the two clocks' readings. E15.S1b: under
        // the engine the clock probe's look chose (PwshEngine), the MSI pwsh placed under this distro's automount root.
        var engine = PwshEngine.Choose(clockSample);
        var disk = await MeasureWindowsDiskAsync(commands, clock, engine, cancellationToken).ConfigureAwait(false);
        var defender = await MeasureDefenderAsync(commands, clock, cancellationToken).ConfigureAwait(false);
        var uptime = ProcText.Read(files, $"{linux.ProcRoot}/uptime").Bind(HealthParsers.Uptime);
        var jumpsBack = await TimeJumpsBackAsync(uptime, cancellationToken).ConfigureAwait(false);
        var failedUnits = (await RunAsync(SystemdCommands.FailedUnits, cancellationToken).ConfigureAwait(false)).Bind(HealthParsers.FailedUnits);
        var journalBytes = (await RunAsync(SystemdCommands.JournalDiskUsage, cancellationToken).ConfigureAwait(false)).Bind(JournalDiskUsage.Parse);
        var oldestEntry = (await RunAsync(SystemdCommands.ListBoots, cancellationToken).ConfigureAwait(false)).Bind(HealthParsers.OldestJournalEntry);
        // journal.history counts from HERE, the instant the journal answered — not from `since` (research/2026-10-12_wsl_clock_fight.md).
        var journalReadAt = clock.GetUtcNow();
        return new HealthSample(
            since,
            failedUnits,
            journalBytes,
            oldestEntry,
            (await SearchAsync(since, new JournalScope.Unit("systemd-resolved"), ClockChange, cancellationToken).ConfigureAwait(false)).Map(lines => lines.Count),
            (await SearchAsync(since, new JournalScope.Kernel(), $"{AllocationFailure}|{OomKiller}", cancellationToken).ConfigureAwait(false)).Map(Kernel),
            (await RunAsync(SystemdCommands.TimeSync, cancellationToken).ConfigureAwait(false)).Bind(HealthParsers.TimeSync),
            await UnitAsync("wsl-pro.service", cancellationToken).ConfigureAwait(false),
            await UnitAsync("fstrim.timer", cancellationToken).ConfigureAwait(false),
            ProcText.Read(files, $"{linux.ProcRoot}/mounts").Bind(HealthParsers.RootHasDiscard),
            uptime,
            Freshness(linux.SysstatDirectory, "sysstat"),
            Freshness(linux.AtopDirectory, "atop"),
            await OomDaemonAsync(cancellationToken).ConfigureAwait(false),
            clockSample,
            profile,
            profile.Map(p => AuditWslConfig(paths.Rules.Join(p, ".wslconfig"))))
        {
            ClockReference = reference,
            ClockJudgement = ClockStandings.Judge(clockSample, reference, ClockReferences.ToleranceSeconds),
            TimeJumpsBack = jumpsBack,
            WindowsDisk = disk,
            Defender = defender,
            JournalReadAt = Reading.Of(journalReadAt),
        };
    }

    /// <summary>A Windows path (<c>C:\Users\me</c>) as the distro sees it (<c>/mnt/c/Users/me</c>), under the
    /// automount root <c>/etc/wsl.conf</c> names; unavailable for a path that is not on a drive letter.</summary>
    public static Reading<string> InDistro(string windowsPath, string automountRoot)
    {
        var path = windowsPath.Trim();
        return IsPlainDrivePath(path)
            ? Reading.Of($"{automountRoot}{char.ToLowerInvariant(path[0])}{path[2..].Replace('\\', '/')}".TrimEnd('/'))
            : Reading.Missing<string>($"\"{path}\" is not a plain path on a Windows drive (a drive letter, no .. segment, no control character)");
    }

    /// <summary>A drive letter and a colon, no <c>..</c> segment that could climb out of the drive's folder (E7.S0 review S1:
    /// <c>C:\..\..\root</c> became <c>/mnt/c/../../root</c> = <c>/root</c>), no control character.</summary>
    private static bool IsPlainDrivePath(string path) =>
        path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':'
        && !path.Split('\\', '/').Contains("..", StringComparer.Ordinal) && !path.Any(char.IsControl);

    /// <summary><c>.wslconfig</c> is a short INI file.</summary>
    public static int MaxWslConfigBytes => Tuning.Current.Int(ConfigKeys.Health.MaxWslConfigBytes);

    /// <summary>What a file at <paramref name="file"/> says; an absent file is WSL's defaults, not an error.</summary>
    /// <remarks>The Windows profile, read through drvfs (plan §15q R1.1, review M2): never through a link, never waited on,
    /// capped — and no owner or mode check, because drvfs shows every file as the mount's uid, 0777.</remarks>
    public WslConfigAudit AuditWslConfig(string file) => AuditWslConfig(files, file);

    /// <summary>The same audit over any file system — what the Windows binary's <c>status</c> reads for its vmmem advice (E14 S7a)
    /// without a command runner.</summary>
    public static WslConfigAudit AuditWslConfig(IFileSystem files, string file) => files.ReadNoFollowFile(file, MaxWslConfigBytes) switch
    {
        FileReadResult.Content content => Audit(file, HealthParsers.WslConfig(System.Text.Encoding.UTF8.GetString(content.Bytes))),
        FileReadResult.Unreadable u => new WslConfigAudit(file, true, new WslConfigSettings(string.Empty, string.Empty, string.Empty, string.Empty), [$"{file} could not be read: {u.Reason}"]) { Read = false },
        _ => new WslConfigAudit(file, false, new WslConfigSettings(string.Empty, string.Empty, string.Empty, string.Empty), []),
    };

    private static WslConfigAudit Audit(string file, WslConfigSettings settings) =>
        new(file, true, settings, [.. Warnings(settings)]);

    /// <summary>Plan §3 0.1 and §4.5: the settings measured or reported to hurt on this kind of machine.</summary>
    private static IEnumerable<string> Warnings(WslConfigSettings settings)
    {
        if (settings.SparseVhd.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            yield return "sparseVhd=true: disabled since WSL 2.5.6 after corruption reports, and a sparse VHDX cannot be compacted";
        }

        if (settings.AutoMemoryReclaim.Equals("gradual", StringComparison.OrdinalIgnoreCase))
        {
            yield return "autoMemoryReclaim=gradual hangs with systemd and Docker Desktop; dropCache is the setting that works here";
        }
    }

    private static KernelSignals Kernel(IReadOnlyList<string> lines) =>
        new(
            lines.Count(l => l.Contains(AllocationFailure, StringComparison.Ordinal)),
            lines.Count(l => l.Contains(OomKiller, StringComparison.Ordinal)),
            [.. lines.Take(KernelLinesKept).Select(l => l.Length <= KernelLineChars ? l : l[..KernelLineChars] + "...")]);

    private Task<Reading<string>> RunAsync(ToolCommand command, CancellationToken cancellationToken) =>
        ToolAnswers.RunAsync(commands, command, cancellationToken);

    private async Task<Reading<IReadOnlyList<string>>> SearchAsync(DateTimeOffset since, JournalScope scope, string pattern, CancellationToken cancellationToken)
    {
        var command = SystemdCommands.Search(since, scope, pattern);
        return HealthParsers.SearchMatches(command, await commands.RunAsync(command.ToRequest(), cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The clock fight (PLAN_windows_time_guard.md D4): journald's backward jumps of this boot in the last 4 h,
    /// windowed on the monotonic clock because the lines carry the wall time AFTER the jump.</summary>
    private async Task<Reading<int>> TimeJumpsBackAsync(Reading<TimeSpan> uptime, CancellationToken cancellationToken)
    {
        var command = SystemdCommands.TimeJumpsBackThisBoot;
        var lines = HealthParsers.SearchMatches(command, await commands.RunAsync(command.ToRequest(), cancellationToken).ConfigureAwait(false));
        return Reading.Combine(lines, uptime, ClockReferences.JumpsInWindow);
    }

    private async Task<Reading<SystemdUnit>> UnitAsync(string unit, CancellationToken cancellationToken) =>
        (await RunAsync(SystemdCommands.ShowUnit(unit), cancellationToken).ConfigureAwait(false)).Bind(SystemdUnit.Parse);

    private async Task<Reading<bool>> OomDaemonAsync(CancellationToken cancellationToken)
    {
        var earlyoom = await UnitAsync("earlyoom.service", cancellationToken).ConfigureAwait(false);
        var oomd = await UnitAsync("systemd-oomd.service", cancellationToken).ConfigureAwait(false);
        return Reading.Combine(earlyoom, oomd, (e, o) => e.ActiveState == "active" || o.ActiveState == "active");
    }

    /// <summary>The newest file of a collector's folder and its last write; unavailable when the folder holds none.</summary>
    public Reading<CollectorFreshness> Freshness(string directory, string tool)
    {
        var stamped = files.ListFiles(directory)
            .SelectMany(f => files.FileSize(f) is FileSizeResult.Measured m ? [new CollectorFreshness(f, m.ModifiedAt)] : Array.Empty<CollectorFreshness>())
            .MaxBy(f => f.LastWrite);
        return stamped is null
            ? Reading.Missing<CollectorFreshness>($"{directory} holds no {tool} file: {tool} is not collecting (the installer sets it up, E4)")
            : Reading.Of(stamped);
    }

    /// <summary>
    /// The distro's clock against Windows' (plan §4.5, §15b #5). The probe prints Windows' "now" and its OWN start,
    /// both on Windows' clock; the start is what lines up with the instant this side launched it, so the offset is
    /// <c>Windows start − our launch instant</c> and the launch latency (<c>printed − started</c>, measured on one clock)
    /// is subtracted rather than counted as skew. Measured 2026-10-02: two probes a second apart read −2.89 s and
    /// −2.74 s this way; without the subtraction −2.05 s and −2.18 s.
    /// </summary>
    private async Task<(WindowsClockSample Sample, Reading<string> Profile)> WindowsClockAsync(LinuxHostPaths linux, CancellationToken cancellationToken)
    {
        var sample = await MeasureWindowsClockAsync(commands, clock, cancellationToken).ConfigureAwait(false);
        return sample.Measured
            ? (sample, WindowsProfiles.InDistro(linux, files, sample.Profile))
            : (sample, Reading.Missing<string>($"the Windows profile is unknown: {sample.Unavailable}"));
    }

    /// <summary>
    /// One observation of the distro's clock against Windows', through <paramref name="runner"/> — the full run's, and A16's
    /// live observation (E3.S3), so the offset is computed ONE way: the probe's start minus our launch instant, its launch
    /// latency subtracted. Unmeasured, with the reason, when the probe does not answer.
    /// </summary>
    /// <remarks>The launch instant is the one the runner stamped immediately before the start
    /// (<see cref="CommandOutcome.StartedAt"/>): the executable lookup runs inside the runner BEFORE it, and the Windows system
    /// drive's may take up to its ceiling — counted from an instant taken before the call, that time read as drift and A16
    /// acts on drift (PR #10 retro round, consultation b41d9220). A runner that stamps nothing (a test's scripted one) is
    /// measured from the instant before the call. In the product both instants are the system clock.</remarks>
    public static async Task<WindowsClockSample> MeasureWindowsClockAsync(ICommandRunner runner, TimeProvider clock, CancellationToken cancellationToken)
    {
        var called = clock.GetUtcNow();
        var outcome = await runner.RunAsync(HealthCommands.WindowsClock.ToRequest(), cancellationToken).ConfigureAwait(false);
        var launched = outcome.StartedAt.ValueOr(called);
        var answer = ClockAnswer(outcome);
        return answer is Reading<WindowsClockAnswer>.Available { Value: var probe }
            ? new WindowsClockSample(launched, (probe.ProcessStartedAt - launched).TotalSeconds, (probe.PrintedAt - probe.ProcessStartedAt).TotalSeconds, string.Empty)
            {
                WindowsProfile = probe.Profile,
                TimeService = probe.TimeService is Reading<WindowsTimeService>.Available { Value: var service } ? service : null,
                Pwsh = probe.Pwsh,
            }
            : new WindowsClockSample(clock.GetUtcNow(), 0, 0, answer.ReasonOrEmpty);
    }

    /// <summary>Code round: the pwsh look of a probe that ran past its ceiling, said once.</summary>
    public const string PwshLookTimedOut = "the Windows clock probe did not finish its pwsh look within its ceiling, so no pwsh was verified";

    /// <summary>
    /// The probe's answer. Code round: the probe WRITES and FLUSHES its clock lines before the pwsh look, so a probe that ran
    /// past its ceiling still answered the clock when its captured WHOLE lines parse — a partial last line (a kill in the middle of
    /// the second write) is dropped (security review, Fable extra a) — the pwsh look is then unread (<see cref="PwshLookTimedOut"/>)
    /// and Windows PowerShell 5.1 runs the disk script. Anything else is read as before.
    /// </summary>
    private static Reading<WindowsClockAnswer> ClockAnswer(CommandOutcome outcome) => outcome switch
    {
        CommandOutcome.TimedOut { Stdout.Text: var text } when HealthParsers.WindowsClock(WholeLines(text)) is Reading<WindowsClockAnswer>.Available { Value: var answer } =>
            Reading.Of(answer with { Pwsh = Reading.Missing<PwshProbe>(PwshLookTimedOut) }),
        _ => ToolAnswers.Read(HealthCommands.WindowsClock, outcome).Bind(HealthParsers.WindowsClock),
    };

    /// <summary>The text up to and including its last line feed — a partial last line dropped; empty when there is no whole line.</summary>
    private static string WholeLines(string text) => text[..(text.LastIndexOf('\n') + 1)];

    private WindowsClockSample ClockUnavailable(string reason) => new(clock.GetUtcNow(), 0, 0, reason);
}
