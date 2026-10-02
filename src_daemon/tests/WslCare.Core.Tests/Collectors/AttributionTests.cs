using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Collectors;

/// <summary>
/// Who holds the memory (plan §4.2 as amended by §15b #4): process memory is RssAnon + RssShmem, a
/// container is counted once through its cgroup, and a remainder below zero is an inconsistent sample.
/// </summary>
public sealed class AttributionTests
{
    private const long KiB = 1024;
    private const string ContainerId = "0e456d1dc8c05f411335f6a0f82f24cc35f053d7e6a21707c2c46f131a15b69d";

    private static ProcessSnapshot Processes(long heldBytes) => new(1, 0, 0, 0, heldBytes, [], []);

    [Fact]
    public void The_remainder_is_anon_plus_shmem_minus_the_processes_minus_the_containers()
    {
        var result = Attribution.Compute(Reading.Of(100L), Reading.Of(20L), Reading.Of(Processes(70)), Reading.Of(new ContainerSet([new ContainerMemory(ContainerId, Reading.Of(999L), Reading.Of(30L))])));

        result.Should().Be(new Unattributed.Remainder(20), "100 + 20 - 70 - 30; memory.current (999) is not in the subtraction");
    }

    [Fact]
    public void A_negative_remainder_is_an_inconsistent_sample_and_never_a_negative_number()
    {
        var result = Attribution.Compute(Reading.Of(100L), Reading.Of(0L), Reading.Of(Processes(90)), Reading.Of(new ContainerSet([new ContainerMemory(ContainerId, Reading.Of(0L), Reading.Of(25L))])));

        result.Should().Be(new Unattributed.InconsistentSample(15));
    }

    [Fact]
    public void A_remainder_of_exactly_zero_is_a_remainder()
    {
        Attribution.Compute(Reading.Of(50L), Reading.Of(0L), Reading.Of(Processes(50)), Reading.Of(new ContainerSet([])))
            .Should().Be(new Unattributed.Remainder(0));
    }

    [Fact]
    public void An_unread_input_leaves_the_remainder_not_computed_with_that_inputs_reason()
    {
        var result = Attribution.Compute(Reading.Of(100L), Reading.Missing<long>("/proc/meminfo has no Shmem line"), Reading.Of(Processes(1)), Reading.Of(new ContainerSet([])));

        result.Should().BeOfType<Unattributed.NotComputed>().Which.Reason.Should().Contain("Shmem");
    }

    [Fact]
    public void Process_memory_is_rss_anon_plus_rss_shmem_not_vm_rss()
    {
        using var tree = new SyntheticProcTree()
            .MemInfo(anonKib: 1000, shmemKib: 100)
            .Process(10, 1, "/user.slice/a.scope", rssAnonKib: 600, rssShmemKib: 50, rssFileKib: 5000);

        var processes = Collect(tree);

        processes.HeldBytesTotal.Should().Be(650 * KiB, "RssFile (5 000 kB) is page cache and is never held memory");
        processes.Top.Should().ContainSingle().Which.HeldBytes.Should().Be(650 * KiB);
        Attribution.Compute(Reading.Of(1000 * KiB), Reading.Of(100 * KiB), Reading.Of(processes), Reading.Of(new ContainerSet([])))
            .Should().Be(new Unattributed.Remainder(450 * KiB), "1 100 kB - 650 kB; with VmRSS the sample would read as inconsistent");
    }

    [Fact]
    public void A_container_process_visible_in_the_distro_is_counted_once_through_its_cgroup()
    {
        using var tree = new SyntheticProcTree()
            .MemInfo(anonKib: 1000, shmemKib: 0)
            .Process(10, 1, "/user.slice/a.scope", rssAnonKib: 300)
            .Process(20, 1, $"/system.slice/docker-{ContainerId}.scope", rssAnonKib: 400)
            .Container(ContainerId, anonBytes: 400 * KiB, shmemBytes: 0, memoryCurrentBytes: 900 * KiB, systemdDriver: true);

        var containers = ContainerSetOf(tree);
        var processes = Collect(tree, containers);

        processes.ContainerProcesses.Should().Be(1);
        processes.ProcessCount.Should().Be(1, "the member of the counted container is not a distro process as well");
        processes.Top.Select(p => p.Pid).Should().Equal(10);
        processes.HeldBytesTotal.Should().Be(300 * KiB);
        Attribution.Compute(Reading.Of(1000 * KiB), Reading.Of(0L), Reading.Of(processes), Reading.Of(containers))
            .Should().Be(new Unattributed.Remainder(300 * KiB), "1 000 - 300 (process) - 400 (container, once)");
    }

    [Fact]
    public void A_container_process_whose_container_cgroup_cannot_be_read_is_counted_as_a_process_so_still_once()
    {
        using var tree = new SyntheticProcTree()
            .MemInfo(anonKib: 1000, shmemKib: 0)
            .Process(20, 1, $"/docker/{ContainerId}", rssAnonKib: 400);

        var containers = ContainerSetOf(tree);
        var processes = Collect(tree, containers);

        containers.Containers.Should().BeEmpty();
        processes.ContainerProcesses.Should().Be(0);
        processes.HeldBytesTotal.Should().Be(400 * KiB);
    }

    [Fact]
    public void The_captured_tree_attributes_a_positive_remainder_and_with_memory_current_it_would_not()
    {
        // The measured deviation of Attribution's remarks, on the fixture: anon + shmem keeps the units alike.
        var files = ProcfsFixture.LinkOverlay(new Core.Files.PhysicalFileSystem(ProcfsFixture.PathsAt(ProcfsFixture.Root)), ProcfsFixture.Root);
        var probe = new LinuxProbe(files, ProcfsFixture.PathsAt(ProcfsFixture.Root), new FixedTimeProvider(ProcfsFixture.CapturedAt));

        var vm = probe.Sample(CancellationToken.None).Vm.Should().BeOfType<Reading<VmSample>.Available>().Subject.Value;

        var containers = vm.Containers.ValueOr(new ContainerSet([]));
        var whole = (12_391_876 + 243_788) * KiB;
        var held = vm.Processes.ValueOr(Processes(0)).HeldBytesTotal;
        vm.Unattributed.Should().Be(new Unattributed.Remainder(whole - held - containers.AnonShmemTotal));
        (whole - held - containers.MemoryCurrentTotal).Should().BeNegative("memory.current charges page cache and kernel memory that AnonPages + Shmem do not hold");
    }

    private static ContainerSet ContainerSetOf(SyntheticProcTree tree) =>
        ContainerCgroups.Read(tree.Files, tree.Paths.CgroupRoot).ValueOr(new ContainerSet([]));

    private static ProcessSnapshot Collect(SyntheticProcTree tree) => Collect(tree, ContainerSetOf(tree));

    private static ProcessSnapshot Collect(SyntheticProcTree tree, ContainerSet containers) =>
        new ProcessCollector(tree.Files, tree.Paths, new FixedTimeProvider())
            .Read(Reading.Of(new Core.Collectors.Procfs.KernelFacts(100, 4096)), containers, CancellationToken.None)
            .Should().BeOfType<Reading<ProcessSnapshot>.Available>().Subject.Value;
}
