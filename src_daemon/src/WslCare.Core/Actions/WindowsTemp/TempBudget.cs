namespace WslCare.Core.Actions.WindowsTemp;

/// <summary>What one run may spend scanning: a number of entries (<c>temp.scanMaxNodes</c>) and a time (<c>temp.scanMaxSeconds</c>).</summary>
internal sealed class TempBudget(int maxNodes, TimeSpan maxTime, TimeProvider clock)
{
    private readonly long _start = clock.GetTimestamp();
    private int _used;

    public int Remaining => Math.Max(0, maxNodes - _used);

    public bool OutOfTime() => clock.GetElapsedTime(_start) >= maxTime;

    public bool Exhausted => Remaining == 0 || OutOfTime();

    public void Spend(int nodes) => _used += nodes;
}
