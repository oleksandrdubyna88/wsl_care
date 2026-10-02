namespace WslCare.Core.Collectors;

/// <summary>
/// The memory no reader of this distro can name (plan §4.2 as amended by §15b #4): the VM's
/// <c>AnonPages</c> + <c>Shmem</c>, minus what the distro's processes hold, minus what the containers'
/// cgroups hold — the other distro (<c>Ubuntu-26.04</c>), the <c>docker-desktop</c> distro's own
/// processes, the kernel's share. Reported, never guessed.
/// </summary>
public abstract record Unattributed
{
    private Unattributed()
    {
    }

    /// <summary>The remainder, zero or more bytes.</summary>
    public sealed record Remainder(long Bytes) : Unattributed;

    /// <summary>The parts added up to MORE than the whole. A process's <c>RssShmem</c> counts a shared
    /// page in every process that maps it, and the figures are read one file at a time while memory
    /// moves, so this can happen; it is shown as an inconsistent sample — never as a negative number.</summary>
    public sealed record InconsistentSample(long OvershootBytes) : Unattributed;

    /// <summary>An input was not read, so no remainder exists.</summary>
    public sealed record NotComputed(string Reason) : Unattributed;
}

/// <summary>The arithmetic, as a pure function so its edges are unit tests.</summary>
/// <remarks>
/// <para><b>Each container is counted once, through its cgroup</b>: a process recognised by its cgroup
/// as a member of a counted container is excluded from the process sum (<see cref="ProcessSnapshot"/>),
/// so its pages are not subtracted twice.</para>
/// <para><b>Deviation from §4.2's wording, measured:</b> the container figure subtracted here is the
/// cgroup's <c>memory.stat</c> <c>anon</c> + <c>shmem</c>, not <c>memory.current</c>.
/// <c>memory.current</c> also charges the container's page cache and kernel memory, which are not in
/// <c>AnonPages</c> + <c>Shmem</c>; mixing the two units made the remainder negative on this machine
/// (2026-10-02, about 13:45 UTC, ten containers: AnonPages + Shmem 12.90 GiB, the distro's 135 processes
/// 9.67 GiB, the containers' <c>memory.current</c> 4.29 GiB → −1.05 GiB; with their anon + shmem,
/// 2.77 GiB → +0.46 GiB). Both figures are
/// reported per container; only the like-for-like one enters the subtraction.</para>
/// </remarks>
public static class Attribution
{
    public static Unattributed Compute(Reading<long> anonPages, Reading<long> shmem, Reading<ProcessSnapshot> processes, Reading<ContainerSet> containers)
    {
        var whole = Reading.Combine(anonPages, shmem, (a, s) => a + s);
        var parts = Reading.Combine(processes, containers, (p, c) => p.HeldBytesTotal + c.AnonShmemTotal);
        return Reading.Combine(whole, parts, (w, p) => w - p) switch
        {
            Reading<long>.Available { Value: >= 0 } ok => new Unattributed.Remainder(ok.Value),
            Reading<long>.Available negative => new Unattributed.InconsistentSample(-negative.Value),
            Reading<long>.Unavailable missing => new Unattributed.NotComputed(missing.Reason),
            _ => throw new System.Diagnostics.UnreachableException("Reading is a closed set"),
        };
    }
}
