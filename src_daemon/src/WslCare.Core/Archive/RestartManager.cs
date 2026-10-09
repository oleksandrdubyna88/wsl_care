using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WslCare.Core.Archive;

/// <summary>What the Restart Manager answered for a set of files: none is held, the processes that hold one, or why it could not be
/// asked — a closed set.</summary>
public abstract record RmAnswer
{
    private RmAnswer()
    {
    }

    public sealed record Free : RmAnswer;

    /// <param name="Holders">Each holding process as <c>&lt;application&gt; (pid &lt;n&gt;)</c>.</param>
    public sealed record Held(IReadOnlyList<string> Holders) : RmAnswer;

    public sealed record Failed(string Why) : RmAnswer;
}

/// <summary>Who holds the files of one unit open (plan §15r D2.2, E9.S5) — the Windows side's open-file question.</summary>
public interface IRestartManager
{
    /// <param name="files">The unit's files, as full paths.</param>
    RmAnswer Holders(IReadOnlyList<string> files);
}

/// <summary>
/// The Windows Restart Manager, ASKED (plan §15r D2.2, E9.S5): <c>RmStartSession</c>, <c>RmRegisterResources</c> with the files as
/// extended-length paths (<c>\\?\</c>, <c>\\?\UNC\</c> for a share — no <c>MAX_PATH</c>), <c>RmGetList</c>, and <c>RmEndSession</c>
/// ALWAYS. It never opens a file: the agent's own write never fails because the archive looked (the archive plan §4.2's exclusive
/// open was refused for exactly that).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class RestartManager : IRestartManager
{
    private const int Success = 0;
    private const int MoreData = 234;
    private const int SessionKeyChars = 33;
    private const int AppNameChars = 256;
    private const int ServiceNameChars = 64;
    private const int MaxPath = 260;

    /// <summary>The Restart Manager for the files whose path fits its buffers; the file system's own list of users for a longer one
    /// (measured: <c>RmRegisterResources</c> answers error 29 for a path past <c>MAX_PATH</c>, <c>\\?\</c> or not — the E9.S5 test of a
    /// 300-character path found it). Either answer that names a holder, or fails, decides.</summary>
    public RmAnswer Holders(IReadOnlyList<string> files)
    {
        var (fit, longer) = (files.Where(f => ExtendedPath.Of(f).Length < MaxPath).ToList(), files.Where(f => ExtendedPath.Of(f).Length >= MaxPath).ToList());
        return Combined(fit.Count > 0 ? Asked(fit) : new RmAnswer.Free(), longer.Count > 0 ? FileUsers.Holders(longer) : new RmAnswer.Free());
    }

    private static RmAnswer Combined(RmAnswer first, RmAnswer second) => (first, second) switch
    {
        (RmAnswer.Failed failed, _) => failed,
        (_, RmAnswer.Failed failed) => failed,
        (RmAnswer.Held a, RmAnswer.Held b) => new RmAnswer.Held([.. a.Holders, .. b.Holders]),
        (RmAnswer.Held a, _) => a,
        _ => second,
    };

    private static RmAnswer Asked(IReadOnlyList<string> files)
    {
        var key = new ushort[SessionKeyChars];
        var started = RmStartSession(out var session, 0, key);
        if (started != Success)
        {
            return Failure("RmStartSession", started);
        }

        try
        {
            return Registered(session, files);
        }
        finally
        {
            _ = RmEndSession(session);
        }
    }

    private static RmAnswer Registered(uint session, IReadOnlyList<string> files)
    {
        var registered = RmRegisterResources(session, (uint)files.Count, [.. files.Select(ExtendedPath.Of)], 0, 0, 0, 0);
        return registered == Success ? Listed(session) : Failure("RmRegisterResources", registered);
    }

    /// <summary>The list: asked once for its size, then for its entries — and once more if it grew in between; a list that keeps
    /// growing is no answer (the unit stays).</summary>
    private static RmAnswer Listed(uint session)
    {
        var first = List(session, 0);
        var second = first.Code == MoreData ? List(session, first.Needed) : first;
        var answer = second.Code == MoreData ? List(session, second.Needed) : second;
        return answer.Code == Success ? Of(answer.Holders) : Failure("RmGetList", answer.Code);
    }

    private static RmAnswer Of(IReadOnlyList<string> holders) => holders.Count == 0 ? new RmAnswer.Free() : new RmAnswer.Held(holders);

    private sealed record ListCall(int Code, uint Needed, IReadOnlyList<string> Holders);

    private static ListCall List(uint session, uint room)
    {
        var infos = new ProcessInfo[room];
        var count = room;
        var code = RmGetList(session, out var needed, ref count, infos, out _);
        return new ListCall(code, needed, code == Success ? [.. infos.Take((int)count).Select(Holder)] : []);
    }

    /// <summary>A holder by its pid ONLY: the Restart Manager's application name is often a window title — a document's, a tab's — and
    /// the reason lands in the records (the E9.S5 own review, 5).</summary>
    private static string Holder(ProcessInfo info) => string.Create(CultureInfo.InvariantCulture, $"a process (pid {info.ProcessId})");

    private static RmAnswer.Failed Failure(string call, int code) =>
        new(string.Create(CultureInfo.InvariantCulture, $"{call} answered Win32 error {code}"));

    /// <summary><c>RM_PROCESS_INFO</c>: <c>RM_UNIQUE_PROCESS</c> (pid, start <c>FILETIME</c>), the application's and the service's
    /// names, its type, status, terminal-services session and whether it restarts.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        public uint ProcessId;
        public uint StartLow;
        public uint StartHigh;
        public AppNameBuffer AppName;
        public ServiceNameBuffer ServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TerminalSession;
        public int Restartable;
    }

    /// <summary><c>CCH_RM_MAX_APP_NAME + 1</c> UTF-16 units.</summary>
    [System.Runtime.CompilerServices.InlineArray(AppNameChars)]
    private struct AppNameBuffer
    {
        private ushort _first;
    }

    /// <summary><c>CCH_RM_MAX_SVC_NAME + 1</c> UTF-16 units.</summary>
    [System.Runtime.CompilerServices.InlineArray(ServiceNameChars)]
    private struct ServiceNameBuffer
    {
        private ushort _first;
    }

    [LibraryImport("rstrtmgr.dll")]
    private static partial int RmStartSession(out uint session, int flags, [Out] ushort[] sessionKey);

    [LibraryImport("rstrtmgr.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RmRegisterResources(uint session, uint fileCount, string[] files, uint applicationCount, nint applications, uint serviceCount, nint services);

    [LibraryImport("rstrtmgr.dll")]
    private static partial int RmGetList(uint session, out uint needed, ref uint count, [In, Out] ProcessInfo[] applications, out uint rebootReasons);

    [LibraryImport("rstrtmgr.dll")]
    private static partial int RmEndSession(uint session);
}

/// <summary>
/// Who has a file open, as the file system keeps it (<c>FileProcessIdsUsingFileInformation</c>, E9.S5) — for a path the Restart
/// Manager cannot take. The file is opened for its ATTRIBUTES only, every sharing allowed and no link followed: an open that asks for
/// no data access conflicts with no one's sharing, so the agent's own write never fails because the archive looked.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class FileUsers
{
    private const uint ReadAttributes = 0x80;
    private const uint ShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const int ProcessIdsUsingFile = 47;
    private const int LengthMismatch = unchecked((int)0xC0000004);
    private const int FirstRoom = 64;

    public static RmAnswer Holders(IReadOnlyList<string> files) =>
        files.Select(Of).FirstOrDefault(a => a is not RmAnswer.Free, new RmAnswer.Free());

    private static RmAnswer Of(string file)
    {
        using var handle = CreateFile(ExtendedPath.Of(file), ReadAttributes, ShareAll, 0, OpenExisting, OpenReparsePoint, 0);
        return handle.IsInvalid
            ? new RmAnswer.Failed(string.Create(CultureInfo.InvariantCulture, $"its attributes could not be opened (Win32 error {Marshal.GetLastPInvokeError()})"))
            : Users(handle, FirstRoom);
    }

    /// <summary>The list: a count, then the pids — asked again, once, in a room of the size a too-small one named.</summary>
    private static RmAnswer Users(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int room)
    {
        var buffer = new byte[room];
        var status = NtQueryInformationFile(handle, out _, buffer, buffer.Length, ProcessIdsUsingFile);
        return status == LengthMismatch && room == FirstRoom ? Users(handle, Needed(buffer, room))
            : status == 0 ? Listed(buffer)
            : new RmAnswer.Failed(string.Create(CultureInfo.InvariantCulture, $"NtQueryInformationFile answered NTSTATUS 0x{status:X8}"));
    }

    /// <summary>A room for the count the first answer named (one <c>ULONG_PTR</c> per process after the header).</summary>
    private static int Needed(byte[] buffer, int room) => Math.Max(room * 2, nint.Size * (BitConverter.ToInt32(buffer, 0) + 2));

    private static RmAnswer Listed(byte[] buffer)
    {
        var count = BitConverter.ToInt32(buffer, 0);
        var pids = Enumerable.Range(0, count).Select(i => nint.Size == 8 ? BitConverter.ToInt64(buffer, nint.Size * (i + 1)) : BitConverter.ToInt32(buffer, nint.Size * (i + 1))).ToList();
        return pids.Count == 0 ? new RmAnswer.Free() : new RmAnswer.Held([.. pids.Select(pid => string.Create(CultureInfo.InvariantCulture, $"a process (pid {pid})"))]);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationFile(Microsoft.Win32.SafeHandles.SafeFileHandle file, out IoStatusBlock status, [Out] byte[] information, int length, int informationClass);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public nint Status;
        public nint Information;
    }
}

/// <summary>A path as Windows' extended-length form spells it — what the Restart Manager is handed (E9.S5): <c>\\?\C:\…</c>, and for a
/// share <c>\\?\UNC\server\share\…</c>; a path already in that form stays as it is.</summary>
public static class ExtendedPath
{
    private const string Prefix = @"\\?\";
    private const string UncPrefix = @"\\?\UNC\";

    public static string Of(string path)
    {
        var normal = path.Replace('/', '\\');
        return normal.StartsWith(Prefix, StringComparison.Ordinal) ? normal
            : normal.StartsWith(@"\\", StringComparison.Ordinal) ? UncPrefix + Collapsed(normal[2..])
            : Prefix + Collapsed(normal);
    }

    /// <summary>The path without <c>.</c> segments, doubled separators or a trailing one — the extended form takes the path as written,
    /// so nothing may be left for Windows to normalise. A unit's files are plain relative names under a judged folder: no <c>..</c>
    /// reaches here.</summary>
    private static string Collapsed(string path) => string.Join('\\', path.Split('\\', StringSplitOptions.RemoveEmptyEntries).Where(p => p != "."));
}
