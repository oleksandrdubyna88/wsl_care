using System.Text;
using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Actions.Engine;
using WslCare.Core.Archive;
using WslCare.Core.Files;
using WslCare.Core.Json;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Archive;

/// <summary>
/// Plan §15r D4, review M1 — one writer per side: the lease file on the base. Another host's lease refuses the run; a live process of
/// this host and boot refuses it; a dead one's lease is taken over and said; the lease is removed when the run ends.
/// </summary>
public sealed class SideLeaseTests : IDisposable
{
    private const string Base = "/mnt/v/ai-archive";
    private const string Side = "wsl-host-distro";
    private static readonly DateTimeOffset Since = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly LinuxSandbox _sandbox = new("archive-lease");

    public SideLeaseTests() => Directory.CreateDirectory(On(Base));

    public void Dispose() => _sandbox.Dispose();

    private string On(string distro) => Path.GetFullPath(_sandbox.Paths.DistroPath(distro));

    private string LeaseFile => On($"{Base}/.wsl-care/sides/{Side}.lease");

    private static LeaseRecord Me => new(1, "host", "boot-1", 4242, 1000, Since, "r2", Since);

    private void Planted(LeaseRecord holder)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LeaseFile)!);
        File.WriteAllText(LeaseFile, JsonSerializer.Serialize(holder, WslCareJsonContext.Compact.LeaseRecord), new UTF8Encoding(false));
    }

    private LeaseTaken Take(IProcessTable processes) => SideLease.Take(_sandbox.Files, On(Base), Side, Me, processes);

    [Fact]
    public void A_free_side_is_leased_and_the_lease_goes_when_the_run_ends()
    {
        var held = Take(new ScriptedProcessTable()).Should().BeOfType<LeaseTaken.Held>().Subject;
        File.ReadAllText(LeaseFile).Should().Contain("\"runId\":\"r2\"");

        SideLease.Release(_sandbox.Files, held, On(Base));

        File.Exists(LeaseFile).Should().BeFalse();
    }

    [Fact]
    public void A_lease_held_by_another_host_refuses_the_run()
    {
        Planted(Me with { Host = "the-other-pc", Pid = 7, RunId = "r1" });

        Take(new ScriptedProcessTable()).Should().BeOfType<LeaseTaken.Refused>().Which.Why.Should().Contain("another host");

        File.ReadAllText(LeaseFile).Should().Contain("the-other-pc", "another host's lease is never judged dead from here");
    }

    [Fact]
    public void A_lease_a_live_run_of_this_host_holds_refuses_the_run()
    {
        Planted(Me with { Pid = 7, StartTicks = 500, RunId = "r1" });
        var processes = new ScriptedProcessTable { [7] = new ProcessLookup.Alive(Since) { StartTicks = 500 } };

        Take(processes).Should().BeOfType<LeaseTaken.Refused>().Which.Why.Should().Contain("another archive run");
    }

    [Fact]
    public void A_lease_of_a_dead_run_of_this_host_and_boot_or_of_an_earlier_boot_is_taken_over()
    {
        Planted(Me with { Pid = 7, StartTicks = 500, RunId = "r1" });
        var reused = new ScriptedProcessTable { [7] = new ProcessLookup.Alive(Since) { StartTicks = 999 } };

        var held = Take(reused).Should().BeOfType<LeaseTaken.Held>().Subject;
        held.Note.Should().Contain("taken over");
        File.ReadAllText(LeaseFile).Should().Contain("\"runId\":\"r2\"");
        SideLease.Release(_sandbox.Files, held, On(Base));

        Planted(Me with { BootId = "boot-0", Pid = 7, RunId = "r0" });
        Take(new ScriptedProcessTable { [7] = new ProcessLookup.Alive(Since) { StartTicks = 1000 } }).Should().BeOfType<LeaseTaken.Held>("a lease of an earlier boot is dead whatever pid it names");
    }

    /// <summary>A process table answering from a script; an unscripted pid is gone.</summary>
    private sealed class ScriptedProcessTable : IProcessTable
    {
        private readonly Dictionary<int, ProcessLookup> _pids = [];

        public ProcessLookup this[int pid]
        {
            set => _pids[pid] = value;
        }

        public ProcessLookup Lookup(int pid) => _pids.TryGetValue(pid, out var found) ? found : new ProcessLookup.Gone();
    }
}
