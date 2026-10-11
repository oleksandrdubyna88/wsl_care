namespace WslCare.TestSupport;

/// <summary>
/// A directory of this test's own under the system temp folder, removed on dispose. Everything a
/// test writes lives under it; nothing real is ever touched.
/// </summary>
/// <remarks>Deleting it here with <c>Directory.Delete</c> is fine: this is test support, not
/// <c>src_daemon/src</c>, so the architecture test does not reach it — and the deletion policy would
/// have nothing to say about a folder the test created a second ago.</remarks>
public sealed class TempRoot : IDisposable
{
    public TempRoot(string purpose)
    {
        _ = Swept.Value;
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"wsl-care-test-{purpose}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
        System.IO.File.WriteAllText(Path + MarkerSuffix, MarkerOf(Environment.ProcessId, ThisProcessStartTicks.Value));
    }

    private static readonly Lazy<long> ThisProcessStartTicks = new(() => System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks);

    /// <summary>The absolute path of this test's root.</summary>
    public string Path { get; }

    /// <summary>How old a root left behind by an EARLIER test process must be before a later one removes it.</summary>
    public static readonly TimeSpan LeftoverAge = TimeSpan.FromHours(24);

    /// <summary>The marker file beside a root (never inside it, so no test that lists its root sees it).</summary>
    public const string MarkerSuffix = ".owner";

    /// <summary>A marker's text: the creating process's pid and start time (UTC ticks).</summary>
    public static string MarkerOf(int pid, long startTicksUtc) => FormattableString.Invariant($"{pid} {startTicksUtc}");

    /// <summary>The exact shape the constructor writes: the prefix, a purpose, a dash and 32 hex digits.</summary>
    private static readonly System.Text.RegularExpressions.Regex RootName =
        new("^wsl-care-test-.+-[0-9a-f]{32}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The leftovers of earlier processes, removed once, by the first root this process makes.</summary>
    private static readonly Lazy<int> Swept = new(() => SweepLeftovers(System.IO.Path.GetTempPath(), DateTime.UtcNow, LeftoverAge));

    /// <summary>
    /// Removes the roots in <paramref name="tempFolder"/> that an earlier test process left behind — only this harness's exact
    /// names, only when last written before <paramref name="olderThan"/> ago, and never one whose marker names a process that is
    /// still alive (pid AND start time: age alone does not prove a root abandoned — the coai plan round) — and answers how many
    /// went. A root it cannot remove (a handle still open) stays for a later sweep; nothing else in the folder is ever touched.
    /// A root from before the markers has none and is judged by its age alone.
    /// </summary>
    public static int SweepLeftovers(string tempFolder, DateTime nowUtc, TimeSpan olderThan) =>
        new DirectoryInfo(tempFolder).EnumerateDirectories()
            .Where(d => RootName.IsMatch(d.Name) && nowUtc - d.LastWriteTimeUtc > olderThan && !OwnerAlive(d.FullName + MarkerSuffix))
            .Count(d => DeleteRootAndMarker(d.FullName));

    /// <summary>Whether the marker names a process that still runs: the same pid with the same start time. A process whose start
    /// time cannot be read is taken as alive (keep rather than guess); a missing or unreadable marker names nobody.</summary>
    private static bool OwnerAlive(string marker)
    {
        var parts = System.IO.File.Exists(marker) ? System.IO.File.ReadAllText(marker).Split(' ') : [];
        return parts.Length == 2
            && int.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var pid)
            && long.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var ticks)
            && StartTicksOf(pid) is { } live && (live == ticks || live == UnreadableStart);
    }

    private const long UnreadableStart = -1;

    /// <summary>The start time of the running process <paramref name="pid"/>; <c>null</c> when no such process runs.</summary>
    private static long? StartTicksOf(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return StartOrUnreadable(process);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static long StartOrUnreadable(System.Diagnostics.Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return UnreadableStart;
        }
    }

    /// <summary>The root, then — only once it is gone — its marker.</summary>
    private static bool DeleteRootAndMarker(string root)
    {
        var gone = DeleteBestEffort(root);
        if (gone)
        {
            DeleteMarker(root);
        }

        return gone;
    }

    private static void DeleteMarker(string root)
    {
        try
        {
            System.IO.File.Delete(root + MarkerSuffix);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A marker without its root names nothing that can be swept; it goes with a later run's delete or stays harmless.
        }
    }

    public string File(string relativePath, string content)
    {
        var full = Under(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        return full;
    }

    public string Dir(string relativePath)
    {
        var full = Under(relativePath);
        Directory.CreateDirectory(full);
        return full;
    }

    public string Under(string relativePath) =>
        System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public void Dispose() => DeleteRootAndMarker(Path);

    /// <summary>
    /// Best effort, twice: on Windows a recursive delete over a tree holding a junction removes the
    /// junction and STILL throws <see cref="UnauthorizedAccessException"/> for it (measured 2026-10-02),
    /// so the second pass finishes what the first one started. A handle still open elsewhere leaves the
    /// folder to a later test process's <see cref="SweepLeftovers"/> — a test must never fail on its own cleanup.
    /// Answers whether the folder is gone.
    /// </summary>
    private static bool DeleteBestEffort(string path)
    {
        for (var attempt = 0; attempt < 2 && Directory.Exists(path); attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Retried once; then left to a later sweep.
            }
        }

        return !Directory.Exists(path);
    }
}
