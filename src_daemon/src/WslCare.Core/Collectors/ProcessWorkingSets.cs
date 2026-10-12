using System.ComponentModel;
using System.Globalization;

namespace WslCare.Core.Collectors;

/// <summary>
/// The summed working set of every process of one name, read from a listing the kernel already keeps (the Windows binary's
/// <c>vmmemWSL</c> and, since E15.S5, <c>Memory Compression</c>). Each reading fails ALONE: no such process, a process that
/// exits between the listing and the read, or a refused read makes THIS figure unavailable with the exception named — never the
/// whole sample. The processes listed are released however the read ends. PURE apart from the two functions it is handed.
/// </summary>
public static class ProcessWorkingSets
{
    /// <summary>The working sets summed, or why not.</summary>
    /// <param name="name">The process name, for the reason.</param>
    /// <param name="list">Lists the processes of that name (a snapshot: nothing is started).</param>
    /// <param name="workingSet">Reads one listed process's working set.</param>
    /// <param name="noneRunning">The reason when the listing holds no process of that name.</param>
    public static Reading<long> Sum<T>(string name, Func<IReadOnlyList<T>> list, Func<T, long> workingSet, string noneRunning)
        where T : IDisposable
    {
        IReadOnlyList<T> processes = [];
        try
        {
            processes = list();
            return processes.Count == 0 ? Reading.Missing<long>(noneRunning) : Reading.Of(processes.Sum(workingSet));
        }
        catch (Exception e) when (Unreadable(e))
        {
            return Reading.Missing<long>(string.Create(CultureInfo.InvariantCulture, $"the working set of {name} could not be read: {e.GetType().Name}: {e.Message}"));
        }
        finally
        {
            Release(processes);
        }
    }

    /// <summary>What a read of another process's figures can meet: it exited (<see cref="InvalidOperationException"/>), the read was
    /// refused (<see cref="Win32Exception"/>, <see cref="UnauthorizedAccessException"/>), or the figure is not offered here.</summary>
    private static bool Unreadable(Exception e) =>
        e is InvalidOperationException or Win32Exception or UnauthorizedAccessException or NotSupportedException;

    private static void Release<T>(IReadOnlyList<T> processes)
        where T : IDisposable
    {
        foreach (var process in processes)
        {
            process.Dispose();
        }
    }
}
