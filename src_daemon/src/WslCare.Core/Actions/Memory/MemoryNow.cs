using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Actions.Memory;

/// <summary>
/// The memory figures A1 and A2 decide and measure by — read NOW from <c>/proc/meminfo</c> and <c>/proc/buddyinfo</c>
/// through <see cref="IFileSystem"/>, by the SAME collector <c>status</c> and <c>collect</c> use (no process started).
/// </summary>
public static class MemoryNow
{
    /// <summary>The memory snapshot of this instant; unavailable with the reason when procfs cannot be read.</summary>
    public static Reading<MemorySnapshot> Read(ActionContext context) =>
        context.Paths is LinuxHostPaths linux
            ? new MemoryCollector(context.Files, linux).Read(ProcText.Bytes(context.Files, $"{linux.ProcRoot}/self/auxv").Bind(bytes => KernelFacts.FromAuxVector(bytes)))
            : Reading.Missing<MemorySnapshot>("memory is the WSL distro's");

    /// <summary>The free order-7 blocks of zone Normal (plan §4.1) — what A2 compacts for and measures.</summary>
    public static Reading<long> Order7Blocks(ActionContext context) =>
        Read(context).Bind(m => m.Fragmentation).Map(f => f.BlocksOrder7Plus);
}
