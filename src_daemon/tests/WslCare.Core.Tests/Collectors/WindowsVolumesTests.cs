using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Health;
using WslCare.Core.Thresholds;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Collectors;

/// <summary>
/// E19.S2: the native reading of the lettered Windows volumes (<see cref="WindowsVolumes"/>, <c>DriveInfo</c>, no process) over SCRIPTED
/// drives — a test never reads this machine's. Only <c>Fixed</c> and <c>Removable</c> drives are volumes; a network drive is dropped
/// WITHOUT being asked anything (a dead mapped drive can block a status for the whole network timeout); a drive that is not ready or throws
/// stays in the table with its size and free space unread and the reason, never 0.
/// </summary>
public sealed class WindowsVolumesTests
{
    private const long Gib = 1L << 30;

    /// <summary>A drive that answers what it is told and records every property read of it.</summary>
    private sealed class ScriptedDrive(string name, DriveType type) : IDrive
    {
        private readonly List<string> _touched = [];

        public bool Ready { get; init; } = true;

        public long Total { get; init; } = 100 * Gib;

        public long Free { get; init; } = 40 * Gib;

        public string Format { get; init; } = "NTFS";

        public string Label { get; init; } = string.Empty;

        /// <summary>Thrown by the size and the free space, the way a disk that went away under the read does.</summary>
        public Exception? Failure { get; init; }

        /// <summary>Every property read after <see cref="Name"/> and <see cref="DriveType"/>, which are cheap and local.</summary>
        public IReadOnlyList<string> Touched => _touched;

        public string Name => name;

        public DriveType DriveType => type;

        public bool IsReady => Read(nameof(IsReady), Ready);

        public long TotalSize => Read(nameof(TotalSize), Total);

        public long AvailableFreeSpace => Read(nameof(AvailableFreeSpace), Free);

        public string DriveFormat => Read(nameof(DriveFormat), Format);

        public string VolumeLabel => Read(nameof(VolumeLabel), Label);

        private T Read<T>(string what, T value)
        {
            _touched.Add(what);
            return Failure is not null && what is nameof(TotalSize) or nameof(AvailableFreeSpace) ? throw Failure : value;
        }
    }

    private static Reading<IReadOnlyList<WindowsVolume>> Read(params IDrive[] drives) => new WindowsVolumes(() => drives).Read();

    private static IReadOnlyList<WindowsVolume> Table(Reading<IReadOnlyList<WindowsVolume>> reading) =>
        reading.Should().BeOfType<Reading<IReadOnlyList<WindowsVolume>>.Available>(reading.ReasonOrEmpty).Subject.Value;

    [Fact]
    public void A_fixed_and_a_removable_drive_are_volumes_with_their_figures_and_no_disk_number()
    {
        var table = Table(Read(
            new ScriptedDrive("C:\\", DriveType.Fixed) { Total = 953 * Gib, Free = 114 * Gib, Format = "NTFS", Label = "System" },
            new ScriptedDrive("E:\\", DriveType.Removable) { Total = 30_000_000, Free = 12_000_000, Format = "FAT", Label = "KEYBOARD" }));

        table.Select(v => v.Letter).Should().Equal("C", "E");
        var system = table[0];
        system.SizeBytes.Should().Be(Reading.Of(953 * Gib));
        system.FreeBytes.Should().Be(Reading.Of(114 * Gib));
        (system.FileSystem, system.Label).Should().Be(("NTFS", "System"));
        system.DiskNumber.ReasonOrEmpty.Should().Contain("not read natively", "the physical disk number is Get-Partition's, which the native reading does not run");
        table[1].FileSystem.Should().Be("FAT");
    }

    [Fact]
    public void A_network_drive_is_not_read()
    {
        var network = new ScriptedDrive("Z:\\", DriveType.Network);
        var table = Table(Read(
            new ScriptedDrive("C:\\", DriveType.Fixed),
            network,
            new ScriptedDrive("D:\\", DriveType.CDRom),
            new ScriptedDrive("R:\\", DriveType.Ram),
            new ScriptedDrive("N:\\", DriveType.NoRootDirectory),
            new ScriptedDrive("U:\\", DriveType.Unknown),
            new ScriptedDrive("E:\\", DriveType.Removable)));

        table.Select(v => v.Letter).Should().Equal("C", "E");
        network.Touched.Should().BeEmpty("a mapped drive that is down can block the read for the whole network timeout: it is dropped by its type alone");
    }

    [Fact]
    public void A_not_ready_drive_is_unknown_with_its_reason()
    {
        var empty = new ScriptedDrive("E:\\", DriveType.Removable) { Ready = false };
        var gone = new ScriptedDrive("F:\\", DriveType.Fixed) { Failure = new IOException("The device is not ready (a test)") };

        var table = Table(Read(empty, gone, new ScriptedDrive("C:\\", DriveType.Fixed)));

        table.Select(v => v.Letter).Should().Equal("C", "E", "F");
        table[1].SizeBytes.ReasonOrEmpty.Should().Contain("not ready");
        table[1].FreeBytes.ReasonOrEmpty.Should().Contain("not ready");
        table[2].SizeBytes.ReasonOrEmpty.Should().Contain("The device is not ready (a test)");
        table[2].FreeBytes.ReasonOrEmpty.Should().Contain("The device is not ready (a test)");
        table[0].FreeBytes.IsAvailable.Should().BeTrue("one drive that cannot be read costs only itself");
        empty.Touched.Should().Equal(nameof(IDrive.IsReady));
    }

    [Fact]
    public void An_unread_volume_is_judged_unknown_never_ok_beside_a_healthy_one()
    {
        var table = Read(new ScriptedDrive("C:\\", DriveType.Fixed) { Total = 953 * Gib, Free = 400 * Gib }, new ScriptedDrive("D:\\", DriveType.Fixed) { Ready = false });
        using var sandbox = new SandboxHost("windows-volumes-judged");
        var config = ConfigLoader.Load(sandbox.Paths, sandbox.Files).Config;

        var assessment = VolumeFreeRules.Evaluate(table, VolumeFreeLimits.From(config));

        assessment.Verdict.Level.Should().Be(Level.Unknown, "D: could not be read, and an unread figure is never ok");
        assessment.Volumes.Single(v => v.Letter == "D").Level.Should().Be(Level.Unknown);
        assessment.Volumes.Single(v => v.Letter == "C").Level.Should().Be(Level.Ok);
    }

    [Fact]
    public void A_table_that_cannot_be_listed_is_unread_with_the_reason()
    {
        var reading = new WindowsVolumes(static () => throw new IOException("the drive list is not available (a test)")).Read();

        reading.IsAvailable.Should().BeFalse();
        reading.ReasonOrEmpty.Should().Contain("the drive list is not available (a test)");
    }

    [Fact]
    public void A_path_that_is_not_a_drive_letter_is_not_a_volume()
    {
        var table = Table(Read(new ScriptedDrive("/", DriveType.Fixed), new ScriptedDrive("C:\\", DriveType.Fixed)));

        table.Select(v => v.Letter).Should().Equal("C");
    }

    /// <summary>The only leg that reads THIS machine's drives, and it asserts shape alone: the system drive is a volume with a size and
    /// a free space that fits it.</summary>
    [Fact]
    public void The_real_table_on_windows_holds_the_system_drive_with_figures_that_fit()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DriveInfo names Windows letters only on Windows");

        var table = Table(new WindowsVolumes().Read());

        var system = table.Should().Contain(v => v.Letter == "C").Subject;
        system.SizeBytes.Should().BeOfType<Reading<long>.Available>().Which.Value.Should().BeGreaterThan(0);
        (system.FreeBytes.ValueOr(-1) <= system.SizeBytes.ValueOr(0)).Should().BeTrue();
        table.Select(v => v.Letter).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
    }
}
