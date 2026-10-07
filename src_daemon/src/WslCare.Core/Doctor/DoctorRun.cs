using System.Globalization;

using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Config;
using WslCare.Core.Docker;
using WslCare.Core.Events;
using WslCare.Core.Files;
using WslCare.Core.Health;
using WslCare.Core.Hosting;
using WslCare.Core.Processes;
using WslCare.Core.Records;
using WslCare.Core.Status;
using WslCare.Core.Systemd;

namespace WslCare.Core.Doctor;

/// <summary>One check of <c>doctor</c>: <c>ok</c>, <c>problem</c>, <c>unknown</c> (it could not be asked) or
/// <c>notChecked</c> (not this binary's side, or a later story's), and what was found.</summary>
public sealed record DoctorCheck(string Id, string State, string Detail);

/// <summary>A component's version, or why it is unknown.</summary>
public sealed record VersionReport(string Component, bool Available, string? Version, string? Reason);

/// <summary>The answer of <c>doctor --json</c> (plan §6): <see cref="Healthy"/> when no check is a <c>problem</c>.</summary>
public sealed record DoctorReport(
    int SchemaVersion,
    string Side,
    DateTimeOffset CheckedAt,
    bool Healthy,
    bool ObserveOnly,
    IReadOnlyList<ConfigErrorReport> ConfigError,
    IReadOnlyList<DoctorCheck> Checks,
    IReadOnlyList<VersionReport> Versions)
{
    /// <summary>User values this run did not take (plan §15q); absent when none.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ConfigNoticeReport>? ConfigNotices { get; init; }
}

/// <summary>
/// <c>doctor [--json]</c> (plan §6): is this installation doing its job — the timer, the events unit, sysstat and atop
/// collecting, the configuration valid, the state directory there, the last full run recent, the follower current —
/// and which versions run. READ-ONLY: <c>systemctl show</c>, <c>systemctl --version</c>, <c>docker version</c> and
/// file reads; it writes nothing (the state directory's writability is reported from its mode by a probe only when it
/// exists, and the probe leaves nothing behind).
/// </summary>
/// <remarks>Root reachability (<c>wsl.exe -u root -- true</c>, plan §15 #5) is the extension's root boundary (E6);
/// <c>doctor</c> reports it <c>notChecked</c>.</remarks>
public sealed class DoctorRun(IHostPaths paths, IFileSystem files, ICommandRunner commands, TimeProvider clock)
{
    public const string Ok = "ok";
    public const string Problem = "problem";
    public const string Unknown = "unknown";
    public const string NotChecked = "notChecked";

    /// <summary>The timer runs every 4 h (plan §8); a last run older than this means it does not.</summary>
    public static TimeSpan LastRunMaxAge => Tuning.Current.Hours(ConfigKeys.Timer.PeriodHours) + Tuning.Current.Minutes(ConfigKeys.Timer.LateSlackMinutes);

    public static readonly IReadOnlyList<string> Units = ["wsl-care.timer", "wsl-care-events.service", "sysstat.service", "atop.service"];

    public async Task<DoctorReport> RunAsync(ConfigLoadResult loaded, string version, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var linux = paths as LinuxHostPaths;
        var checks = new List<DoctorCheck> { Config(loaded), StateDirectory(), LastRun(now), DetailsLost() };
        checks.AddRange(linux is null ? [.. Units.Select(u => new DoctorCheck($"unit.{u}", NotChecked, "a unit of the WSL distro: wsl-care inside it checks it"))] : await UnitsAsync(cancellationToken).ConfigureAwait(false));
        checks.Add(linux is null ? new("unitConfig", NotChecked, "a unit of the WSL distro: wsl-care inside it checks it") : UnitConfig(linux));
        checks.AddRange(linux is null ? [new("collector.sysstat", NotChecked, "Linux side"), new("collector.atop", NotChecked, "Linux side")] : Collectors(linux, now));
        checks.Add(Follower(now));
        checks.Add(new("root", NotChecked, "root reachability (wsl.exe -u root -- true) is checked by the extension's root boundary (E6)"));
        var versions = new List<VersionReport> { new("wsl-care", true, version, null) };
        versions.AddRange(await DockerVersionsAsync(cancellationToken).ConfigureAwait(false));
        if (linux is not null)
        {
            versions.Add(Version("systemd", (await ToolAnswers.RunAsync(commands, SystemdCommands.Version, cancellationToken).ConfigureAwait(false)).Bind(HealthParsers.SystemdVersion)));
            versions.Add(Version("kernel", ProcText.Read(files, $"{linux.ProcRoot}/sys/kernel/osrelease").Map(t => t.Trim())));
        }

        return new DoctorReport(
            Core.SchemaVersion.Current,
            paths.Side == HostSide.Wsl ? "wsl" : "windows",
            now,
            checks.All(c => c.State != Problem),
            loaded.IsObserveOnly,
            [.. loaded.Errors.Select(ConfigErrorReport.From)],
            checks,
            versions)
        {
            ConfigNotices = ConfigNoticeReport.Of(loaded),
        };
    }

    private static DoctorCheck Config(ConfigLoadResult loaded) =>
        loaded.IsObserveOnly
            ? new("config", Problem, $"observe-only: {string.Join("; ", loaded.Errors.Select(e => e.Display))}")
            : new("config", Ok, "every layer is valid");

    private DoctorCheck StateDirectory()
    {
        if (!files.DirectoryExists(paths.StateDirectory))
        {
            return new("stateDirectory", Problem, $"{paths.StateDirectory} does not exist: no full run has recorded anything (is wsl-care installed?)");
        }

        return files.ProbeWriteAccess(paths.StateDirectory) is WriteAccess.NotWritable denied
            ? new("stateDirectory", Ok, $"{paths.StateDirectory} is read-only for this process — expected unless run as root ({denied.Reason})")
            : new("stateDirectory", Ok, $"{paths.StateDirectory} is writable by this process");
    }

    private DoctorCheck LastRun(DateTimeOffset now)
    {
        var history = RunHistory.Read(paths, files);
        if (history.Problem.Length > 0)
        {
            return new("lastRun", Unknown, history.Problem);
        }

        if (history.Records.LastOrDefault(IsFullCheck) is not { } last)
        {
            return new("lastRun", Problem, "no full run has been recorded yet");
        }

        var age = now - last.StartedAt;
        var text = string.Create(CultureInfo.InvariantCulture, $"{last.RunId.Text}: {Camel(last.Outcome.ToString())}, {age.TotalHours:0.0} h ago");
        return age > LastRunMaxAge || last.Outcome == RunOutcome.Failed
            ? new("lastRun", Problem, $"{text}{(last.Reason is { Length: > 0 } r ? $" ({r})" : string.Empty)}; the timer runs every {Tuning.Current.Text(ConfigKeys.Timer.PeriodHours)} h")
            : new("lastRun", Ok, text);
    }

    /// <summary>Whether <c>lastRun</c> judges this line: a full check (<c>kind: collect</c>) — never an <c>act</c>, whose frequent
    /// lines would hide a timer that stopped, nor a kind this build does not know. A line WITHOUT a kind (written before plan
    /// §15o) keeps the rule it had: it is judged (PR #16 retro round, consultation 0d924598).</summary>
    private static bool IsFullCheck(RunRecord line) => line.Kind.IsAbsent || line.Kind == RecordedKind.Collect;

    private DoctorCheck DetailsLost()
    {
        var lost = RunHistory.Entries(paths, files).Where(e => e.Detail == DetailState.Lost).Select(e => e.Record.RunId.Text).ToList();
        return lost.Count == 0
            ? new("runDetails", Ok, "every history line that names a detail has it")
            : new("runDetails", Problem, $"detail lost for {lost.Count} run(s): {string.Join(", ", lost.TakeLast(5))}");
    }

    /// <summary>E7.S2c: does every installed unit drop-in say what the machine configuration says (<see cref="UnitDropIns"/>) —
    /// the timer's period among them. A key changed after the install takes effect only when install.sh writes the drop-ins again.</summary>
    private DoctorCheck UnitConfig(LinuxHostPaths linux)
    {
        var found = UnitDropIns.Units.Select(unit => (Unit: unit, Read: files.ReadFile(UnitDropIns.Path(linux, unit), RootFileCaps.State))).ToList();
        var unreadable = found.Where(f => f.Read is FileReadResult.Unreadable).Select(f => $"{f.Unit}: {((FileReadResult.Unreadable)f.Read).Reason}").ToList();
        if (unreadable.Count > 0)
        {
            return new("unitConfig", Unknown, string.Join("; ", unreadable));
        }

        var problems = found.Select(f => UnitDropIns.Check(f.Unit, f.Read is FileReadResult.Content c ? System.Text.Encoding.UTF8.GetString(c.Bytes) : string.Empty)).Where(p => p.Length > 0).ToList();
        return problems.Count > 0
            ? new("unitConfig", Problem, string.Join("; ", problems))
            : new("unitConfig", Ok, string.Create(CultureInfo.InvariantCulture, $"every unit drop-in matches the machine configuration (the timer every {Tuning.Current.Int(ConfigKeys.Timer.PeriodHours)} h)"));
    }

    private async Task<IReadOnlyList<DoctorCheck>> UnitsAsync(CancellationToken cancellationToken)
    {
        var checks = new List<DoctorCheck>();
        foreach (var unit in Units)
        {
            var answer = (await ToolAnswers.RunAsync(commands, SystemdCommands.ShowUnit(unit), cancellationToken).ConfigureAwait(false)).Bind(SystemdUnit.Parse);
            checks.Add(answer switch
            {
                Reading<SystemdUnit>.Available { Value.Exists: false } => new($"unit.{unit}", Problem, "not installed"),
                Reading<SystemdUnit>.Available { Value: var u } => new($"unit.{unit}", u.ActiveState == "active" ? Ok : Problem, $"{u.ActiveState} ({u.SubState}), {u.UnitFileState}"),
                _ => new($"unit.{unit}", Unknown, answer.ReasonOrEmpty),
            });
        }

        return checks;
    }

    private IEnumerable<DoctorCheck> Collectors(LinuxHostPaths linux, DateTimeOffset now)
    {
        var health = new HealthCollector(commands, files, paths, clock);
        yield return Fresh("collector.sysstat", health.Freshness(linux.SysstatDirectory, "sysstat"), now);
        yield return Fresh("collector.atop", health.Freshness(linux.AtopDirectory, "atop"), now);
    }

    private static DoctorCheck Fresh(string id, Reading<CollectorFreshness> freshness, DateTimeOffset now) => freshness switch
    {
        Reading<CollectorFreshness>.Available { Value: var f } => new(
            id,
            now - f.LastWrite <= Thresholds.ThresholdRules.CollectorFreshFor ? Ok : Problem,
            string.Create(CultureInfo.InvariantCulture, $"last sample {(now - f.LastWrite).TotalMinutes:0} min ago ({f.File})")),
        _ => new(id, Problem, freshness.ReasonOrEmpty),
    };

    private DoctorCheck Follower(DateTimeOffset now)
    {
        var covered = Coverage.LastCovered(new ContainerStartsStore(paths, files).ReadAll());
        return covered switch
        {
            null => new("eventsFollower", Problem, "the events follower has recorded nothing (wsl-care-events.service)"),
            { } at when now - at > Coverage.Staleness => new("eventsFollower", Problem, string.Create(CultureInfo.InvariantCulture, $"container starts recorded up to {at.UtcDateTime:yyyy-MM-dd HH:mm}Z only: the follower is not running, or Docker is unreachable")),
            { } at => new("eventsFollower", Ok, string.Create(CultureInfo.InvariantCulture, $"container starts recorded up to {at.UtcDateTime:yyyy-MM-dd HH:mm:ss}Z")),
        };
    }

    private async Task<IReadOnlyList<VersionReport>> DockerVersionsAsync(CancellationToken cancellationToken) =>
        DockerReachability.From(await new DockerCli(commands).RunAsync(DockerCommands.Version, cancellationToken).ConfigureAwait(false)) switch
        {
            DockerReachability.Reachable { Engine: var e } => [new("docker", true, e.ServerVersion, null), new("docker-cli", true, e.ClientVersion, null)],
            DockerReachability.Unreachable { Problem: var p } => [new("docker", false, null, $"{p.Kind}: {p.Reason}")],
            _ => throw new System.Diagnostics.UnreachableException("DockerReachability is a closed set"),
        };

    private static VersionReport Version(string component, Reading<string> version) =>
        version is Reading<string>.Available { Value: var v } ? new(component, true, v, null) : new(component, false, null, version.ReasonOrEmpty);

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];
}
