using WslCare.Core.Collectors;
using WslCare.Core.Hosting;

namespace WslCare.TestSupport;

/// <summary>A probe that answers one fixed sample — for tests about what a run RECORDS, where what it measured does
/// not matter. Every figure is unavailable with a reason that says so.</summary>
public sealed class FakeProbe(HostSide side, TimeProvider clock) : IHostProbe
{
    public const string Reason = "not sampled: a test probe";

    public HostSide Side => side;

    public int Samples { get; private set; }

    public ProbeSample Sample(CancellationToken cancellationToken)
    {
        Samples++;
        return new ProbeSample(side, clock.GetUtcNow(), TimeSpan.Zero, Reading.Missing<VmSample>(Reason), Reading.Missing<HostSample>(Reason));
    }
}
