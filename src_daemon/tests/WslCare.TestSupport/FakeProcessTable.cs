using WslCare.Core.Actions.Engine;

namespace WslCare.TestSupport;

/// <summary>A process table a test scripts: every pid is gone unless the test says it is alive (or uninspectable); the
/// boot and its monotonic clock are unknown unless the test says which (E6.S0 review D1).</summary>
public sealed class FakeProcessTable : IProcessTable
{
    private readonly Dictionary<int, ProcessLookup> _pids = [];
    private BootClock _boot = BootClock.Unknown;

    public FakeProcessTable Alive(int pid, DateTimeOffset startUtc)
    {
        _pids[pid] = new ProcessLookup.Alive(startUtc);
        return this;
    }

    /// <summary>Alive, with the boot-relative start ticks a Linux reader takes from <c>/proc/[pid]/stat</c> — and a wall-clock
    /// start the test may SHIFT, as .NET's <c>Process.StartTime</c> shifts when the clock steps.</summary>
    public FakeProcessTable Alive(int pid, DateTimeOffset startUtc, long startTicks)
    {
        _pids[pid] = new ProcessLookup.Alive(startUtc) { StartTicks = startTicks };
        return this;
    }

    public FakeProcessTable Uninspectable(int pid, string reason)
    {
        _pids[pid] = new ProcessLookup.Unknown(reason);
        return this;
    }

    /// <summary>The boot this table answers for, and its monotonic clock now.</summary>
    public FakeProcessTable Booted(string bootId, long monotonicMilliseconds)
    {
        _boot = new BootClock(bootId, monotonicMilliseconds);
        return this;
    }

    public ProcessLookup Lookup(int pid) => _pids.GetValueOrDefault(pid) ?? new ProcessLookup.Gone();

    public BootClock Boot() => _boot;
}
