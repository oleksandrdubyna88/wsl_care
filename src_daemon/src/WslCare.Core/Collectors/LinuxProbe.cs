using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Collectors;

/// <summary>
/// The distro side's fast sample: memory (§4.1), who holds it (§4.2 with §15b #4), the containers'
/// cgroups, the unattributed remainder, and <c>df /</c> (§4.4) — every read through
/// <see cref="IFileSystem"/> under the roots <see cref="LinuxHostPaths"/> names, so a test or a scenario
/// points the whole probe at a captured fixture tree.
/// </summary>
public sealed class LinuxProbe(IFileSystem files, LinuxHostPaths paths, TimeProvider clock) : IHostProbe
{
    /// <summary>Why the Windows-side figures are not in the Linux binary's answer.</summary>
    public const string HostIsTheOtherBinary =
        "Windows-side figures (host RAM, C:, vmmemWSL, the .vhdx files) are read by wsl-care.exe on Windows, not from inside the VM";

    public HostSide Side => HostSide.Wsl;

    public ProbeSample Sample(CancellationToken cancellationToken)
    {
        var started = clock.GetTimestamp();
        var sampledAt = clock.GetUtcNow();
        var kernel = ProcText.Bytes(files, $"{paths.ProcRoot}/self/auxv").Bind(bytes => KernelFacts.FromAuxVector(bytes));
        var memory = new MemoryCollector(files, paths).Read(kernel);
        var containers = ContainerCgroups.Read(files, paths.CgroupRoot);
        var processes = new ProcessCollector(files, paths, clock).Read(kernel, containers.ValueOr(new ContainerSet([])), cancellationToken);
        var unattributed = Attribution.Compute(memory.Bind(m => m.AnonPages), memory.Bind(m => m.Shmem), processes, containers);
        var vm = new VmSample(memory, processes, containers, unattributed, VolumeUsage.Measure(files, paths.FilesystemRoot));
        return new ProbeSample(Side, sampledAt, clock.GetElapsedTime(started), Reading.Of(vm), Reading.Missing<HostSample>(HostIsTheOtherBinary));
    }
}
