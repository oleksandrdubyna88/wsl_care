using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Microsoft.Win32.SafeHandles;

using WslCare.Core.Collectors;

namespace WslCare.Core.Processes;

/// <summary>
/// The Win32 half of <see cref="JobSupervisor"/> (WR-D3, PLAN_windows_runner.md §3.3) — the ONLY file of the product that ends a
/// process through a job or by its handle, or creates one through <c>CreateProcessW</c> (<c>ArchitectureTests</c>' job scan). It
/// ends only what it created: the job it made, and the one worker it created suspended into it — never a process found by name or
/// by a pid read elsewhere.
/// </summary>
/// <remarks>
/// <para><b>The job</b> is made with an explicit NON-inheritable security attribute and the extended limit
/// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> alone: no <c>BREAKAWAY_OK</c>, no <c>SILENT_BREAKAWAY_OK</c>, so no child may leave it.</para>
/// <para><b>The worker</b> is created by its absolute path (<c>lpApplicationName</c>: no search), <c>CREATE_SUSPENDED |
/// CREATE_NO_WINDOW</c>, with the three standard handles inherited through <c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c> — inheritable
/// DUPLICATES of them, closed again once the worker exists; nothing else is inherited, the job's handle least of all. A standard
/// handle this process does not have is simply not passed.</para>
/// <para>Native AOT: every call is a source-generated <c>LibraryImport</c>; the structures are blittable.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class Win32JobControl : IJobControl
{
    private const string Kernel32 = "kernel32.dll";
    private const uint KillOnJobClose = 0x00002000;
    private const int ExtendedLimitInformationClass = 9;
    private const uint CreateSuspendedFlag = 0x00000004;
    private const uint CreateNoWindowFlag = 0x08000000;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const nint HandleListAttribute = 0x00020002;
    private const uint UseStdHandles = 0x00000100;
    private const uint DuplicateSameAccess = 0x00000002;
    private const uint ResumeFailed = 0xFFFFFFFF;
    private static readonly int[] StandardHandleIds = [-10, -11, -12];

    public Reading<IJobHandle> CreateJob()
    {
        // NON-inheritable, said explicitly: no process this one creates can hold the job's handle.
        var notInherited = new SecurityAttributes { Length = (uint)Marshal.SizeOf<SecurityAttributes>(), InheritHandle = 0 };
        var handle = CreateJobObject(ref notInherited, null);
        if (handle.IsInvalid)
        {
            return Failed<IJobHandle>("CreateJobObjectW", handle);
        }

        var limits = new ExtendedLimitInformation { Basic = new BasicLimitInformation { LimitFlags = KillOnJobClose } };
        return SetInformationJobObject(handle, ExtendedLimitInformationClass, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>())
            ? Reading.Of<IJobHandle>(new Job(handle))
            : Failed<IJobHandle>("SetInformationJobObject(KILL_ON_JOB_CLOSE)", handle);
    }

    public Reading<IWorkerProcess> StartSuspended(string executable, string commandLine) =>
        StandardCopies(InheritableCopyOf, CloseCopy) switch
        {
            Reading<nint[]>.Available standard => CreateClosing(executable, commandLine, standard.Value),
            Reading<nint[]>.Unavailable refused => Reading.Missing<IWorkerProcess>(refused.Reason),
            _ => throw new System.Diagnostics.UnreachableException("Reading is a closed set"),
        };

    public string Assign(IJobHandle job, IWorkerProcess worker) =>
        AssignProcessToJobObject(((Job)job).Handle, ((Worker)worker).Exit.SafeWaitHandle) ? string.Empty : Error("AssignProcessToJobObject");

    /// <summary>Resumes the worker's first thread ONLY from its suspended start: <c>ResumeThread</c> answers the previous suspend count,
    /// and anything but exactly 1 means the worker was not where the supervisor put it (m1 of the WR-D3 review).</summary>
    public string Resume(IWorkerProcess worker) => ResumeThread(((Worker)worker).Thread) switch
    {
        1 => string.Empty,
        ResumeFailed => Error("ResumeThread"),
        var previous => string.Create(CultureInfo.InvariantCulture, $"the worker was not in its suspended start (ResumeThread answered a previous suspend count of {previous}, not 1)"),
    };

    /// <summary>A failed wait (<c>WAIT_FAILED</c>) is NOT answered here: <see cref="WaitHandle.WaitAny(WaitHandle[], TimeSpan)"/> throws,
    /// the job's <c>using</c> closes it on the way out, and the program's last frame answers 70 <c>Internal</c> — fail closed.</summary>
    public WorkerWait Wait(IWorkerProcess worker, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var exit = ((Worker)worker).Exit;
        WaitHandle[] handles = cancellationToken.CanBeCanceled ? [exit, cancellationToken.WaitHandle] : [exit];
        return WaitHandle.WaitAny(handles, timeout) switch
        {
            0 => ExitOf(exit),
            WaitHandle.WaitTimeout => new WorkerWait.TimedOut(),
            _ => new WorkerWait.Cancelled(),
        };
    }

    public void EndJob(IJobHandle job, uint exitCode) =>
        // A failure here is answered by the close that always follows: the job is kill-on-close.
        _ = TerminateJobObject(((Job)job).Handle, exitCode);

    public string EndSuspended(IWorkerProcess worker, uint exitCode) =>
        TerminateProcess(((Worker)worker).Exit.SafeWaitHandle, exitCode) ? string.Empty : Error("TerminateProcess");

    /// <summary>The job's handle, for the Windows tests that read its limits and its inheritance back.</summary>
    internal sealed class Job(SafeWaitHandle handle) : IJobHandle
    {
        internal SafeWaitHandle Handle => handle;

        public void Dispose() => handle.Dispose();
    }

    /// <summary>The worker's process (as a waitable handle) and its first thread; disposing closes both handles.</summary>
    internal sealed class Worker(SafeWaitHandle process, SafeWaitHandle thread, int pid) : IWorkerProcess
    {
        public int Pid => pid;

        internal ProcessExit Exit { get; } = new(process);

        internal SafeWaitHandle Thread => thread;

        public void Dispose()
        {
            Exit.Dispose();
            thread.Dispose();
        }
    }

    /// <summary>A process handle as a <see cref="WaitHandle"/>: signalled when the process has exited.</summary>
    internal sealed class ProcessExit : WaitHandle
    {
        public ProcessExit(SafeWaitHandle process) => SafeWaitHandle = process;
    }

    private static Reading<IWorkerProcess> Create(string executable, string commandLine, nint[] standard)
    {
        var listed = standard.Where(h => h != 0).ToArray();
        using var attributes = AttributeList.For(listed);
        if (attributes.Problem.Length > 0)
        {
            return Reading.Missing<IWorkerProcess>(attributes.Problem);
        }

        var startup = StartupOf(standard, attributes.Pointer, listed.Length > 0);
        var line = (commandLine + '\0').ToCharArray();
        var flags = CreateSuspendedFlag | CreateNoWindowFlag | (listed.Length > 0 ? ExtendedStartupInfoPresent : 0);
        return CreateProcess(executable, line, 0, 0, listed.Length > 0, flags, 0, null, ref startup, out var created)
            ? Reading.Of<IWorkerProcess>(new Worker(new SafeWaitHandle(created.Process, ownsHandle: true), new SafeWaitHandle(created.Thread, ownsHandle: true), (int)created.ProcessId))
            : Reading.Missing<IWorkerProcess>(Error("CreateProcessW"));
    }

    private static StartupInfoEx StartupOf(nint[] standard, nint attributeList, bool inherits) => new()
    {
        StartupInfo = new StartupInfo
        {
            Size = (uint)(inherits ? Marshal.SizeOf<StartupInfoEx>() : Marshal.SizeOf<StartupInfo>()),
            Flags = inherits ? UseStdHandles : 0,
            StdInput = standard[0],
            StdOutput = standard[1],
            StdError = standard[2],
        },
        AttributeList = attributeList,
    };

    /// <summary>An inheritable duplicate of one standard handle; 0 when this process has none — or why it could not be duplicated.</summary>
    private static Reading<nint> InheritableCopyOf(int which)
    {
        var handle = GetStdHandle(which);
        if (handle is 0 or -1)
        {
            return Reading.Of<nint>(0);
        }

        var self = GetCurrentProcess();
        return DuplicateHandle(self, handle, self, out var copy, 0, inherit: true, DuplicateSameAccess) ? Reading.Of(copy) : Reading.Missing<nint>(Error("DuplicateHandle"));
    }

    /// <summary>The inheritable copies of stdin, stdout and stderr (0 = this process has none, which is simply not passed) — or, when one
    /// that exists could not be copied, the refusal (n1 of the WR-D3 review: fail closed, never a worker started without a stream it was
    /// meant to have), with every copy already made closed again through <paramref name="close"/>.</summary>
    internal static Reading<nint[]> StandardCopies(Func<int, Reading<nint>> copyOf, Action<nint> close)
    {
        var copies = StandardHandleIds.Select(copyOf).ToArray();
        if (copies.OfType<Reading<nint>.Unavailable>().FirstOrDefault() is not { } failed)
        {
            return Reading.Of(copies.OfType<Reading<nint>.Available>().Select(c => c.Value).ToArray());
        }

        foreach (var made in copies.OfType<Reading<nint>.Available>().Where(c => c.Value != 0))
        {
            close(made.Value);
        }

        return Reading.Missing<nint[]>($"a standard handle could not be passed to the worker ({failed.Reason}); nothing was started");
    }

    private static void CloseCopy(nint copy) => _ = CloseHandle(copy);

    /// <summary>The worker created, then the standard copies closed again — they were only for the worker to inherit.</summary>
    private static Reading<IWorkerProcess> CreateClosing(string executable, string commandLine, nint[] standard)
    {
        try
        {
            return Create(executable, commandLine, standard);
        }
        finally
        {
            foreach (var copy in standard.Where(h => h != 0))
            {
                CloseCopy(copy);
            }
        }
    }

    /// <summary>The worker exited: its code — or, when Windows will not say it, why (n2 of the WR-D3 review: a refusal, never a made-up
    /// code).</summary>
    private static WorkerWait ExitOf(ProcessExit exit) =>
        GetExitCodeProcess(exit.SafeWaitHandle, out var code) ? new WorkerWait.Exited(unchecked((int)code)) : new WorkerWait.Unreadable(Error("GetExitCodeProcess"));

    private static Reading<T> Failed<T>(string call, SafeHandle handle)
    {
        var error = Error(call);
        handle.Dispose();
        return Reading.Missing<T>(error);
    }

    private static string Error(string call)
    {
        var code = Marshal.GetLastPInvokeError();
        return string.Create(CultureInfo.InvariantCulture, $"{call} failed: error {code} ({new Win32Exception(code).Message})");
    }

    /// <summary>The <c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c> of the worker's start: the handles it may inherit, nothing else. Owns its
    /// unmanaged memory, which must outlive <c>CreateProcessW</c>.</summary>
    private sealed class AttributeList : IDisposable
    {
        private readonly nint _handles;

        private AttributeList(nint pointer, nint handles, string problem)
        {
            Pointer = pointer;
            _handles = handles;
            Problem = problem;
        }

        public nint Pointer { get; }

        public string Problem { get; }

        public static AttributeList For(nint[] handles)
        {
            if (handles.Length == 0)
            {
                return new(0, 0, string.Empty);
            }

            nuint size = 0;
            _ = InitializeProcThreadAttributeList(0, 1, 0, ref size);
            var list = Marshal.AllocHGlobal((nint)size);
            var array = Marshal.AllocHGlobal(IntPtr.Size * handles.Length);
            Marshal.Copy(handles, 0, array, handles.Length);
            return Filled(list, array, handles.Length, size);
        }

        private static AttributeList Filled(nint list, nint array, int count, nuint size)
        {
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref size))
            {
                var problem = Error("InitializeProcThreadAttributeList");
                Marshal.FreeHGlobal(list);
                Marshal.FreeHGlobal(array);
                return new(0, 0, problem);
            }

            var updated = UpdateProcThreadAttribute(list, 0, HandleListAttribute, array, (nuint)(IntPtr.Size * count), 0, 0);
            return new(list, array, updated ? string.Empty : Error("UpdateProcThreadAttribute(HANDLE_LIST)"));
        }

        public void Dispose()
        {
            if (Pointer != 0)
            {
                DeleteProcThreadAttributeList(Pointer);
                Marshal.FreeHGlobal(Pointer);
            }

            if (_handles != 0)
            {
                Marshal.FreeHGlobal(_handles);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;
        public nint SecurityDescriptor;
        public int InheritHandle;
    }

    /// <summary><c>JOBOBJECT_BASIC_LIMIT_INFORMATION</c>, field for field.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    /// <summary><c>IO_COUNTERS</c>, field for field.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    /// <summary><c>JOBOBJECT_EXTENDED_LIMIT_INFORMATION</c>, field for field.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public IoCounters Io;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    /// <summary><c>STARTUPINFOW</c>, field for field.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Size;
        public nint Reserved2;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
    }

    /// <summary><c>STARTUPINFOEXW</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint AttributeList;
    }

    /// <summary><c>PROCESS_INFORMATION</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [LibraryImport(Kernel32, EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeWaitHandle CreateJobObject(ref SecurityAttributes attributes, string? name);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(SafeWaitHandle job, int informationClass, ref ExtendedLimitInformation information, uint length);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeWaitHandle job, SafeWaitHandle process);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(SafeWaitHandle job, uint exitCode);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(SafeWaitHandle process, uint exitCode);

    [LibraryImport(Kernel32, SetLastError = true)]
    private static partial uint ResumeThread(SafeWaitHandle thread);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(SafeWaitHandle process, out uint exitCode);

    [LibraryImport(Kernel32, SetLastError = true)]
    private static partial nint GetStdHandle(int which);

    [LibraryImport(Kernel32)]
    private static partial nint GetCurrentProcess();

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateHandle(nint sourceProcess, nint source, nint targetProcess, out nint target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nuint size);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint value, nuint size, nint previous, nint returnSize);

    [LibraryImport(Kernel32)]
    private static partial void DeleteProcThreadAttributeList(nint list);

    [LibraryImport(Kernel32, EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcess(string applicationName, char[] commandLine, nint processAttributes, nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment, string? currentDirectory, ref StartupInfoEx startupInfo, out ProcessInformation processInformation);
}
