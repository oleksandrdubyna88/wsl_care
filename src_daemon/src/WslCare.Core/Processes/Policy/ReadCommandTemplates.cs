using WslCare.Core.Docker;
using WslCare.Core.Health;
using WslCare.Core.Systemd;

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
        [L("container"), L("inspect"), L("--format"), L(DockerCommands.InspectTemplate), new ArgPart.Repeat("id", new SlotKind.Hex(64), 1, DockerCommands.InspectBatch)],
        DockerCommands.ListingCeiling);

    public static IReadOnlyList<CommandTemplate> All { get; } =
    [
        .. new[]
        {
            DockerCommands.Version, DockerCommands.SystemDf, DockerCommands.SystemDfVerbose, DockerCommands.DanglingVolumes, DockerCommands.VolumeList,
            DockerCommands.ContainerList, DockerCommands.Stats, DockerCommands.EngineStart,
            SystemdCommands.JournalDiskUsage, SystemdCommands.ListBoots, SystemdCommands.FailedUnits, SystemdCommands.Version, SystemdCommands.TimeSync,
            HealthCommands.WindowsClock, HealthCommands.SnapList,
        }.Select(CommandTemplate.Fixed),
        ContainerInspect,
        Machine(
            "events",
            DockerCommands.Executable,
            [L("events"), L("--since"), S("since", Instant), L("--until"), S("until", Instant), L("--filter"), L("type=container"), L("--filter"), L("event=start"), L("--format"), L("{{json .}}")],
            DockerCommands.ListingCeiling),
        Machine(
            "events-backfill",
            DockerCommands.Executable,
            [L("events"), L("--since"), S("since", Instant), L("--until"), S("until", Instant), L("--format"), L("{{json .}}")],
            DockerCommands.ListingCeiling),
        Machine(
            "systemctl-show",
            SystemdCommands.Systemctl,
            [L("show"), S("unit", Unit), L("--timestamp=unix"), L($"--property={SystemdCommands.UnitProperties}")],
            SystemdCommands.Ceiling),
        Machine(
            "journalctl-search",
            SystemdCommands.Journalctl,
            [
                L("--since"), S("since", new SlotKind.UnixSeconds()), L("--no-pager"), L("--quiet"), L("--output=cat"),
                S("scope", new SlotKind.AnyOf([new SlotKind.OneOf(["--dmesg"]), new SlotKind.Prefixed("--unit=", new SlotKind.UnitName(TypeRequired: false))])),
                S("pattern", new SlotKind.Prefixed("--grep=", new SlotKind.Text(256))),
            ],
            SystemdCommands.SearchCeiling),
    ];

    private static CommandTemplate Machine(string name, string executable, IReadOnlyList<ArgPart> parts, TimeSpan ceiling) =>
        new(name, CommandScope.Machine, executable, parts, ceiling, CommandRequest.DefaultOutputCapChars);

    private static ArgPart.Literal L(string text) => new(text);

    private static ArgPart.Slot S(string name, SlotKind kind) => new(name, kind);
}
