using System.Globalization;

namespace WslCare.TestSupport;

/// <summary>
/// Two SYNTHETIC memory states laid over a copy of the captured procfs tree (<see cref="ProcfsFixture.CopyTo"/>): only
/// <c>proc/meminfo</c>, <c>proc/buddyinfo</c> and <c>proc/pressure/</c> are rewritten (or removed), in the kernel's formats,
/// so no memory figure of 2026-10-02 is mixed into a state of another day. The processes, cgroups and auxiliary vector
/// stay the capture's. Labelled synthetic: they are test inputs, never a claim about what the machine measured.
/// </summary>
public static class ProcfsVariants
{
    private const long MemTotalKib = 47_066_772;
    private const long SwapTotalKib = 12_582_912;
    private const long PageKib = 4;

    /// <summary>
    /// 2026-10-01 18:36, from the dump in <c>research/2026-10-02_wsl_resource_baseline.md</c> (Finding 1): free 69 510
    /// pages, inactive anon 5 553 683, active anon 204 843, page cache 4 968 477 (written as <c>Cached</c>; the dump does not
    /// split <c>Buffers</c>, written 0), shmem 187 499, swap 2.2 of 12 GB, zone Normal <c>12594 5327 948 173 0 …</c> — no
    /// free block of 64 KiB or larger. <c>MemTotal</c> is the capture's (the same VM a day later). <b><c>MemAvailable</c> was
    /// not recorded and is LEFT OUT</b>: its verdict is <c>unknown</c>, never an invented figure.
    /// </summary>
    public static void October1Evening(string root)
    {
        var swapUsedKib = (long)(2.2 * 1024 * 1024);
        Write(root, "proc/meminfo", Lines(
            ("MemTotal", MemTotalKib),
            ("MemFree", 69_510 * PageKib),
            ("Buffers", 0),
            ("Cached", 4_968_477 * PageKib),
            ("Active(anon)", 204_843 * PageKib),
            ("Inactive(anon)", 5_553_683 * PageKib),
            ("SwapTotal", SwapTotalKib),
            ("SwapFree", SwapTotalKib - swapUsedKib),
            ("AnonPages", (5_553_683 + 204_843) * PageKib),
            ("Shmem", 187_499 * PageKib)));
        Write(root, "proc/buddyinfo", "Node 0, zone   Normal  12594   5327    948    173      0      0      0      0      0      0      0 \n");
        RemovePressure(root);
    }

    /// <summary>A fresh boot, INVENTED for the test (no capture of one exists): almost all of the VM free and available,
    /// little cache, no swap in use, thousands of free order-10 blocks, no pressure.</summary>
    public static void FreshBoot(string root)
    {
        Write(root, "proc/meminfo", Lines(
            ("MemTotal", MemTotalKib),
            ("MemFree", 45_100_000),
            ("MemAvailable", 45_900_000),
            ("Buffers", 60_000),
            ("Cached", 900_000),
            ("Active(anon)", 150_000),
            ("Inactive(anon)", 250_000),
            ("SwapTotal", SwapTotalKib),
            ("SwapFree", SwapTotalKib),
            ("AnonPages", 400_000),
            ("Shmem", 20_000)));
        Write(root, "proc/buddyinfo", "Node 0, zone   Normal     40     30     25     20     18     15     12     10      8      6  10900 \n");
        foreach (var resource in new[] { "memory", "io", "cpu" })
        {
            Write(root, $"proc/pressure/{resource}", "some avg10=0.00 avg60=0.00 avg300=0.00 total=0\nfull avg10=0.00 avg60=0.00 avg300=0.00 total=0\n");
        }
    }

    private static string Lines(params (string Key, long Kib)[] entries) =>
        string.Concat(entries.Select(e => string.Create(CultureInfo.InvariantCulture, $"{e.Key + ":",-16}{e.Kib,12} kB\n")));

    private static void Write(string root, string relative, string content) =>
        File.WriteAllText(Path.Combine(root, relative), content);

    private static void RemovePressure(string root)
    {
        var pressure = Path.Combine(root, "proc", "pressure");
        if (Directory.Exists(pressure))
        {
            Directory.Delete(pressure, recursive: true);
        }
    }
}
