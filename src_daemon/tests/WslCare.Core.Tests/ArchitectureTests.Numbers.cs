using System.Reflection;
using System.Text.RegularExpressions;

using FluentAssertions;

namespace WslCare.Core.Tests;

/// <summary>
/// E7.S2c, the owner's rule (2026-10-05): "every number we have must be configurable" — a behavioural number is a configuration
/// key, never a literal. This scan finds the shapes a behavioural number takes in the product source — a <c>TimeSpan</c> built
/// from a literal, a numeric <c>const</c> / <c>static readonly</c>, a <c>Take(n)</c> of a list a person reads, a byte size
/// spelt as a product of literals — and fails on every one that is not in <see cref="Formats"/>: the group-C list of formats,
/// contracts and units, each with its reason. A new number is a key (default in <c>default.json</c>, range and safe direction
/// in <c>contracts/config-keys.json</c>), or a reasoned entry here.
/// </summary>
public sealed partial class NumbersArchitectureTests
{
    /// <summary>Group C: not configurable — a format, a contract, a unit — keyed "file: symbol or literal", each with why.</summary>
    private static readonly IReadOnlyDictionary<string, string> Formats = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["WslCare.Cli/Commands/ActCommand.cs: BytesPerGigabyte"] = "a unit: bytes in a GiB / GB / MiB, never a choice",
        ["WslCare.Cli/Commands/LogsCommand.cs: BytesPerGigabyte"] = "a unit: bytes in a GiB / GB / MiB, never a choice",
        ["WslCare.Cli/Commands/PreviewCommand.cs: BytesPerGigabyte"] = "a unit: bytes in a GiB / GB / MiB, never a choice",
        ["WslCare.Cli/Commands/StatusCommand.cs: BytesPerGibibyte"] = "a unit: bytes in a GiB / GB / MiB, never a choice",
        ["WslCare.Cli/Commands/LogsCommand.cs: Take(20)"] = "a display truncation: how many entries one sentence quotes, not how much is done",
        ["WslCare.Cli/CommandLine.cs: MaxShownVolumes"] = "the parser's COMPILE-TIME ceiling: act.maxShownNames' range maximum (a test holds them equal); the verb holds the value in force (coai E7 code round #4)",
        ["WslCare.Cli/Commands/StatusCommand.cs: TopShown"] = "a display truncation: how many entries one sentence quotes, not how much is done",
        ["WslCare.Core/Actions/BuildServers/BuildServerShutdown.cs: Take(3)"] = "a display truncation: how many entries one sentence quotes, not how much is done",
        ["WslCare.Core/Actions/Disk/FilesystemTrim.cs: SomeTrimmed"] = "util-linux fstrim's exit code 64 — the tool's contract",
        ["WslCare.Core/Actions/DockerCleanups/BuildCachePrune.cs: Gib"] = "a unit: bytes in a GiB / GB / MiB, never a choice",
        ["WslCare.Core/Actions/DockerCleanups/ImagePrune.cs: Gib"] = "a unit: bytes in a GiB / GB / MiB, never a choice",
        ["WslCare.Core/Actions/DockerCleanups/VolumeRemoval.cs: Gib"] = "a unit: bytes in a GiB / GB / MiB, never a choice",
        ["WslCare.Core/Actions/DockerCleanups/VolumeRemoval.cs: Take(5)"] = "a display truncation: how many entries one sentence quotes, not how much is done",
        ["WslCare.Core/Actions/Engine/IdleGate.cs: Take(3)"] = "a display truncation: how many entries one sentence quotes, not how much is done",
        ["WslCare.Core/Actions/Engine/IdleGate.cs: Take(4)"] = "an argv heuristic: how many leading words name the program, a property of the command line",
        ["WslCare.Core/Actions/Engine/RequestSweep.cs: TimeSpan.FromMinutes(1)"] = "a text format: an age under a minute is shown in seconds",
        ["WslCare.Core/Actions/Engine/RunningState.cs: StartTolerance"] = "the kernel's start-time resolution (a 10 ms tick from the boot time) — a measurement tolerance",
        ["WslCare.Core/Actions/JournalVacuum.cs: Gib"] = "a unit: bytes in a GiB / GB / MiB, never a choice",
        ["WslCare.Core/Actions/Memory/CacheDrop.cs: Gib"] = "a unit: bytes in a GiB / GB / MiB, never a choice",
        ["WslCare.Core/Actions/PackageCaches/PackageCacheClean.cs: Take(60)"] = "a display truncation: how many entries one sentence quotes, not how much is done",
        ["WslCare.Core/Actions/Suspects/AgentOrphans.cs: InitPid"] = "the kernel's init process is pid 1 — an orphan's parent (E7.S2b review A-M2)",
        ["WslCare.Core/Collectors/Procfs/ProcessFiles.cs: ProgramWords"] = "an argv heuristic: how many leading words name the program, a property of the command line (moved here from A18 by plan §15q E7.S2d C-2)",
        ["WslCare.Core/Mcp/McpSample.cs: PercentPerCore"] = "a unit: one whole core is 100 % of one core",
        ["WslCare.Core/Mcp/McpRunLogs.cs: TimeSpan.FromDays(1)"] = "a calendar day: the run logs of today and of yesterday — the folders a starts window of at most one day (its key's own maximum) can reach",
        ["WslCare.Core/Actions/Suspects/AgentOrphans.cs: Take(5)"] = "a display truncation: how many entries one sentence quotes, not how much is done",
        ["WslCare.Core/Actions/Suspects/SuspectTermination.cs: Take(5)"] = "a display truncation: how many entries one sentence quotes, not how much is done",
        ["WslCare.Core/Actions/TargetUser.cs: FirstLoginUid"] = "Debian/Ubuntu's UID_MIN (login.defs) — the distribution's convention",
        ["WslCare.Core/Actions/TargetUser.cs: NobodyUid"] = "the kernel's overflow uid (nobody)",
        ["WslCare.Core/Actions/UserCaches/EditorServerCleanup.cs: Take(5)"] = "a display truncation: how many entries one sentence quotes, not how much is done",
        ["WslCare.Core/Actions/UserCaches/NpmCacheClean.cs: Gib"] = "a unit: bytes in a GiB / GB / MiB, never a choice",
        ["WslCare.Core/Actions/UserCaches/ToolCacheTrims.cs: Gib"] = "a unit: bytes in a GiB / GB / MiB, never a choice",
        ["WslCare.Core/Agents/AgentDiscovery.cs: MaxLinkHops"] = "the kernel's own link limit (MAXSYMLINKS, ELOOP at 40)",
        ["WslCare.Core/Agents/AgentWalk.cs: Take(5)"] = "the wire shape: a report's 'largest' list holds five sessions (contracts/agents.schema.json)",
        ["WslCare.Core/Agents/AgentsReport.cs: Mb"] = "a unit: bytes in a GiB / GB / MiB, never a choice",
        ["WslCare.Core/Agents/AgentsReport.cs: Gb"] = "a unit: bytes in a GiB / GB / MiB, never a choice",
        ["WslCare.Core/Agents/ExtraAgent.cs: MaxEntries"] = "the extra-agent schema limit, part of the contract other tools write against (contracts/config-keys.json)",
        ["WslCare.Core/Agents/ExtraAgent.cs: MaxFolders"] = "the extra-agent schema limit, part of the contract other tools write against (contracts/config-keys.json)",
        ["WslCare.Core/Agents/ExtraAgent.cs: MaxPathLength"] = "the extra-agent schema limit, part of the contract other tools write against (contracts/config-keys.json)",
        ["WslCare.Core/Agents/ExtraAgent.cs: MaxGlobLength"] = "the extra-agent schema limit, part of the contract other tools write against (contracts/config-keys.json)",
        ["WslCare.Core/Agents/ExtraAgent.cs: MaxNameLength"] = "the extra-agent schema limit, part of the contract other tools write against (contracts/config-keys.json)",
        ["WslCare.Core/Agents/ExtraAgent.cs: Take(40)"] = "a display truncation: how many entries one sentence quotes, not how much is done",
        ["WslCare.Core/Collectors/ContainerCgroups.cs: IdLength"] = "a Docker container id is 64 hex characters — Docker's format",
        ["WslCare.Core/Collectors/ProcessFamilies.cs: Take(2)"] = "an argv heuristic: how many leading words name the program, a property of the command line",
        ["WslCare.Core/Collectors/Procfs/BuddyInfo.cs: SmallOrder"] = "a buddy-allocator order the report names (order 4 / order 7) — the kernel's format",
        ["WslCare.Core/Collectors/Procfs/BuddyInfo.cs: LargeOrder"] = "a buddy-allocator order the report names (order 4 / order 7) — the kernel's format",
        ["WslCare.Core/Collectors/Procfs/KernelFacts.cs: AtNull"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/Collectors/Procfs/KernelFacts.cs: AtPageSize"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/Collectors/Procfs/KernelFacts.cs: AtClockTick"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/Collectors/Procfs/KernelFacts.cs: EntryBytes"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/Collectors/Procfs/MemInfo.cs: BytesPerKibibyte"] = "a unit: bytes in a GiB / GB / MiB, never a choice",
        ["WslCare.Core/Collectors/Procfs/ProcessFiles.cs: BytesPerKibibyte"] = "a unit: bytes in a GiB / GB / MiB, never a choice",
        ["WslCare.Core/Collectors/Procfs/ProcessFiles.cs: FirstFieldAfterName"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/Collectors/Procfs/ProcessFiles.cs: TtyField"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/Collectors/Procfs/ProcessFiles.cs: UserTimeField"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/Collectors/Procfs/ProcessFiles.cs: SystemTimeField"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/Collectors/Procfs/ProcessFiles.cs: StartTimeField"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/Config/ConfigKeys.cs: CountCeiling"] = "a key's range ceiling — the contract that bounds the configuration itself",
        ["WslCare.Core/Config/ConfigKeys.cs: GbCeiling"] = "a key's range ceiling — the contract that bounds the configuration itself",
        ["WslCare.Core/Config/ConfigKeys.cs: DaysCeiling"] = "a key's range ceiling — the contract that bounds the configuration itself",
        ["WslCare.Core/Config/ConfigKeys.cs: HoursCeiling"] = "a key's range ceiling — the contract that bounds the configuration itself",
        ["WslCare.Core/Config/KeyRules.cs: MaxLength"] = "a path key's length in the schema (contracts/config-keys.json)",
        ["WslCare.Core/Config/NumberRules.cs: BytesPerShownName"] = "a coupled-limit rule's factor: a 64-hex name, its quotes and its comma — the request file's format",
        ["WslCare.Core/Config/NumberRules.cs: HeartbeatsBeforeWedged"] = "the owner's rule itself: wedged is at least three heartbeats",
        ["WslCare.Core/Config/NumberRules.cs: StopCeilingMarginSeconds"] = "a coupled-limit rule's margin: the stop command's ceiling stays this far above the unit's TimeoutStopSec",
        ["WslCare.Core/Config/Tuning.cs: BytesPerMebibyte"] = "a unit: bytes in a GiB / GB / MiB, never a choice",
        ["WslCare.Core/Config/Tuning.cs: BytesPerGibibyte"] = "a unit: bytes in a GiB / GB / MiB, never a choice",
        ["WslCare.Core/Config/UserConfigWriter.cs: MaxAsideProbes"] = "a naming fallback: after this many '-n' suffixes the aside name is a GUID — no limit on what is done",
        ["WslCare.Core/Docker/DockerEngine.cs: MinimumMajorForA4"] = "Docker's own version contract: the anonymous-volume label exists from engine 23",
        ["WslCare.Core/Docker/DockerEvents.cs: NanosPerTick"] = "a unit: bytes in a GiB / GB / MiB, never a choice",
        ["WslCare.Core/Docker/DockerText.cs: 1024 * 1024"] = "a unit: bytes in a GiB / GB / MiB, never a choice",
        ["WslCare.Core/Docker/DockerText.cs: 1024 * 1024 * 1024"] = "a unit: bytes in a GiB / GB / MiB, never a choice",
        ["WslCare.Core/Docker/DockerText.cs: 1024 * 1024 * 1024 * 1024"] = "a unit: bytes in a GiB / GB / MiB, never a choice",
        ["WslCare.Core/Doctor/DoctorRun.cs: TakeLast(5)"] = "a display truncation: how many entries one sentence quotes, not how much is done",
        ["WslCare.Core/Events/Coverage.cs: BackfillWindow"] = "Docker's event buffer reach and plan §4.3's '24 h' count — the report's definition",
        ["WslCare.Core/Events/Coverage.cs: CountWindow"] = "the report's definition: 'started in the last 24 h' (plan §4.3)",
        ["WslCare.Core/Events/Coverage.cs: BufferCapacity"] = "moby's own event buffer (eventsLimit = 256) — Docker's constant, measured",
        ["WslCare.Core/Files/BeneathFiles.cs: ReadOnlyNonBlocking"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/Files/BeneathFiles.cs: PathOnly"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/Files/BeneathFiles.cs: CloseOnExec"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/Files/BeneathFiles.cs: NoEntry"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/Files/BeneathFiles.cs: NotADirectory"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/Files/BeneathFiles.cs: TooManyLinks"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/Files/RealPath.cs: MaxLinkHops"] = "the kernel's own link limit (MAXSYMLINKS, ELOOP at 40)",
        ["WslCare.Core/Files/RegularFiles.cs: OpenReadOnlyNonBlocking"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/Files/RegularFiles.cs: AtEmptyPath"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/Files/RegularFiles.cs: AtFdCwd"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/Files/RegularFiles.cs: AtSymlinkNoFollow"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/Files/RegularFiles.cs: StatxType"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/Files/RegularFiles.cs: StatxBasic"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/Files/RegularFiles.cs: StatxSize"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/Files/RegularFiles.cs: StatxModeOffset"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/Files/RegularFiles.cs: TypeMask"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/Files/RegularFiles.cs: TypeRegular"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/Files/RegularFiles.cs: NoEntry"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/Files/RegularFiles.cs: NotADirectory"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/Files/RegularFiles.cs: StatxMode"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/Files/RegularFiles.cs: StatxUid"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/Files/RegularFiles.cs: StatxUidOffset"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/Files/RegularFiles.cs: GroupOrOtherWrite"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/Files/RegularFiles.cs: TooManyLinks"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/Files/RegularFiles.cs: ExecuteOk"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/Files/RegularFiles.cs: Exists"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/Files/TreeWalk.cs: CancellationStride"] = "how often a walk looks at its token — a cost of the check, no limit on the walk",
        ["WslCare.Core/Preview/CleanupTargets.cs: CapUnit"] = "a unit: bytes in a GiB / GB / MiB, never a choice",
        ["WslCare.Core/Processes/ProcessSignals.cs: SigTerm"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/Processes/ProcessSignals.cs: SigKill"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/Processes/ProcessSignals.cs: Esrch"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/Processes/ProcessSignals.cs: Eintr"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/Processes/SystemDriveFiles.cs: GroupOrOtherWrite"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/Processes/SystemDriveFiles.cs: AnyExecute"] = "a kernel ABI constant (flag, errno, field offset) — the kernel decides it",
        ["WslCare.Core/SchemaVersion.cs: Current"] = "the wire schema version — a contract",
        ["WslCare.Core/Systemd/SystemdParsers.cs: 1L << 10"] = "a unit: bytes in a GiB / GB / MiB, never a choice",
        ["WslCare.Core/Systemd/SystemdParsers.cs: 1L << 20"] = "a unit: bytes in a GiB / GB / MiB, never a choice",
        ["WslCare.Core/Systemd/SystemdParsers.cs: 1L << 30"] = "a unit: bytes in a GiB / GB / MiB, never a choice",
        ["WslCare.Core/Systemd/SystemdParsers.cs: 1L << 40"] = "a unit: bytes in a GiB / GB / MiB, never a choice",
        ["WslCare.Core/Systemd/SystemdParsers.cs: 1L << 50"] = "a unit: bytes in a GiB / GB / MiB, never a choice",
        ["WslCare.Core/Thresholds/ThresholdRules.cs: Gib"] = "a unit: bytes in a GiB / GB / MiB, never a choice",
        ["WslCare.Core/Actions/Engine/RequestSweep.cs: [..16]"] = "a run id's timestamp part (yyyyMMddTHHmmssZ, 16 characters) — the id's format",
        ["WslCare.Core/Actions/UserCaches/EditorServerCleanup.cs: <= 255"] = "the kernel's longest file name (NAME_MAX)",
        ["WslCare.Core/Actions/UserCaches/EditorServerCleanup.cs: <= 64"] = "a commit id's length (7 to 64 hex digits) — git's format",
        ["WslCare.Core/Config/NumberRules.cs: BytesPerCpuEntry"] = "a coupled-limit rule's factor or margin — part of the rule itself (E7.S2b/S2c review)",
        ["WslCare.Core/Config/NumberRules.cs: CeilingMarginSeconds"] = "a coupled-limit rule's factor or margin — part of the rule itself (E7.S2b/S2c review)",
        ["WslCare.Core/Config/NumberRules.cs: HistoryBytesPerDay"] = "a coupled-limit rule's factor or margin — part of the rule itself (E7.S2b/S2c review)",
        ["WslCare.Core/Config/NumberRules.cs: RequestOverheadBytes"] = "a coupled-limit rule's factor or margin — part of the rule itself (E7.S2b/S2c review)",
        ["WslCare.Core/Config/NumberRules.cs: RunMarginMinutes"] = "a coupled-limit rule's factor or margin — part of the rule itself (E7.S2b/S2c review)",
        ["WslCare.Core/Docker/DockerInventory.cs: >= 19"] = "an image id's short form (sha256: and 12 hex digits) — Docker's format",
        ["WslCare.Core/Docker/DockerJson.cs: == 64"] = "a full Docker id is 64 hex digits — Docker's format",
        ["WslCare.Core/Events/Continuity.cs: > 12"] = "a Docker short id is 12 hex digits — Docker's format",
        ["WslCare.Core/Events/Continuity.cs: [..12]"] = "a Docker short id is 12 hex digits — Docker's format",
        ["WslCare.Core/Processes/Policy/CommandPolicy.cs: > 120"] = "a display truncation: how much of a value one sentence quotes, not how much is done",
        ["WslCare.Core/Processes/Policy/CommandPolicy.cs: [..120]"] = "a display truncation: how much of a value one sentence quotes, not how much is done",
        ["WslCare.Core/Processes/Policy/CommandTemplate.cs: > 40"] = "a display truncation: how much of a value one sentence quotes, not how much is done",
        ["WslCare.Core/Processes/Policy/SlotKind.cs: <= 128"] = "a command-policy slot bound — the closed policy is never configuration (plan §15q R1.3)",
        ["WslCare.Core/Processes/Policy/SlotKind.cs: <= 19"] = "the digits of a 64-bit number — a format",
        ["WslCare.Core/Processes/Policy/SlotKind.cs: <= 32"] = "a Linux account name's length (useradd's 32) — the system's format",
        ["WslCare.Core/Processes/Policy/SlotKind.cs: <= 40"] = "a command-policy slot bound — the closed policy is never configuration (plan §15q R1.3)",
        ["WslCare.Core/Processes/Policy/SlotKind.cs: == 20"] = "an RFC 3339 UTC instant to the second is 20 characters — a format",
        ["WslCare.Core/Processes/WindowsSystemDrive.cs: >= 10"] = "the fields of a /proc/self/mountinfo line — the kernel's format",
        ["WslCare.Core/Thresholds/ThresholdRules.cs: Gb"] = "a unit: bytes in a GiB / GB / MiB, never a choice",
    };

    [Fact]
    public void No_behavioural_number_is_a_literal()
    {
        var found = SourceFiles().SelectMany(f => Numbers(Relative(f), File.ReadAllText(f))).Where(n => !Formats.ContainsKey(n)).Distinct().ToList();

        found.Should().BeEmpty("every behavioural number is a configuration key (E7.S2c); a format, a contract or a unit is listed in Formats with its reason");
    }

    /// <summary>The companion: the scan finds each shape it looks for — a scan that finds nothing would pass forever.</summary>
    [Fact]
    public void The_number_scan_finds_a_planted_literal_of_every_shape()
    {
        const string planted = """
            public static readonly TimeSpan Wait = TimeSpan.FromSeconds(42);
            private const int MaxThings = 17;
            var top = items.Take(9);
            var cap = 3 * 1024 * 1024;
            Thread.Sleep(25);
            await Task.Delay(5);
            var cut = age > 30 ? text[..120] : text.Substring(0, 64);
            var wait = new TimeSpan(0, 0, 9);
            public int Most => 250;
            public int Least { get; } = 12;
            """;

        Numbers("Planted.cs", planted).Should().BeEquivalentTo([
            "Planted.cs: Wait", "Planted.cs: MaxThings", "Planted.cs: Take(9)", "Planted.cs: 3 * 1024 * 1024", "Planted.cs: Sleep(25)", "Planted.cs: Delay(5)",
            "Planted.cs: > 30", "Planted.cs: [..120]", "Planted.cs: Substring(0, 64)", "Planted.cs: new TimeSpan(0", "Planted.cs: => 250;", "Planted.cs: } = 12;"]);
    }

    [Fact]
    public void Every_listed_format_still_exists_in_the_source()
    {
        var all = SourceFiles().SelectMany(f => Numbers(Relative(f), File.ReadAllText(f))).ToHashSet(StringComparer.Ordinal);

        Formats.Keys.Where(k => !all.Contains(k)).Should().BeEmpty("a listed format that is gone is a stale entry");
    }

    private static IEnumerable<string> Numbers(string file, string source)
    {
        var text = WithoutComments(source);
        foreach (Match m in Declared().Matches(text))
        {
            yield return $"{file}: {m.Groups["name"].Value}";
        }

        foreach (Match m in InlineTime().Matches(text))
        {
            if (!IsInDeclaration(text, m.Index))
            {
                yield return $"{file}: {m.Value}";
            }
        }

        foreach (Match m in Taken().Matches(text))
        {
            yield return $"{file}: {m.Value}";
        }

        foreach (Match m in LiteralArgument().Matches(text))
        {
            yield return $"{file}: {m.Value}";
        }

        // E7.S2b/S2c review C-M9: a number compared, clipped to, built into a TimeSpan, or answered by a property is a number too.
        foreach (var shape in new[] { Compared(), NewTimeSpan(), Clipped(), Answered() })
        {
            foreach (Match m in shape.Matches(text))
            {
                yield return $"{file}: {m.Value.Trim()}";
            }
        }

        foreach (Match m in ByteProduct().Matches(text))
        {
            if (!IsInDeclaration(text, m.Index))
            {
                yield return $"{file}: {m.Value}";
            }
        }
    }

    /// <summary>A literal on a line that declares a const / static readonly is that declaration's (named once, by its symbol).</summary>
    private static bool IsInDeclaration(string text, int index)
    {
        var start = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var end = text.IndexOf('\n', index);
        return Declared().IsMatch(text[start..(end < 0 ? text.Length : end)]);
    }

    private static string WithoutComments(string source) => LineComment().Replace(source, string.Empty);

    [GeneratedRegex(@"\b(?:const|static\s+readonly)\s+(?:int|long|uint|ulong|double|TimeSpan)\s+(?<name>\w+)\s*=\s*(?:[-(]?\s*\d|TimeSpan\.From\w+\(\s*\d)", RegexOptions.CultureInvariant)]
    private static partial Regex Declared();

    [GeneratedRegex(@"TimeSpan\.From(?:Milliseconds|Seconds|Minutes|Hours|Days)\(\s*\d[\d_.]*\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex InlineTime();

    [GeneratedRegex(@"\bTake(?:Last)?\(\s*(?:[2-9]|\d{2,})\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex Taken();

    [GeneratedRegex(@"\b\d+L?\s*\*\s*1024(?:\s*\*\s*1024)*|\b1L?\s*<<\s*\d+", RegexOptions.CultureInvariant)]
    private static partial Regex ByteProduct();

    /// <summary>A literal handed to a wait, a jitter or a capped read: <c>Thread.Sleep(10)</c>, <c>Next(5, 25)</c>,
    /// <c>ReadStateFile(path, 4096)</c>.</summary>
    [GeneratedRegex(@"\b(?:Next|Read\w*File)\([^()]*?\b\d{2,}[^()]*\)|\b(?:Sleep|Delay)\([^()]*?\b\d[^()]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex LiteralArgument();

    /// <summary>A comparison with a literal of two digits or more (<c>age &gt; 30</c>, <c>x.Length &gt;= 120</c>).</summary>
    [GeneratedRegex(@"(?<![<>=!])(?:[<>]=?|==|!=)\s*\d{2,}(?![\d.x])", RegexOptions.CultureInvariant)]
    private static partial Regex Compared();

    [GeneratedRegex(@"new TimeSpan\(\s*\d", RegexOptions.CultureInvariant)]
    private static partial Regex NewTimeSpan();

    /// <summary>A clip to a literal: <c>[..120]</c>, <c>[^40..]</c>, <c>Substring(0, 64)</c>, <c>Skip(10)</c>.</summary>
    [GeneratedRegex(@"\[\^?\d{2,}\.\.\]|\[\.\.\^?\d{2,}\]|\bSubstring\([^()]*\b\d{2,}\)|\bSkip\(\s*\d{2,}\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex Clipped();

    /// <summary>A property that answers a literal: <c>=&gt; 250;</c>, <c>{ get; } = 12;</c>.</summary>
    [GeneratedRegex(@"=>\s*-?\d{2,}[LlDdMm]?\s*;|\}\s*=\s*-?\d{2,}[LlDdMm]?\s*;", RegexOptions.CultureInvariant)]
    private static partial Regex Answered();

    [GeneratedRegex(@"//[^\n]*", RegexOptions.CultureInvariant)]
    private static partial Regex LineComment();

    private static IReadOnlyList<string> SourceFiles()
    {
        var root = typeof(NumbersArchitectureTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "WslCare.SourceRoot").Value!;
        return [.. Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))];
    }

    private static string Relative(string file)
    {
        var root = typeof(NumbersArchitectureTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "WslCare.SourceRoot").Value!;
        return Path.GetRelativePath(root, file).Replace('\\', '/');
    }
}
