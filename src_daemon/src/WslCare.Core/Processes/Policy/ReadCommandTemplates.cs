using WslCare.Core.Config;
using WslCare.Core.Docker;
using WslCare.Core.Health;
using WslCare.Core.Systemd;
using WslCare.Core.Worktrees;

namespace WslCare.Core.Processes.Policy;

/// <summary>
/// The templates of every READ command the collectors build (E2: <see cref="DockerCommands"/>,
/// <see cref="SystemdCommands"/>, <see cref="HealthCommands"/>). A fixed command is taken from its own
/// <see cref="ToolCommand"/> (no retyping); a parameterised one states its shape with the same constants the factory
/// uses, and <c>ReadCommandTemplatesTests</c> holds every factory's output to these shapes.
/// </summary>
public static class ReadCommandTemplates
{
    private static readonly SlotKind.Rfc3339Utc Instant = new();
    private static readonly SlotKind.UnitName Unit = new();

    /// <summary><c>docker container inspect</c> through the product's field template, for 1 to 100 full ids — the one
    /// parameterised read the Docker collector runs; the Docker actions of E3.S2 declare this same instance.</summary>
    public static CommandTemplate ContainerInspect { get; } = Machine(
        "container-inspect",
        DockerCommands.Executable,
        [L("container"), L("inspect"), L("--format"), L(DockerCommands.InspectTemplate), new ArgPart.Repeat("id", new SlotKind.Hex(64), 1, ConfigKeys.Docker.BatchSize.Max)],
        ConfigKeys.Docker.ListTimeoutSeconds);

    /// <summary><c>systemctl show &lt;unit&gt;</c> (<see cref="SystemdCommands.ShowUnit"/>) — also declared by A15 (E3.S3), which
    /// reads <c>fstrim.timer</c> through it. Declared BEFORE <see cref="All"/>: static members initialise in textual order.</summary>
    public static CommandTemplate SystemctlShow { get; } = Machine(
        "systemctl-show",
        SystemdCommands.Systemctl,
        [L("show"), S("unit", Unit), L("--timestamp=unix"), L($"--property={SystemdCommands.UnitProperties}")],
        ConfigKeys.Systemd.TimeoutSeconds);

    /// <summary><c>journalctl --since … --grep=…</c> (<see cref="SystemdCommands.Search"/>) — also declared by A2 (E3.S3), which
    /// counts the kernel's page allocation failures since the last run through it.</summary>
    public static CommandTemplate JournalSearch { get; } = Machine(
        "journalctl-search",
        SystemdCommands.Journalctl,
        [
            L("--since"), S("since", new SlotKind.UnixSeconds()), L("--no-pager"), L("--quiet"), L("--output=cat"),
            S("scope", new SlotKind.AnyOf([new SlotKind.OneOf(["--dmesg"]), new SlotKind.Prefixed("--unit=", new SlotKind.UnitName(TypeRequired: false))])),
            S("pattern", new SlotKind.Prefixed("--grep=", new SlotKind.Text(256))),
        ],
        ConfigKeys.Systemd.SearchTimeoutSeconds);

    /// <summary>
    /// The clock reference's HEAD (<see cref="HealthCommands.ClockReference"/>, PLAN_windows_time_guard.md D2) — PARAMETERISED,
    /// not <see cref="CommandTemplate.Fixed(Func{ToolCommand})"/>: that freezes the argv at its first call, and the address
    /// and the timeout are configuration. The address slot holds the key's own rule (<see cref="HttpsUrls"/>); the
    /// timeout slot the key's range. Declared BEFORE <see cref="All"/>, and by A16.
    /// </summary>
    public static CommandTemplate ClockReference { get; } = Machine(
        "curl-head-date",
        HealthCommands.Curl,
        [
            L("--disable"), L("--silent"), L("--show-error"), L("--head"), L("--max-redirs"), L("0"), L("--proto"), L("=https"), L("--proto-redir"), L("=https"),
            L("--max-time"), S("seconds", new SlotKind.Number(ConfigKeys.Clock.ReferenceTimeoutSeconds.Min, ConfigKeys.Clock.ReferenceTimeoutSeconds.Max)),
            L("--header"), L("Cache-Control: no-cache"), L("--url"), S("url", new SlotKind.HttpsUrl()),
        ],
        ConfigKeys.Clock.ReferenceTimeoutSeconds);

    /// <summary><c>timedatectl timesync-status</c> — also declared by A16.</summary>
    public static CommandTemplate TimesyncStatus { get; } = CommandTemplate.Fixed(() => SystemdCommands.TimesyncStatus);

    /// <summary>journald's backward jumps of this boot, monotonic stamps (<see cref="SystemdCommands.TimeJumpsBackThisBoot"/>).</summary>
    public static CommandTemplate TimeJumpsBack { get; } = CommandTemplate.Fixed(() => SystemdCommands.TimeJumpsBackThisBoot);

    /// <summary>
    /// The Windows disk script (E15.S1, <see cref="WindowsDiskCommands.WindowsDisk"/>): its three fixed words and ONE slot that
    /// accepts exactly the script the configuration in force builds (<see cref="SlotKind.WindowsDiskScript"/>) — not
    /// <see cref="CommandTemplate.Fixed(Func{ToolCommand})"/>, which freezes the argv at its first call while two of the script's
    /// integers are configuration. Its ceiling is <c>health.windowsDiskTimeoutSeconds</c>, its cap the health probes'. The
    /// never-list's closed set says the same thing first (<see cref="HealthCommands.FixedScripts"/>). Declared BEFORE <see cref="All"/>.
    /// </summary>
    public static CommandTemplate WindowsDisk { get; } = DiskScript(WindowsDiskCommands.Name, HealthCommands.PowerShell);

    /// <summary>The SAME script under the bare <c>pwsh.exe</c>, the MSI PowerShell 7 (E15.S1b,
    /// <see cref="WindowsDiskCommands.WindowsDiskUnderPwsh"/>) — the same words, the same one-value slot, the same ceiling and
    /// cap. The clock probe has no pwsh template. Declared BEFORE <see cref="All"/>.</summary>
    public static CommandTemplate WindowsDiskUnderPwsh { get; } = DiskScript(WindowsDiskCommands.PwshName, HealthCommands.Pwsh);

    /// <summary><c>git.exe rev-parse --show-toplevel</c> (E19.S4): which working tree git believes a folder is in. The bare
    /// <c>git.exe</c> is judged; the file started is <see cref="Processes.GitExecutable"/>'s, never one found on <c>PATH</c>.
    /// Declared BEFORE <see cref="All"/>.</summary>
    public static CommandTemplate GitTopLevel { get; } = Machine(
        "git-toplevel",
        GitCommands.Executable,
        [.. GitOptions, L("rev-parse"), L("--show-toplevel")],
        ConfigKeys.Worktrees.GitTimeoutSeconds);

    /// <summary><c>git.exe ls-files -z --cached -- :(literal,icase)&lt;name&gt;…</c>: the tracked files under 1 to
    /// <c>worktrees.gitNamesPerCall</c> relative names, each a literal pathspec matched without regard to case (<see cref="GitCommands.TrackedNameMagic"/>:
    /// git's pathspec match ignores <c>core.ignorecase</c>, its ignore match does not). The magic is a template literal in front of the name slot, so the
    /// slot itself is the one every git template shares.</summary>
    public static CommandTemplate GitLsFiles { get; } = Machine(
        "git-ls-files",
        GitCommands.Executable,
        [.. GitOptions, L("ls-files"), L("-z"), L("--cached"), L("--"), new ArgPart.Repeat("name", new SlotKind.Prefixed(GitCommands.TrackedNameMagic, new SlotKind.RelativeName()), 1, ConfigKeys.Worktrees.GitNamesPerCall.Max)],
        ConfigKeys.Worktrees.GitTimeoutSeconds);

    /// <summary><c>git.exe check-ignore -v -n -- &lt;names&gt;</c>: the rule that decides each name (one line per name).</summary>
    public static CommandTemplate GitCheckIgnore { get; } = Machine(
        "git-check-ignore",
        GitCommands.Executable,
        [.. GitOptions, L("check-ignore"), L("-v"), L("-n"), L("--"), GitNames],
        ConfigKeys.Worktrees.GitTimeoutSeconds);

    /// <summary>The orphan form: <c>--git-dir=&lt;admin folder&gt; --work-tree=. check-ignore --no-index -v -n -- &lt;names&gt;</c> from the
    /// orphan's own folder. The <c>.git</c> FILE the classifier read lies under a configured root; the admin folder it names, and so the repository
    /// derived from it, may be anywhere (the caller proves it is that value); the slot holds only its shape. Git reads configuration and
    /// <c>info/exclude</c> there and nothing else that matters: <c>--no-index</c> reads no index, so tracked files are NOT known.</summary>
    public static CommandTemplate GitCheckIgnoreOrphan { get; } = Machine(
        "git-check-ignore-orphan",
        GitCommands.Executable,
        [.. GitOptions, S("adminFolder", new SlotKind.Prefixed(GitCommands.GitDirPrefix, new SlotKind.AdminFolder())), L("--work-tree=."), L("check-ignore"), L("--no-index"), L("-v"), L("-n"), L("--"), GitNames],
        ConfigKeys.Worktrees.GitTimeoutSeconds);

    private static IEnumerable<ArgPart> GitOptions => GitCommands.Options.Select(L);

    private static ArgPart GitNames => new ArgPart.Repeat("name", new SlotKind.RelativeName(), 1, ConfigKeys.Worktrees.GitNamesPerCall.Max);

    public static IReadOnlyList<CommandTemplate> All { get; } =
    [
        .. new Func<ToolCommand>[]
        {
            () => DockerCommands.Version, () => DockerCommands.SystemDf, () => DockerCommands.SystemDfVerbose, () => DockerCommands.DanglingVolumes,
            () => DockerCommands.VolumeList, () => DockerCommands.ContainerList, () => DockerCommands.Stats, () => DockerCommands.EngineStart,
            () => SystemdCommands.JournalDiskUsage, () => SystemdCommands.ListBoots, () => SystemdCommands.FailedUnits, () => SystemdCommands.Version,
            () => SystemdCommands.TimeSync, () => HealthCommands.WindowsClock, () => HealthCommands.SnapList,
        }.Select(CommandTemplate.Fixed),
        ClockReference,
        TimesyncStatus,
        TimeJumpsBack,
        WindowsDisk,
        WindowsDiskUnderPwsh,
        ContainerInspect,
        Machine(
            "events",
            DockerCommands.Executable,
            [L("events"), L("--since"), S("since", Instant), L("--until"), S("until", Instant), L("--filter"), L("type=container"), L("--filter"), L("event=start"), L("--format"), L("{{json .}}")],
            ConfigKeys.Docker.ListTimeoutSeconds),
        Machine(
            "events-backfill",
            DockerCommands.Executable,
            [L("events"), L("--since"), S("since", Instant), L("--until"), S("until", Instant), L("--format"), L("{{json .}}")],
            ConfigKeys.Docker.ListTimeoutSeconds),
        SystemctlShow,
        JournalSearch,
        GitTopLevel,
        GitLsFiles,
        GitCheckIgnore,
        GitCheckIgnoreOrphan,
    ];

    private static CommandTemplate Machine(string name, string executable, IReadOnlyList<ArgPart> parts, ConfigKey.IntKey timeoutSeconds) =>
        new(name, CommandScope.Machine, executable, parts, timeoutSeconds, ConfigKeys.Commands.OutputCapBytes);

    /// <summary>The disk script's template under <paramref name="executable"/>: three fixed words and the one-value script slot.</summary>
    private static CommandTemplate DiskScript(string name, string executable) => new(
        name,
        CommandScope.Machine,
        executable,
        [L("-NoProfile"), L("-NonInteractive"), L("-Command"), S("script", new SlotKind.WindowsDiskScript())],
        ConfigKeys.Health.WindowsDiskTimeoutSeconds,
        ConfigKeys.Health.OutputCapBytes);

    private static ArgPart.Literal L(string text) => new(text);

    private static ArgPart.Slot S(string name, SlotKind kind) => new(name, kind);
}
