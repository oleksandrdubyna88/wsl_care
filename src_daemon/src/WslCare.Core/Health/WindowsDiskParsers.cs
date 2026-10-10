using System.Globalization;

using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;

using C = WslCare.Core.Health.WindowsDiskCommands;

namespace WslCare.Core.Health;

/// <summary>
/// The parser of the Windows disk script's answer (<see cref="WindowsDiskCommands"/>) — pure, so every shape is a unit test
/// over the captured answer. Lines are read BY TAG, never by position. An answer whose last line is not
/// <see cref="WindowsDiskCommands.EndLine"/> was cut and is unavailable as a whole. A line that does not parse — no tag, an
/// unknown tag, a field count or a number that is wrong — is counted (<see cref="WindowsDiskFacts.MalformedLines"/>) and
/// skipped; the rest still reads. A field the script printed EMPTY is an answer ("not there", "no partition"), never a 0.
/// </summary>
public static class WindowsDiskParsers
{
    /// <summary>The name Docker Desktop's data disk is listed under (it is no registered distro).</summary>
    public const string DockerDataDiskName = "docker-data";

    private const char Separator = '|';

    /// <summary>Every tag this parser reads; a tag outside it is a malformed line.</summary>
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        C.DistroTag, C.DistrosErrorTag, C.DockerTag, C.DockerErrorTag, C.VolumeTag, C.VolumesErrorTag, C.DiskTag, C.DisksErrorTag,
        C.ReliabilityTag, C.ReliabilityReadTag, C.ReliabilityRefusedTag, C.EventsReadTag, C.EventsErrorTag, C.EventTag,
        C.CommitTag, C.CommitErrorTag, C.CompressionTag, C.CompressionErrorTag, C.EditionTag, C.VersionTag, C.BuildTag,
        C.InstallDateTag, C.WindowsErrorTag, C.MachineGuidTag, C.MachineErrorTag, C.AdapterTag, C.AdaptersErrorTag,
    };

    /// <summary>The facts of <paramref name="stdout"/>; <paramref name="maxRead"/> and <paramref name="sinceDays"/> are the two
    /// numbers the script was built with (the events' cap and window).</summary>
    public static Reading<WindowsDiskFacts> Parse(string stdout, int maxRead, int sinceDays)
    {
        var lines = ProcText.Lines(stdout).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        return lines.Count > 0 && lines[^1] == C.EndLine
            ? Reading.Of(Facts(Answer.Of(lines[..^1]), maxRead, sinceDays))
            : Reading.Missing<WindowsDiskFacts>($"the Windows disk script's answer was cut: its last line is not {C.EndLine} ({lines.Count} line(s) read), so none of it is read");
    }

    private static WindowsDiskFacts Facts(Answer a, int maxRead, int sinceDays)
    {
        var distros = Listed(a, C.DistroTag, C.DistrosErrorTag, "the distros' registry (Lxss)", Distro);
        var volumes = Listed(a, C.VolumeTag, C.VolumesErrorTag, "the volumes", Volume);
        var disks = Listed(a, C.DiskTag, C.DisksErrorTag, "the physical disks", PhysicalDisk);
        var adapters = Listed(a, C.AdapterTag, C.AdaptersErrorTag, "the display adapters", Adapter);
        var (reliability, badReliability) = Reliability(a);
        var (events, badEvents) = Events(a, maxRead, sinceDays);
        var docker = Docker(a);
        var (commit, limit, badCommit) = Commit(a);
        var (identity, badIdentity) = Identity(a);
        var compression = Single(a, C.CompressionTag, C.CompressionErrorTag, "Memory Compression's working set").Bind(v => Long(v, "Memory Compression's working set"));
        var malformed = a.Unread + distros.Bad + volumes.Bad + disks.Bad + adapters.Bad + badReliability + badEvents + docker.Bad + badCommit + badIdentity + Bad(compression, a, C.CompressionTag);
        return new WindowsDiskFacts(distros.Items, docker.Disk, volumes.Items, disks.Items, reliability, events, commit, limit, compression, identity, adapters.Items, malformed);
    }

    /// <summary>The lines of one tag as a list, or — when the script printed its error tag — why the section could not be read.
    /// Lines that do not parse are counted, not listed.</summary>
    private static (Reading<IReadOnlyList<T>> Items, int Bad) Listed<T>(Answer a, string tag, string errorTag, string what, Func<string, Reading<T>> parse)
    {
        var parsed = a.All(tag).Select(parse).ToList();
        var bad = parsed.Count(p => !p.IsAvailable);
        return a.Has(errorTag)
            ? (Reading.Missing<IReadOnlyList<T>>(Failed(what, a.Last(errorTag))), bad)
            : (Reading.Of<IReadOnlyList<T>>([.. parsed.OfType<Reading<T>.Available>().Select(p => p.Value)]), bad);
    }

    private static string Failed(string what, string error) => $"the Windows disk script could not read {what}: {error}";

    /// <summary>One value of a tag printed once, or why not (its error tag, or no line at all).</summary>
    private static Reading<string> Single(Answer a, string tag, string errorTag, string what) => (a.Has(errorTag), a.Has(tag)) switch
    {
        (true, _) => Reading.Missing<string>(Failed(what, a.Last(errorTag))),
        (false, true) => Reading.Of(a.Last(tag)),
        _ => Reading.Missing<string>($"the Windows disk script printed no {tag.TrimEnd('=')} line ({what})"),
    };

    /// <summary>1 when the tag WAS printed and its value did not parse — an unprinted or failed figure is not a malformed line.</summary>
    private static int Bad<T>(Reading<T> reading, Answer a, string tag) => a.Has(tag) && !reading.IsAvailable ? 1 : 0;

    /// <summary><c>size|sparse|lastWrite|path|name</c> — the name last, free text.</summary>
    private static Reading<WindowsVirtualDisk> Distro(string value) =>
        Fields(value, 5) is { } f ? File(f[4], f[3], f[0], f[1], f[2]) : Malformed<WindowsVirtualDisk>(value);

    /// <summary>The file fields: all three empty — the file is not there; all three readable — the file; else malformed.</summary>
    private static Reading<WindowsVirtualDisk> File(string name, string path, string size, string sparse, string lastWrite)
    {
        var absent = $"{path} is not there";
        return (size, sparse, lastWrite) is ("", "", "")
            ? Reading.Of(new WindowsVirtualDisk(name, path, Reading.Missing<long>(absent), Reading.Missing<bool>(absent), Reading.Missing<DateTimeOffset>(absent)))
            : Present(name, path, Long(size, path), Flag(sparse, path), Instant(lastWrite, path));
    }

    private static Reading<WindowsVirtualDisk> Present(string name, string path, Reading<long> size, Reading<bool> sparse, Reading<DateTimeOffset> lastWrite) =>
        size.IsAvailable && sparse.IsAvailable && lastWrite.IsAvailable && path.Length > 0
            ? Reading.Of(new WindowsVirtualDisk(name, path, size, sparse, lastWrite))
            : Malformed<WindowsVirtualDisk>(path);

    /// <summary>Docker's data disk: absent is an answer about the file, said as the disk's own reason.</summary>
    private static (Reading<WindowsVirtualDisk> Disk, int Bad) Docker(Answer a)
    {
        var line = Single(a, C.DockerTag, C.DockerErrorTag, "Docker's data disk");
        var disk = line.Bind(v => Fields(v, 4) is { } f ? File(DockerDataDiskName, f[3], f[0], f[1], f[2]) : Malformed<WindowsVirtualDisk>(v));
        return (disk.Bind(d => d.SizeBytes.IsAvailable ? Reading.Of(d) : Reading.Missing<WindowsVirtualDisk>($"Docker's data disk {d.SizeBytes.ReasonOrEmpty}")), Bad(disk, a, C.DockerTag));
    }

    /// <summary><c>letter|disk|filesystem|size|free|label</c> — the label last, free text.</summary>
    private static Reading<WindowsVolume> Volume(string value) =>
        Fields(value, 6) is { } f && IsVolume(f)
            ? Reading.Of(new WindowsVolume(f[0], DiskOf(f[0], f[1]), f[2], Long(f[3], $"{f[0]}:'s size"), Long(f[4], $"{f[0]}:'s free space"), f[5]))
            : Malformed<WindowsVolume>(value);

    private static bool IsVolume(string[] f) => f[0].Length == 1 && char.IsAsciiLetter(f[0][0]) && IntOrEmpty(f[1]) && Numbers(f[3], f[4]);

    private static Reading<int> DiskOf(string letter, string disk) =>
        disk.Length == 0 ? Reading.Missing<int>($"{letter}: is on no partition Get-Partition could name") : Int(disk, $"{letter}:'s disk number");

    /// <summary><c>number|bus|health|operational|firmware|model</c> — the model last, free text.</summary>
    private static Reading<WindowsPhysicalDisk> PhysicalDisk(string value) =>
        Fields(value, 6) is { } f && int.TryParse(f[0], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? Reading.Of(new WindowsPhysicalDisk(number, f[1], f[2], f[3], f[4], f[5]))
            : Malformed<WindowsPhysicalDisk>(value);

    /// <summary><c>code|pnpDeviceId|name</c> — the name last, free text.</summary>
    private static Reading<DisplayAdapter> Adapter(string value) =>
        Fields(value, 3) is { } f && IntOrEmpty(f[0])
            ? Reading.Of(new DisplayAdapter(f[0].Length == 0 ? Reading.Missing<int>($"{f[2]} has no problem code") : Int(f[0], "the problem code"), f[1], f[2]))
            : Malformed<DisplayAdapter>(value);

    /// <summary>The counters when the script could read them, else the refusal — the ordinary unelevated answer (D4).</summary>
    private static (Reading<IReadOnlyList<StorageReliability>> Counters, int Bad) Reliability(Answer a)
    {
        var listed = Listed(a, C.ReliabilityTag, C.ReliabilityRefusedTag, "the reliability counters", Counter);
        var refused = $"the reliability counters were refused ({a.Last(C.ReliabilityRefusedTag)}): reading them needs elevation, and wsl-care never elevates";
        return (a.Has(C.ReliabilityRefusedTag), a.Has(C.ReliabilityReadTag)) switch
        {
            (true, _) => (Reading.Missing<IReadOnlyList<StorageReliability>>(refused), listed.Bad),
            (false, true) => listed,
            _ => (Reading.Missing<IReadOnlyList<StorageReliability>>("the Windows disk script printed no reliability answer"), listed.Bad),
        };
    }

    /// <summary><c>number|temperature|wear|readErrors|writeErrors|powerOnHours</c>.</summary>
    private static Reading<StorageReliability> Counter(string value) =>
        Fields(value, 6) is { } f && IsInt(f[0]) && Numbers(f[1], f[2], f[3], f[4], f[5])
            ? Reading.Of(new StorageReliability(int.Parse(f[0], CultureInfo.InvariantCulture), Long(f[1], "the temperature"), Long(f[2], "the wear"), Long(f[3], "the read errors"), Long(f[4], "the write errors"), Long(f[5], "the power-on hours")))
            : Malformed<StorageReliability>(value);

    /// <summary>The events' total and their groups; "no events" is <c>events.read=0</c>, a count, never an error.</summary>
    private static (Reading<StorageEvents> Events, int Bad) Events(Answer a, int maxRead, int sinceDays)
    {
        var groups = Listed(a, C.EventTag, C.EventsErrorTag, "the System log's storage events", Group);
        var read = Single(a, C.EventsReadTag, C.EventsErrorTag, "the System log's storage events").Bind(v => Int(v, "the number of events read"));
        var events = Reading.Combine(read, groups.Items, (total, list) => new StorageEvents(total, maxRead, sinceDays, list));
        return (events, groups.Bad + Bad(read, a, C.EventsReadTag));
    }

    /// <summary><c>yyyy-MM-dd|provider|id|HarddiskN|count</c> — the day is a CALENDAR day, stamped UTC, never converted.</summary>
    private static Reading<StorageEventGroup> Group(string value) =>
        Fields(value, 5) is { } f && Day(f[0]) is { } day && IsGroup(f)
            ? Reading.Of(new StorageEventGroup(day, f[1], int.Parse(f[2], CultureInfo.InvariantCulture), Harddisk(f[3]), int.Parse(f[4], CultureInfo.InvariantCulture)))
            : Malformed<StorageEventGroup>(value);

    private static bool IsGroup(string[] f) => IsInt(f[2]) && IsInt(f[4]) && HarddiskOrEmpty(f[3]);

    private static bool IsInt(string text) => text.Length > 0 && IntOrEmpty(text);

    private const string HarddiskPrefix = "Harddisk";

    private static bool HarddiskOrEmpty(string text) =>
        text.Length == 0 || (text.StartsWith(HarddiskPrefix, StringComparison.Ordinal) && IsInt(text[HarddiskPrefix.Length..]));

    private static Reading<int> Harddisk(string text) =>
        text.Length == 0 ? Reading.Missing<int>(@"the events name no \Device\HarddiskN") : Int(text[HarddiskPrefix.Length..], "the disk number");

    private static DateTimeOffset? Day(string text) =>
        DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? new DateTimeOffset(day, TimeSpan.Zero) : null;

    /// <summary>Commit charge and limit, both from one line.</summary>
    private static (Reading<long> Commit, Reading<long> Limit, int Bad) Commit(Answer a)
    {
        var pair = Single(a, C.CommitTag, C.CommitErrorTag, "commit").Bind(v => Fields(v, 2) is { } f && Numbers(f[0], f[1]) && f[0].Length > 0 && f[1].Length > 0
            ? Reading.Of((Committed: long.Parse(f[0], CultureInfo.InvariantCulture), Limit: long.Parse(f[1], CultureInfo.InvariantCulture)))
            : Malformed<(long Committed, long Limit)>(v));
        return (pair.Map(p => p.Committed), pair.Map(p => p.Limit), Bad(pair, a, C.CommitTag));
    }

    /// <summary>The edition, version, build, install date and machine id.</summary>
    private static (WindowsIdentity Identity, int Bad) Identity(Answer a)
    {
        var installed = Single(a, C.InstallDateTag, C.WindowsErrorTag, "Windows' install date").Bind(InstallDate);
        var guid = Single(a, C.MachineGuidTag, C.MachineErrorTag, "the machine id").Bind(MachineGuid);
        var identity = new WindowsIdentity(Text(a, C.EditionTag, "the edition"), Text(a, C.VersionTag, "the display version"), Text(a, C.BuildTag, "the build"), installed, guid);
        return (identity, Bad(installed, a, C.InstallDateTag) + Bad(guid, a, C.MachineGuidTag));
    }

    private static Reading<string> Text(Answer a, string tag, string what) =>
        Single(a, tag, C.WindowsErrorTag, what).Bind(v => v.Length > 0 ? Reading.Of(v) : Reading.Missing<string>($"the Windows disk script printed {what} empty"));

    private static Reading<DateTimeOffset> InstallDate(string value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds > 0 && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()
            ? Reading.Of(DateTimeOffset.FromUnixTimeSeconds(seconds))
            : Reading.Missing<DateTimeOffset>($"Windows' install date \"{value}\" is not unix seconds");

    private static Reading<string> MachineGuid(string value) =>
        Guid.TryParse(value, out var guid) ? Reading.Of(guid.ToString("D")) : Reading.Missing<string>($"the machine id \"{value}\" is not a GUID");

    private static string[]? Fields(string value, int count) => value.Split(Separator, count) is var f && f.Length == count ? f : null;

    private static bool IntOrEmpty(string text) => text.Length == 0 || int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _);

    private static bool Numbers(params string[] fields) => fields.All(f => f.Length == 0 || long.TryParse(f, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _));

    private static Reading<long> Long(string text, string what) =>
        long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? Reading.Of(value) : Reading.Missing<long>($"the Windows disk script printed no number for {what}");

    private static Reading<int> Int(string text, string what) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? Reading.Of(value) : Reading.Missing<int>($"the Windows disk script printed no number for {what}");

    private static Reading<bool> Flag(string text, string what) => text switch
    {
        "0" => Reading.Of(false),
        "1" => Reading.Of(true),
        _ => Reading.Missing<bool>($"the sparse bit of {what} is not 0 or 1"),
    };

    private static Reading<DateTimeOffset> Instant(string text, string what) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? Reading.Of(at)
            : Reading.Missing<DateTimeOffset>($"the last write of {what} is not an instant");

    private static Reading<T> Malformed<T>(string value) => Reading.Missing<T>($"a malformed line: {value}");

    /// <summary>The answer's lines by tag (the tag keeps its <c>=</c>), and how many lines carry no tag this parser reads.</summary>
    private sealed record Answer(ILookup<string, string> Values, int Unread)
    {
        public static Answer Of(IReadOnlyList<string> lines)
        {
            var tagged = lines.Select(Split).ToList();
            var known = tagged.Where(t => Known.Contains(t.Tag)).ToList();
            return new Answer(known.ToLookup(t => t.Tag, t => t.Value, StringComparer.Ordinal), tagged.Count - known.Count);
        }

        private static (string Tag, string Value) Split(string line) =>
            line.IndexOf('=', StringComparison.Ordinal) is var at and > 0 ? (line[..(at + 1)], line[(at + 1)..]) : (string.Empty, line);

        public bool Has(string tag) => Values.Contains(tag);

        public IEnumerable<string> All(string tag) => Values[tag];

        public string Last(string tag) => Values[tag].LastOrDefault() ?? string.Empty;
    }
}
