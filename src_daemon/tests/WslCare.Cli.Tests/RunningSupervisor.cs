using WslCare.Core.Processes;

namespace WslCare.Cli.Tests;

/// <summary>
/// A supervisor for the tests of <c>tasks run</c>: records the launch, then runs its argv through the same program over the same host —
/// what the real worker would do, with no process and no job. Shared by the archive body's and the cleanup body's tests (WR-D5, WR-T2).
/// </summary>
internal sealed class RunningSupervisor(Func<CliHost> host) : IWorkerSupervisor
{
    public List<WorkerLaunch> Launches { get; } = [];

    public List<(int Exit, string Stdout, string Stderr)> Workers { get; } = [];

    public Action WhileRunning { get; init; } = static () => { };

    public WorkerOutcome Run(WorkerLaunch launch, CancellationToken cancellationToken)
    {
        Launches.Add(launch);
        WhileRunning();
        var worker = CliRun.Over(host(), Serilog.Core.Logger.None, cancellationToken, [.. launch.Arguments]);
        Workers.Add(worker);
        return new WorkerOutcome.Exited(worker.Exit);
    }
}
