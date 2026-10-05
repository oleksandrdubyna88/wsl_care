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

    private const string ClockChange = "Clock change detected";
    /// <summary>The kernel's words for a failed allocation (plan §4.1) — also A2's event (E3.S3).</summary>
    public const string AllocationFailure = "page allocation failure";
    private const string OomKiller = "invoked oom-killer";
    private const int KernelLinesKept = 5;
    private const int KernelLineChars = 200;

    public async Task<HealthSample> CollectAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        if (paths is not LinuxHostPaths linux)
        {
            var home = Reading.Of(paths.Home);
            var file = paths is WindowsHostPaths windows ? windows.WslConfigFile : paths.Rules.Join(paths.Home, ".wslconfig");
            return HealthSample.Unavailable(since, WindowsSide, ClockUnavailable(WindowsIsTheReference), home, Reading.Of(AuditWslConfig(file)));
        }

        var (clockSample, profile) = await WindowsClockAsync(linux, cancellationToken).ConfigureAwait(false);
        return new HealthSample(
            since,
            (await RunAsync(SystemdCommands.FailedUnits, cancellationToken).ConfigureAwait(false)).Bind(HealthParsers.FailedUnits),
            (await RunAsync(SystemdCommands.JournalDiskUsage, cancellationToken).ConfigureAwait(false)).Bind(JournalDiskUsage.Parse),
            (await RunAsync(SystemdCommands.ListBoots, cancellationToken).ConfigureAwait(false)).Bind(HealthParsers.OldestJournalEntry),
            (await SearchAsync(since, new JournalScope.Unit("systemd-resolved"), ClockChange, cancellationToken).ConfigureAwait(false)).Map(lines => lines.Count),
            (await SearchAsync(since, new JournalScope.Kernel(), $"{AllocationFailure}|{OomKiller}", cancellationToken).ConfigureAwait(false)).Map(Kernel),
            (await RunAsync(SystemdCommands.TimeSync, cancellationToken).ConfigureAwait(false)).Bind(HealthParsers.TimeSync),
            await UnitAsync("wsl-pro.service", cancellationToken).ConfigureAwait(false),
            await UnitAsync("fstrim.timer", cancellationToken).ConfigureAwait(false),
            ProcText.Read(files, $"{linux.ProcRoot}/mounts").Bind(HealthParsers.RootHasDiscard),
            ProcText.Read(files, $"{linux.ProcRoot}/uptime").Bind(HealthParsers.Uptime),
            Freshness(linux.SysstatDirectory, "sysstat"),
            Freshness(linux.AtopDirectory, "atop"),
            await OomDaemonAsync(cancellationToken).ConfigureAwait(false),
            clockSample,
            profile,
            profile.Map(p => AuditWslConfig(paths.Rules.Join(p, ".wslconfig"))));
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
    public const int MaxWslConfigBytes = 1024 * 1024;

    /// <summary>What a file at <paramref name="file"/> says; an absent file is WSL's defaults, not an error.</summary>
    /// <remarks>The Windows profile, read through drvfs (plan §15q R1.1, review M2): never through a link, never waited on,
    /// capped — and no owner or mode check, because drvfs shows every file as the mount's uid, 0777.</remarks>
    public WslConfigAudit AuditWslConfig(string file) => files.ReadNoFollowFile(file, MaxWslConfigBytes) switch
    {
        FileReadResult.Content content => Audit(file, HealthParsers.WslConfig(System.Text.Encoding.UTF8.GetString(content.Bytes))),
        FileReadResult.Unreadable u => new WslConfigAudit(file, true, new WslConfigSettings(string.Empty, string.Empty, string.Empty, string.Empty), [$"{file} could not be read: {u.Reason}"]),
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
    public static async Task<WindowsClockSample> MeasureWindowsClockAsync(ICommandRunner runner, TimeProvider clock, CancellationToken cancellationToken)
    {
        var launched = clock.GetUtcNow();
        var answer = (await ToolAnswers.RunAsync(runner, HealthCommands.WindowsClock, cancellationToken).ConfigureAwait(false)).Bind(HealthParsers.WindowsClock);
        return answer is Reading<WindowsClockAnswer>.Available { Value: var probe }
            ? new WindowsClockSample(launched, (probe.ProcessStartedAt - launched).TotalSeconds, (probe.PrintedAt - probe.ProcessStartedAt).TotalSeconds, string.Empty) { WindowsProfile = probe.Profile }
            : new WindowsClockSample(clock.GetUtcNow(), 0, 0, answer.ReasonOrEmpty);
    }

    private WindowsClockSample ClockUnavailable(string reason) => new(clock.GetUtcNow(), 0, 0, reason);
}
