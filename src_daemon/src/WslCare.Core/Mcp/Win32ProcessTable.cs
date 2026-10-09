using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using WslCare.Core.Collectors;

namespace WslCare.Core.Mcp;

/// <summary>
/// The real Windows process table (E14 S7a) — READ-ONLY by construction: <c>CreateToolhelp32Snapshot</c> names every process (pid,
/// parent pid, exe name) in one kernel snapshot, starting nothing; <see cref="Details"/> opens ONE process with
/// <c>PROCESS_QUERY_LIMITED_INFORMATION</c> — the query right only, never terminate, write or read memory — reads its times and
/// memory counters (and, for the archive's E9.S5, its command line) and closes the handle. This type imports no call that stops,
/// suspends or changes a process.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class Win32ProcessTable : IWindowsProcessTable
{
    private const uint SnapProcess = 0x00000002;
    private const uint QueryLimitedInformation = 0x00001000;
    private const int AccessDenied = 5;
    private const int InvalidParameter = 87;
    private const int NoMoreFiles = 18;
    private const int ProcessCommandLineInformation = 60;
    private static readonly nint InvalidHandle = -1;

    public Reading<IReadOnlyList<WindowsProcessEntry>> List()
    {
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == InvalidHandle)
        {
            return Reading.Missing<IReadOnlyList<WindowsProcessEntry>>(Failed("CreateToolhelp32Snapshot", Marshal.GetLastPInvokeError()));
        }

        try
        {
            return Walk(snapshot);
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    public WindowsProcessDetails Details(int pid)
    {
        var session = ProcessIdToSessionId((uint)pid, out var id) ? Reading.Of((int)id) : Reading.Missing<int>(Failed("ProcessIdToSessionId", Marshal.GetLastPInvokeError()));
        var handle = OpenProcess(QueryLimitedInformation, false, (uint)pid);
        if (handle == 0)
        {
            return WindowsProcessDetails.Unopenable(Unopenable(pid, Marshal.GetLastPInvokeError())) with { SessionId = session };
        }

        try
        {
            var (created, cpu) = Times(handle);
            var (workingSet, privateBytes) = Memory(handle);
            return new WindowsProcessDetails(created, cpu, workingSet, privateBytes, session);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static Reading<IReadOnlyList<WindowsProcessEntry>> Walk(nint snapshot)
    {
        var entry = new ProcessEntry32 { Size = (uint)Unsafe.SizeOf<ProcessEntry32>() };
        var entries = new List<WindowsProcessEntry>();
        for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
        {
            entries.Add(new WindowsProcessEntry((int)entry.ProcessId, (int)entry.ParentProcessId, entry.ExeName()));
        }

        var error = Marshal.GetLastPInvokeError();
        return error == NoMoreFiles
            ? Reading.Of<IReadOnlyList<WindowsProcessEntry>>(entries)
            : Reading.Missing<IReadOnlyList<WindowsProcessEntry>>(Failed("Process32NextW", error));
    }

    /// <summary>E9.S5: the command line of ONE process — <c>NtQueryInformationProcess(ProcessCommandLineInformation)</c> through the
    /// same query-only handle (<c>PROCESS_QUERY_LIMITED_INFORMATION</c>); never its memory.</summary>
    public Reading<string> CommandLine(int pid)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, (uint)pid);
        if (handle == 0)
        {
            return Reading.Missing<string>(Unopenable(pid, Marshal.GetLastPInvokeError()));
        }

        try
        {
            return CommandLineOf(handle);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>Asked once for its size, then into a buffer of it: a <c>UNICODE_STRING</c> whose characters follow it.</summary>
    private static Reading<string> CommandLineOf(nint handle)
    {
        _ = NtQueryInformationProcess(handle, ProcessCommandLineInformation, 0, 0, out var needed);
        if (needed <= 0)
        {
            return Reading.Missing<string>(Invariant($"NtQueryInformationProcess named no size ({needed})"));
        }

        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            var status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, needed, out _);
            return status == 0 ? Reading.Of(UnicodeString(buffer)) : Reading.Missing<string>(Invariant($"NtQueryInformationProcess failed with NTSTATUS 0x{status:X8}"));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary><c>UNICODE_STRING</c>: its length in BYTES, then (aligned) the pointer to its characters.</summary>
    private static string UnicodeString(nint buffer)
    {
        var bytes = Marshal.ReadInt16(buffer);
        var characters = Marshal.ReadIntPtr(buffer, nint.Size);
        return characters == 0 || bytes <= 0 ? string.Empty : Marshal.PtrToStringUni(characters, (ushort)bytes / sizeof(char)) ?? string.Empty;
    }

    private static (Reading<DateTimeOffset> Created, Reading<TimeSpan> Cpu) Times(nint handle)
    {
        if (!GetProcessTimes(handle, out var creation, out _, out var kernel, out var user))
        {
            var why = Failed("GetProcessTimes", Marshal.GetLastPInvokeError());
            return (Reading.Missing<DateTimeOffset>(why), Reading.Missing<TimeSpan>(why));
        }

        // FILETIMEs are 100 ns units: the creation time since 1601 UTC, the CPU times as durations — TimeSpan ticks exactly.
        return (Reading.Of(new DateTimeOffset(DateTime.FromFileTimeUtc(creation))), Reading.Of(TimeSpan.FromTicks(kernel + user)));
    }

    private static (Reading<long> WorkingSet, Reading<long> PrivateBytes) Memory(nint handle)
    {
        var counters = new ProcessMemoryCountersEx { Size = (uint)Unsafe.SizeOf<ProcessMemoryCountersEx>() };
        if (!K32GetProcessMemoryInfo(handle, ref counters, counters.Size))
        {
            var why = Failed("K32GetProcessMemoryInfo", Marshal.GetLastPInvokeError());
            return (Reading.Missing<long>(why), Reading.Missing<long>(why));
        }

        return (Reading.Of((long)counters.WorkingSetSize), Reading.Of((long)counters.PrivateUsage));
    }

    private static string Unopenable(int pid, int error) => error switch
    {
        AccessDenied => Invariant($"OpenProcess({pid}) failed: access denied (another account's or a protected process)"),
        InvalidParameter => Invariant($"OpenProcess({pid}) failed: no such process (it exited)"),
        _ => Failed(Invariant($"OpenProcess({pid})"), error),
    };

    private static string Failed(string call, int error) => Invariant($"{call} failed with Win32 error {error}");

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(nint process, int informationClass, nint information, int length, out int returned);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32FirstW(nint snapshot, ref ProcessEntry32 entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32NextW(nint snapshot, ref ProcessEntry32 entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(nint process, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool K32GetProcessMemoryInfo(nint process, ref ProcessMemoryCountersEx counters, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    /// <summary><c>PROCESSENTRY32W</c>, field for field; the name is 260 UTF-16 units, NUL-terminated.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        public ExeFileName ExeFile;

        public readonly string ExeName()
        {
            ReadOnlySpan<char> name = MemoryMarshal.Cast<ushort, char>((ReadOnlySpan<ushort>)ExeFile);
            var end = name.IndexOf('\0');
            return new string(end < 0 ? name : name[..end]);
        }
    }

    [InlineArray(260)]
    private struct ExeFileName
    {
        private ushort _first;
    }

    /// <summary><c>PROCESS_MEMORY_COUNTERS_EX</c>, field for field.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx
    {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
    }
}
