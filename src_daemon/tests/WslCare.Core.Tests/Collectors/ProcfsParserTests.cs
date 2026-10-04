using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Collectors;

/// <summary>
/// The parsers over the files captured from WSL Ubuntu on 2026-10-02 (<see cref="ProcfsFixture"/>; its
/// <c>SOURCE.txt</c> names source, date and redactions). Expected values are the fixture's own numbers,
/// read off the captured files by eye, not recomputed by the code under test.
/// </summary>
public sealed class ProcfsParserTests
{
    private const long KiB = 1024;

    private static string Fixture(string relative) => File.ReadAllText(Path.Combine(ProcfsFixture.Root, relative));

    private static byte[] FixtureBytes(string relative) => File.ReadAllBytes(Path.Combine(ProcfsFixture.Root, relative));

    [Fact]
    public void Meminfo_sizes_are_kibibytes_turned_into_bytes_and_counts_are_kept_apart()
    {
        var info = MemInfo.Parse(Fixture("proc/meminfo"));

        info.Bytes("MemTotal").Should().Be(Reading.Of(47_066_772 * KiB));
        info.Bytes("MemAvailable").Should().Be(Reading.Of(31_458_292 * KiB));
        info.Bytes("Inactive(anon)").Should().Be(Reading.Of(11_729_800 * KiB), "a key holding parentheses is one key");
        info.Bytes("AnonPages").Should().Be(Reading.Of(12_391_876 * KiB));
        info.Bytes("Shmem").Should().Be(Reading.Of(243_788 * KiB));
        info.Counts.Should().ContainKey("HugePages_Total", "a line without kB is a count");
        info.Bytes("HugePages_Total").IsAvailable.Should().BeFalse("a count is never read as a size");
    }

    [Fact]
    public void A_meminfo_line_the_kernel_does_not_write_is_unavailable_with_its_name_never_zero()
    {
        var info = MemInfo.Parse("MemTotal:  100 kB\nnot a line\n");

        info.Bytes("SwapTotal").Should().BeOfType<Reading<long>.Unavailable>()
            .Which.Reason.Should().Contain("SwapTotal");
    }

    [Fact]
    public void Buddyinfo_reads_every_zone_and_the_normal_zone_holds_the_fixtures_free_high_order_blocks()
    {
        var zones = BuddyInfo.Parse(Fixture("proc/buddyinfo"));

        zones.Select(z => z.Zone).Should().Equal("DMA32", "Normal");
        zones[1].FreeBlocksByOrder.Should().Equal(147, 40, 77, 101, 84, 44, 8, 2, 5, 3, 3923);
        var fragmentation = Fragmentation.Of(zones, Fragmentation.ThresholdZone, 4096).Should().BeOfType<Reading<Fragmentation>.Available>().Subject.Value;
        fragmentation.BlocksOrder4Plus.Should().Be(84 + 44 + 8 + 2 + 5 + 3 + 3923);
        fragmentation.BlocksOrder7Plus.Should().Be(2 + 5 + 3 + 3923);
        fragmentation.BytesOrder7Plus.Should().Be((2 * 128 + 5 * 256 + 3 * 512 + 3923 * 1024) * 4096L);
    }

    [Fact]
    public void A_zone_with_no_free_block_of_64_kib_reports_zero_blocks_as_a_real_zero()
    {
        // SYNTHETIC line in buddyinfo format carrying the zone Normal counts of the 2026-10-01 18:36
        // page-allocation-failure dump (research/2026-10-02_wsl_resource_baseline.md, Finding 1):
        // 12594*4kB 5327*8kB 948*16kB 173*32kB 0*64kB ... — no free block of order 4 or more.
        var zones = BuddyInfo.Parse("Node 0, zone   Normal  12594   5327    948    173      0      0      0      0      0      0      0\n");

        var fragmentation = Fragmentation.Of(zones, "Normal", 4096).Should().BeOfType<Reading<Fragmentation>.Available>().Subject.Value;

        fragmentation.BlocksOrder4Plus.Should().Be(0, "an empty high-order list is a measured zero, the alert case of plan §4.1");
        fragmentation.BlocksOrder7Plus.Should().Be(0);
    }

    [Fact]
    public void A_zone_the_kernel_does_not_list_is_unavailable()
    {
        Fragmentation.Of(BuddyInfo.Parse(Fixture("proc/buddyinfo")), "HighMem", 4096)
            .Should().BeOfType<Reading<Fragmentation>.Unavailable>().Which.Reason.Should().Contain("HighMem");
    }

    [Fact]
    public void Pressure_files_carry_some_and_full_with_three_averages_and_the_total()
    {
        var memory = PressureFile.Parse(Fixture("proc/pressure/memory"), "memory").Should().BeOfType<Reading<Pressure>.Available>().Subject.Value;
        var cpu = PressureFile.Parse(Fixture("proc/pressure/cpu"), "cpu").Should().BeOfType<Reading<Pressure>.Available>().Subject.Value;
        var io = PressureFile.Parse(Fixture("proc/pressure/io"), "io").Should().BeOfType<Reading<Pressure>.Available>().Subject.Value;

        memory.Some.Should().Be(new PressureLine(0, 0, 0, 0), "no memory stall at the capture: a measured zero");
        cpu.Some.Should().Be(new PressureLine(0.10, 4.18, 2.74, 12_077_848));
        cpu.Full.Should().Be(Reading.Of(new PressureLine(0, 0, 0, 0)), "this kernel writes a full line for cpu too");
        io.Full.Should().Be(Reading.Of(new PressureLine(0.06, 1.29, 1.58, 8_288_681)));
    }

    [Fact]
    public void A_pressure_file_without_a_full_line_keeps_some_and_marks_full_unavailable()
    {
        var cpu = PressureFile.Parse("some avg10=1.00 avg60=2.00 avg300=3.00 total=4\n", "cpu").Should().BeOfType<Reading<Pressure>.Available>().Subject.Value;

        cpu.Some.Avg60.Should().Be(2.00);
        cpu.Full.IsAvailable.Should().BeFalse();
        PressureFile.Parse("some avg10=x\n", "cpu").IsAvailable.Should().BeFalse("a malformed some line is not a zero pressure");
    }

    [Fact]
    public void The_clock_tick_and_the_page_size_come_from_the_auxiliary_vector()
    {
        KernelFacts.FromAuxVector(FixtureBytes("proc/self/auxv")).Should().Be(Reading.Of(new KernelFacts(100, 4096)));
        KernelFacts.FromAuxVector([]).IsAvailable.Should().BeFalse("an unknown tick is never replaced by a customary 100");
    }

    [Fact]
    public void The_boot_time_is_btime_of_proc_stat_in_utc()
    {
        BootTime.Parse(Fixture("proc/stat")).Should().Be(Reading.Of(DateTimeOffset.FromUnixTimeSeconds(1_790_948_339)));
        BootTime.Parse("cpu 1 2 3\n").IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void A_status_file_yields_name_parent_owner_and_rss_anon_plus_rss_shmem()
    {
        var status = ProcStatus.Parse(Fixture("proc/1169/status"), "1169").Should().BeOfType<Reading<ProcStatus>.Available>().Subject.Value;

        status.Name.Should().Be("MainThread");
        status.State.Should().Be('S');
        status.ParentPid.Should().Be(571);
        status.Uid.Should().Be(1000);
        status.RssAnonBytes.Should().Be(1_314_224 * KiB);
        status.RssShmemBytes.Should().Be(0);
        status.HeldBytes.Should().Be(1_314_224 * KiB, "VmRSS (1 421 940 kB) includes 107 716 kB of file pages, which are page cache, not held memory");
        status.IsKernelThread.Should().BeFalse();
    }

    [Theory]
    [InlineData("Name:\tkworker/0:1\nPPid:\t2\nKthread:\t1\n")]
    [InlineData("Name:\tkthreadd\nPPid:\t0\nVmRSS:\t0 kB\n")]
    public void A_kernel_thread_is_recognised_by_kthread_or_by_having_no_rss_anon(string text)
    {
        ProcStatus.Parse(text, "k").Should().BeOfType<Reading<ProcStatus>.Available>().Which.Value.IsKernelThread.Should().BeTrue();
    }

    [Fact]
    public void A_stat_line_is_counted_from_the_last_parenthesis_so_a_name_with_spaces_and_parentheses_does_not_shift_it()
    {
        var real = ProcStat.Parse(Fixture("proc/1169/stat"), "1169").Should().BeOfType<Reading<ProcStat>.Available>().Subject.Value;
        var odd = ProcStat.Parse("42 (a (b) c) S 1 42 42 0 -1 0 0 0 0 0 7 3 0 0 20 0 1 0 999 0 0\n", "42").Should().BeOfType<Reading<ProcStat>.Available>().Subject.Value;

        real.Should().Be(new ProcStat(TtyNumber: 34816, CpuTicks: 9123 + 4169, StartTicks: 1948));
        odd.Should().Be(new ProcStat(0, 10, 999));
        ProcStat.Parse("42 (short) S 1", "42").IsAvailable.Should().BeFalse();
    }

    [Theory]
    [InlineData("0::/init.scope\n", "")]
    [InlineData("0::/docker/0e456d1dc8c05f411335f6a0f82f24cc35f053d7e6a21707c2c46f131a15b69d\n", "0e456d1dc8c05f411335f6a0f82f24cc35f053d7e6a21707c2c46f131a15b69d")]
    [InlineData("0::/system.slice/docker-247515d64498725223f87330637fabb21a922e72f89bcd95b237b3db87968c40.scope\n", "247515d64498725223f87330637fabb21a922e72f89bcd95b237b3db87968c40")]
    [InlineData("0::/docker/buildkit\n", "")]
    [InlineData("0::/user.slice/user-1000.slice/session-4.scope\n", "")]
    public void A_process_belongs_to_a_container_when_its_cgroup_is_the_containers(string cgroupFile, string expectedId)
    {
        ContainerCgroups.ContainerIdOf(ProcCgroup.Parse(cgroupFile)).Should().Be(expectedId);
    }

    [Fact]
    public void Every_fixture_process_reads_a_cgroup_and_none_of_them_is_a_container_member()
    {
        // Measured 2026-10-02: Docker Desktop's container processes live in another PID namespace.
        foreach (var dir in Directory.GetDirectories(Path.Combine(ProcfsFixture.Root, "proc")).Where(d => Path.GetFileName(d).All(char.IsAsciiDigit)))
        {
            var path = ProcCgroup.Parse(File.ReadAllText(Path.Combine(dir, "cgroup")));
            path.Should().StartWith("/", dir);
            ContainerCgroups.ContainerIdOf(path).Should().BeEmpty(dir);
        }
    }

    [Fact]
    public void A_command_line_is_split_on_nul_and_secret_looking_values_are_redacted_in_both_flag_forms()
    {
        byte[] cmdline = [.. "node\0--connection-token=abc123\0--csrf_token\0zzz\0--port=0\0/mnt/c/x\0"u8];

        var argv = CommandLineText.Arguments(cmdline);

        argv.Should().Equal("node", "--connection-token=abc123", "--csrf_token", "zzz", "--port=0", "/mnt/c/x");
        CommandLineText.Shown(argv).Should().Be("node --connection-token=<redacted> --csrf_token <redacted> --port=0 /mnt/c/x");
    }

    [Fact]
    public void A_shown_command_line_is_cut_to_the_plans_200_characters()
    {
        var argv = CommandLineText.Arguments(FixtureBytes("proc/7203/cmdline"));

        string.Join(' ', argv).Length.Should().BeGreaterThan(CommandLineText.ShownLength, "the fixture's claude command line is long");
        CommandLineText.Shown(argv).Should().HaveLength(CommandLineText.ShownLength).And.StartWith("/home/user/.vscode-server/extensions/vendor.extension-a-1.0.0-linux-x64/resources/native-binary/claude");
    }

    [Fact]
    public void Container_cgroups_are_read_from_the_cgroup_tree_with_both_memory_figures()
    {
        var host = new PhysicalFileSystemOverFixture();

        var set = ContainerCgroups.Read(host.Files, ProcfsFixture.PathsAt(ProcfsFixture.Root).CgroupRoot).Should().BeOfType<Reading<ContainerSet>.Available>().Subject.Value;

        set.Containers.Should().HaveCount(10);
        var first = set.Containers.Single(c => c.Id == "0e456d1dc8c05f411335f6a0f82f24cc35f053d7e6a21707c2c46f131a15b69d");
        first.AnonShmemBytes.Should().Be(Reading.Of(373_964_800L + 0), "memory.stat anon + shmem, in bytes");
        first.MemoryCurrentBytes.Should().Be(Reading.Of(long.Parse(Fixture("sys/fs/cgroup/docker/0e456d1dc8c05f411335f6a0f82f24cc35f053d7e6a21707c2c46f131a15b69d/memory.current").Trim(), System.Globalization.CultureInfo.InvariantCulture)));
        set.CountedIds.Should().HaveCount(10);
    }

    [Fact]
    public void A_missing_cgroup_mount_is_unavailable_and_an_empty_one_is_an_empty_set()
    {
        using var root = new TempRoot("cgroup");
        var files = new PhysicalFileSystemOverFixture().Files;

        ContainerCgroups.Read(files, root.Under("absent")).IsAvailable.Should().BeFalse();
        ContainerCgroups.Read(files, root.Dir("sys/fs/cgroup")).Should().BeOfType<Reading<ContainerSet>.Available>()
            .Which.Value.Containers.Should().BeEmpty("no container running is an answer, not an unknown");
    }

    [Fact]
    public void Passwd_names_the_fixtures_owners()
    {
        Passwd.Parse(Fixture("etc/passwd")).Should().Contain(0, "root").And.Contain(1000, "user");
    }

    /// <summary>The real file system, sandboxed at the fixture (reads only).</summary>
    private sealed class PhysicalFileSystemOverFixture
    {
        public Core.Files.IFileSystem Files { get; } = new Core.Files.PhysicalFileSystem(ProcfsFixture.PathsAt(ProcfsFixture.Root));
    }
}
