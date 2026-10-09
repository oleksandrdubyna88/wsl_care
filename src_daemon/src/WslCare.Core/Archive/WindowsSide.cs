using System.Runtime.Versioning;

using WslCare.Core.Mcp;

namespace WslCare.Core.Archive;

/// <summary>What the Windows side's open-file check is asked of (E9.S5): the Restart Manager and the process table — the real ones in the
/// Windows binary's host, a test's own anywhere else.</summary>
public interface IWindowsSide
{
    InUseView View(TimeSpan ceiling);
}

/// <summary>The real Windows side: the Restart Manager and this machine's process table.</summary>
[SupportedOSPlatform("windows")]
public sealed class RealWindowsSide(IWindowsProcessTable processes) : IWindowsSide
{
    public InUseView View(TimeSpan ceiling) => InUseWindows.View(new RestartManager(), processes, ceiling);
}

/// <summary>The side a host off Windows — or a test that reads no Windows — holds: the check did not run, said so, so nothing moves.</summary>
public sealed class UncheckedWindowsSide(string why) : IWindowsSide
{
    public static UncheckedWindowsSide NotWindows { get; } = new("this process is not on Windows, so the Restart Manager cannot be asked");

    public InUseView View(TimeSpan ceiling) => InUseView.NotChecked(why);
}
