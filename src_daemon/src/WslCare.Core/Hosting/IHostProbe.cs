using WslCare.Core.Collectors;

namespace WslCare.Core.Hosting;

/// <summary>
/// The platform split of plan §8: what one side of the machine can observe. <c>Collectors.LinuxProbe</c>
/// reads <c>/proc</c> and the cgroup tree; <c>Collectors.WindowsProbe</c> reads host RAM, the system drive and
/// <c>vmmemWSL</c>. One binary per RID, so a binary answers for its own side and names the other side's
/// binary as the reason it does not.
/// </summary>
/// <remarks><see cref="Sample"/> is the FAST sample of <c>status</c> (plan §6, §15b #5): it reads files and
/// asks the operating system for counters, and it starts no process — a probe holds no
/// <c>ICommandRunner</c>, so it cannot. The slow parts (<c>docker stats</c>, the Windows clock) belong to
/// <c>collect</c>, and <c>status</c> reports them from the last full run.</remarks>
public interface IHostProbe
{
    HostSide Side { get; }

    ProbeSample Sample(CancellationToken cancellationToken);
}
