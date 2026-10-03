using System.Globalization;
using System.Text;
using System.Text.Json;

using WslCare.Core.Docker;
using WslCare.Core.Health;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Processes.Policy;

/// <summary>
/// The world the ACTION property runs every registered action against (E3.S2): Docker answers generated per case — hostile
/// volume, container, mount and repository NAMES (Docker controls ids, which stay 64-hex; names are anyone's), random
/// versions (22, 29, unreadable), ages, keep and Testcontainers labels — a snap listing with hostile names, and answers to
/// every write the actions may ask for. Seeded through <see cref="HostileInputs"/>, so a failure replays.
/// </summary>
internal sealed class GeneratedWorld(HostileInputs inputs)
{
    private static readonly DateTimeOffset Now = FixedTimeProvider.DefaultNow;

    private readonly List<string> _volumes = [];

    /// <summary>The 64-hex volumes Docker treats as NAMED: listed without the <c>com.docker.volume.anonymous</c> label (what
    /// <c>docker volume create</c> without a name leaves), or dangling but absent from <c>system df -v</c> (labels unknown).
    /// No action may ever name one in a removal (independent review, item 1).</summary>
    private readonly HashSet<string> _notAnonymous = new(StringComparer.Ordinal);

    /// <summary>A runner under <paramref name="policy"/> answering this case's Docker, snap and tools.</summary>
    public RecordingCommandRunner Runner(CommandPolicy policy)
    {
        var containers = Enumerable.Range(0, inputs.Next(6)).Select(_ => Hex()).ToList();
        _volumes.AddRange(Enumerable.Range(0, inputs.Next(8)).Select(_ => inputs.Next(2) == 0 ? Hex() : Name()));
        var labels = _volumes.Distinct(StringComparer.Ordinal).ToDictionary(v => v, _ => inputs.Pick<string>(["com.docker.volume.anonymous=,wsl-care.keep=true", "com.docker.volume.anonymous=", "com.docker.volume.anonymous=", string.Empty]), StringComparer.Ordinal);
        var unlisted = Enumerable.Range(0, inputs.Next(3)).Select(_ => Hex()).ToList();
        _notAnonymous.UnionWith(_volumes.Where(v => labels[v].Length == 0).Concat(unlisted));
        var images = Enumerable.Range(0, inputs.Next(5)).Select(_ => "sha256:" + Hex()).ToList();
        return new RecordingCommandRunner { Policy = policy, Default = RecordingCommandRunner.Exited(0, "Archived and active journals take up 1.5G in the file system.") }
            .Script(DockerCommands.Version.Argv, 0, Version())
            .Script(DockerCommands.SystemDf.Argv, 0, "{\"Active\":\"1\",\"Reclaimable\":\"1GB (50%)\",\"Size\":\"2GB\",\"TotalCount\":\"3\",\"Type\":\"Local Volumes\"}\n")
            .Script(DockerCommands.SystemDfVerbose.Argv, 0, DiskUsage(containers, images, labels))
            .Script(DockerCommands.DanglingVolumes.Argv, 0, string.Join('\n', _volumes.Concat(unlisted)))
            .Script(DockerCommands.VolumeList.Argv, 0, string.Join('\n', _volumes.Where(_ => inputs.Next(2) == 0)))
            .Script(argv => argv is ["docker", "container", "inspect", ..], RecordingCommandRunner.Exited(0, string.Join('\n', containers.Select((id, i) => Inspect(id, images)))))
            .ScriptEffect(argv => argv is ["docker", "volume", "rm", ..] or ["docker", "rm", ..], r => RecordingCommandRunner.Exited(0, string.Join('\n', r.Argv.Skip(3))))
            .Script(argv => argv is ["docker", "image", "prune", ..], RecordingCommandRunner.Exited(0, "Deleted Images:\ndeleted: sha256:" + Hex() + "\n\nTotal reclaimed space: 1.5GB\n"))
            .Script(argv => argv is ["docker", "builder", "prune", "--help"], RecordingCommandRunner.Exited(0, inputs.Pick<string>(["      --max-used-space bytes", "      --keep-storage bytes", "  -f, --force"])))
            .Script(argv => argv is ["docker", "builder", "prune", ..], RecordingCommandRunner.Exited(0, "ID\tRECLAIMABLE\tSIZE\nabc\ttrue\t1GB\nTotal:\t1GB\n"))
            .Script(HealthCommands.SnapList.Argv, 0, Snaps())
            .Script(HealthCommands.WindowsClock.Argv, 0, Clock())
            .Script(Systemd.SystemdCommands.TimeSync.Argv, 0, $"NTP=yes\nNTPSynchronized={inputs.Pick<string>(["yes", "no", "no"])}\n")
            .Script(Systemd.SystemdCommands.ShowUnit("fstrim.timer").Argv, 0, $"Id=fstrim.timer\nLoadState=loaded\nUnitFileState={inputs.Pick<string>(["enabled", "disabled"])}\n")
            .Script(argv => argv is ["fstrim", "-av"], RecordingCommandRunner.Exited(inputs.Pick<int>([0, 64, 1]), $"/: 1 GiB ({inputs.Next(1 << 30)} bytes) trimmed on /dev/sdc\n{Name()}: x\n"));
    }

    /// <summary>The E3.S3 actions' process table (A3's servers and builds, A16's chronyd), hostile command lines around them.</summary>
    public WslCare.Core.Collectors.Reading<WslCare.Core.Collectors.ProcessSnapshot> Processes() =>
        inputs.Next(8) == 0
            ? WslCare.Core.Collectors.Reading.Missing<WslCare.Core.Collectors.ProcessSnapshot>("generated: unreadable")
            : WslCare.Core.Collectors.Reading.Of(Actions.UserWorld.Snapshot([.. Enumerable.Range(0, inputs.Next(6)).Select(i => inputs.Next(4) switch
            {
                0 => Actions.UserWorld.Process(100 + i, "dotnet MSBuild.dll /nodemode:1 " + Name(), family: "dotnet-build-servers", ageHours: inputs.Next(10)),
                1 => Actions.UserWorld.Process(100 + i, inputs.Pick<string>(["dotnet build", "dotnet test x", "dotnet run"])),
                2 => Actions.UserWorld.Process(100 + i, "/usr/sbin/chronyd -F 1", user: "_chrony") with { Name = "chronyd" },
                _ => Actions.UserWorld.Process(100 + i, Name()),
            })]));

    /// <summary>The Windows clock probe's three lines, Windows ahead or behind by 0–60 s — or something that is not them.</summary>
    private string Clock()
    {
        var started = Now.AddSeconds(inputs.Next(121) - 60);
        return inputs.Next(6) == 0 ? Name() : $"{Instant(started.AddSeconds(1))}\n{Instant(started)}\nC:\\Users\\{Name()}\n";
    }

    /// <summary>Some of this case's volume names, hostile ones included — what a "shown list" might carry (the named 64-hex
    /// ones too: a shown list never makes a named volume a target).</summary>
    public IReadOnlyList<string> Shown() => [.. _volumes.Concat(_notAnonymous).Where(_ => inputs.Next(2) == 0), Name()];

    /// <summary>Every removal argv that names a volume Docker treats as named — what must never happen.</summary>
    public IEnumerable<string> NamedVolumeRemovals(IEnumerable<CommandRequest> requests) =>
        requests.Where(r => r.Argv is ["docker", "volume", "rm", ..]).SelectMany(r => r.Argv.Skip(3)).Where(_notAnonymous.Contains);

    private string Version() =>
        $"{{\"Client\":{{\"Version\":\"29.6.1\"}},\"Server\":{{\"Platform\":{{\"Name\":\"x\"}},\"Version\":\"{inputs.Pick<string>(["29.6.1", "29.6.1", "22.0.4", "nightly"])}\"}}}}";

    private string DiskUsage(IReadOnlyList<string> containers, IReadOnlyList<string> images, IReadOnlyDictionary<string, string> labels)
    {
        var text = new StringBuilder("{\"Images\":[");
        text.AppendJoin(',', images.Select(id => $"{{\"ID\":\"{id}\",\"Repository\":{Json(inputs.Next(3) == 0 ? "<none>" : Name())},\"Tag\":\"x\",\"Containers\":\"{inputs.Next(2)}\",\"CreatedAt\":\"{Age()}\",\"Size\":\"1GB\",\"UniqueSize\":\"{inputs.Next(900)}MB\"}}"));
        text.Append("],\"Containers\":[");
        text.AppendJoin(',', containers.Select(id => $"{{\"ID\":\"{id}\",\"Names\":{Json(Name())},\"Image\":\"x\",\"State\":\"exited\",\"CreatedAt\":\"{Age()}\",\"Size\":\"{inputs.Next(900)}MB\",\"Labels\":\"\"}}"));
        text.Append("],\"Volumes\":[");
        text.AppendJoin(',', _volumes.Select(name => $"{{\"Name\":{Json(name)},\"Links\":\"0\",\"Size\":\"{inputs.Next(900)}MB\",\"Labels\":{Json(labels[name])}}}"));
        text.Append("],\"BuildCache\":[");
        text.AppendJoin(',', Enumerable.Range(0, inputs.Next(4)).Select(_ => $"{{\"ID\":\"{Hex()[..12]}\",\"InUse\":\"false\",\"Shared\":\"false\",\"LastUsedAt\":\"{Age()}\",\"Size\":\"{inputs.Next(30)}GB\"}}"));
        return text.Append("]}").ToString();
    }

    private string Inspect(string id, IReadOnlyList<string> images)
    {
        var mounts = string.Join(',', Enumerable.Range(0, inputs.Next(3)).Select(_ => $"{{\"type\":\"volume\",\"name\":{Json(MountedVolume())}}}"));
        var stopped = Instant(Now.AddDays(-inputs.Next(30)));
        return $"{{\"id\":\"{id}\",\"name\":{Json("/" + Name())},\"created\":\"{stopped}\",\"state\":\"{inputs.Pick<string>(["exited", "created", "running", "dead"])}\",\"startedAt\":\"{stopped}\",\"finishedAt\":\"{stopped}\","
            + $"\"image\":\"{(images.Count > 0 ? inputs.Pick(images) : "sha256:" + Hex())}\",\"logDriver\":\"json-file\",\"logMaxSize\":null,\"logPath\":\"/x\","
            + $"\"testcontainers\":{(inputs.Next(2) == 0 ? "\"true\"" : "null")},\"keep\":{(inputs.Next(4) == 0 ? "\"true\"" : "null")},\"mounts\":[{mounts}]}}";
    }

    /// <summary>A mounted volume: one of this case's listed volumes (anonymous, named-hex or named — so A5 meets each), or a
    /// name <c>system df -v</c> does not list.</summary>
    private string MountedVolume() => _volumes.Count > 0 && inputs.Next(3) > 0 ? inputs.Pick(_volumes) : inputs.Next(2) == 0 ? Hex() : Name();

    private string Snaps() =>
        "Name Version Rev Tracking Publisher Notes\n" + string.Concat(Enumerable.Range(0, inputs.Next(5)).Select(_ =>
            $"{inputs.Pick<string>(["core22", "lxd", "gnome-42-2204", inputs.HostileValue().Replace(' ', '_').Replace('\n', '_')])} 1.0 {inputs.Pick<string>(["1122", "x1", "28460", "--all"])} latest/stable canonical {inputs.Pick<string>(["disabled", "base,disabled", "-"])}\n"));

    private string Hex() => new([.. Enumerable.Range(0, 64).Select(_ => "0123456789abcdef"[inputs.Next(16)])]);

    private string Name() => inputs.HostileValue();

    private string Age() => Now.AddDays(-inputs.Next(60)).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss '+0000 UTC'", CultureInfo.InvariantCulture);

    private static string Instant(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Json(string text) => "\"" + JsonEncodedText.Encode(text).ToString() + "\"";
}
