using System.Runtime.Versioning;

using WslCare.Core.Mcp;

namespace WslCare.Core.Archive;

/// <summary>What the Windows side's open-file check is asked of (E9.S5): the Restart Manager and the process table — the real ones in the
/// Windows binary's host, a test's own anywhere else.</summary>
public interface IWindowsSide
{
    /// <param name="budget">The time the caller has for every question of the view; each question also within
    /// <see cref="InUse.Ceiling"/>.</param>
    InUseView View(TimeSpan budget, CancellationToken cancellationToken);
}

/// <summary>The real Windows side: the Restart Manager and this machine's process table.</summary>
[SupportedOSPlatform("windows")]
public sealed class RealWindowsSide(IWindowsProcessTable processes) : IWindowsSide
{
    public InUseView View(TimeSpan budget, CancellationToken cancellationToken) =>
        InUseWindows.View(new RestartManager(), processes, new WindowsAsk(InUse.Ceiling, budget, cancellationToken));
}

/// <summary>The side a host off Windows — or a test that reads no Windows — holds: the check did not run, said so, so nothing moves.</summary>
public sealed class UncheckedWindowsSide(string why) : IWindowsSide
{
    public static UncheckedWindowsSide NotWindows { get; } = new("this process is not on Windows, so the Restart Manager cannot be asked");

    public InUseView View(TimeSpan budget, CancellationToken cancellationToken) => InUseView.NotChecked(why);
}
