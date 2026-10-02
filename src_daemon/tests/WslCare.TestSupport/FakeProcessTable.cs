using WslCare.Core.Actions.Engine;

namespace WslCare.TestSupport;

/// <summary>A process table a test scripts: every pid is gone unless the test says it is alive (or uninspectable).</summary>
public sealed class FakeProcessTable : IProcessTable
{
    private readonly Dictionary<int, ProcessLookup> _pids = [];

    public FakeProcessTable Alive(int pid, DateTimeOffset startUtc)
    {
        _pids[pid] = new ProcessLookup.Alive(startUtc);
        return this;
    }

    public FakeProcessTable Uninspectable(int pid, string reason)
    {
        _pids[pid] = new ProcessLookup.Unknown(reason);
        return this;
    }

    public ProcessLookup Lookup(int pid) => _pids.GetValueOrDefault(pid) ?? new ProcessLookup.Gone();
}
