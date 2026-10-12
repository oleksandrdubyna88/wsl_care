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
/// CREATE_NO_WINDOW</c>, ALREADY INSIDE the job through <c>PROC_THREAD_ATTRIBUTE_JOB_LIST</c> (never outside it, not for a moment;
/// <c>IsProcessInJob</c> verifies it before the resume), with the three standard handles inherited through
/// <c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c> — inheritable
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
    private const nint JobListAttribute = 0x0002000D;
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

    public Reading<IWorkerProcess> StartSuspended(IJobHandle job, string executable, string commandLine) =>
        StandardCopies(InheritableCopyOf, CloseCopy) switch
        {
            Reading<nint[]>.Available standard => CreateClosing(executable, commandLine, standard.Value, ((Job)job).Handle),
            Reading<nint[]>.Unavailable refused => Reading.Missing<IWorkerProcess>(refused.Reason),
            _ => throw new System.Diagnostics.UnreachableException("Reading is a closed set"),
        };

    /// <summary>The membership check before the resume (<c>IsProcessInJob</c>, read-only): the worker was created in the job, and this
    /// verifies it rather than trusting it. There is no assign step any more — the coai code round on WR-D3, finding 1.</summary>
    public string InJob(IJobHandle job, IWorkerProcess worker) =>
        IsProcessInJob(((Worker)worker).Exit.SafeWaitHandle, ((Job)job).Handle, out var inJob) switch
        {
            false => Error("IsProcessInJob"),
            true when !inJob => "IsProcessInJob answered false: the worker is not in its job",
            _ => string.Empty,
        };

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

    /// <summary>The worker created suspended and ALREADY IN the job whose raw handle is <paramref name="job"/>: the job goes in the start's
    /// <c>PROC_THREAD_ATTRIBUTE_JOB_LIST</c> (the handle is not inherited for that — the list needs no inheritance), so Windows makes
    /// the process a member before its first instruction. When Windows refuses (a parent job that forbids nesting, an older Windows),
    /// <c>CreateProcessW</c> fails and nothing was started.</summary>
    private static Reading<IWorkerProcess> Create(string executable, string commandLine, nint[] standard, nint job)
    {
        var listed = standard.Where(h => h != 0).ToArray();
        using var attributes = AttributeList.For(listed, job);
        if (attributes.Problem.Length > 0)
        {
            return Reading.Missing<IWorkerProcess>(attributes.Problem);
        }

        var startup = StartupOf(standard, attributes.Pointer, listed.Length > 0);
        var line = (commandLine + '\0').ToCharArray();
        return CreateProcess(executable, line, 0, 0, listed.Length > 0, CreateSuspendedFlag | CreateNoWindowFlag | ExtendedStartupInfoPresent, 0, null, ref startup, out var created)
            ? Reading.Of<IWorkerProcess>(new Worker(new SafeWaitHandle(created.Process, ownsHandle: true), new SafeWaitHandle(created.Thread, ownsHandle: true), (int)created.ProcessId))
            : Reading.Missing<IWorkerProcess>(Error("CreateProcessW (suspended, inside its job)"));
    }

    private static StartupInfoEx StartupOf(nint[] standard, nint attributeList, bool inherits) => new()
    {
        StartupInfo = new StartupInfo
        {
            Size = (uint)Marshal.SizeOf<StartupInfoEx>(),
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

    /// <summary>The worker created inside <paramref name="job"/>, then the standard copies closed again — they were only for the worker to
    /// inherit. The job's handle is held (add-ref) while its raw value sits in the attribute list.</summary>
    private static Reading<IWorkerProcess> CreateClosing(string executable, string commandLine, nint[] standard, SafeWaitHandle job)
    {
        var held = false;
        try
        {
            job.DangerousAddRef(ref held);
            return Create(executable, commandLine, standard, job.DangerousGetHandle());
        }
        finally
        {
            if (held)
            {
                job.DangerousRelease();
            }

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

    /// <summary>The worker's start attributes: <c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c> — the handles it may inherit, nothing else (left
    /// out when there are none) — and <c>PROC_THREAD_ATTRIBUTE_JOB_LIST</c> — the one job it is created in. Owns its unmanaged memory
    /// (the list, then the handles followed by the job), which must outlive <c>CreateProcessW</c>.</summary>
    private sealed class AttributeList : IDisposable
    {
        private readonly nint _values;

        private AttributeList(nint pointer, nint values, string problem)
        {
            Pointer = pointer;
            _values = values;
            Problem = problem;
        }

        public nint Pointer { get; }

        public string Problem { get; }

        public static AttributeList For(nint[] handles, nint job)
        {
            var count = handles.Length > 0 ? 2 : 1;
            nuint size = 0;
            _ = InitializeProcThreadAttributeList(0, count, 0, ref size);
            var list = Marshal.AllocHGlobal((nint)size);
            var values = Marshal.AllocHGlobal(IntPtr.Size * (handles.Length + 1));
            Marshal.Copy(handles, 0, values, handles.Length);
            Marshal.WriteIntPtr(values, IntPtr.Size * handles.Length, job);
            if (!InitializeProcThreadAttributeList(list, count, 0, ref size))
            {
                var problem = Error("InitializeProcThreadAttributeList");
                Marshal.FreeHGlobal(list);
                Marshal.FreeHGlobal(values);
                return new(0, 0, problem);
            }

            return new(list, values, Filled(list, values, handles.Length));
        }

        /// <summary>The two attributes written; empty when both are, else the first that is not.</summary>
        private static string Filled(nint list, nint values, int handleCount)
        {
            if (handleCount > 0 && !UpdateProcThreadAttribute(list, 0, HandleListAttribute, values, (nuint)(IntPtr.Size * handleCount), 0, 0))
            {
                return Error("UpdateProcThreadAttribute(HANDLE_LIST)");
            }

            return UpdateProcThreadAttribute(list, 0, JobListAttribute, values + (IntPtr.Size * handleCount), (nuint)IntPtr.Size, 0, 0)
                ? string.Empty
                : Error("UpdateProcThreadAttribute(JOB_LIST)");
        }

        public void Dispose()
        {
            if (Pointer != 0)
            {
                DeleteProcThreadAttributeList(Pointer);
                Marshal.FreeHGlobal(Pointer);
            }

            if (_values != 0)
            {
                Marshal.FreeHGlobal(_values);
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
    private static partial bool IsProcessInJob(SafeWaitHandle process, SafeWaitHandle job, [MarshalAs(UnmanagedType.Bool)] out bool result);

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
