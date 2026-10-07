using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Microsoft.Win32.SafeHandles;

namespace WslCare.Core.Files;

/// <summary>An open of a file or a folder by a native call (plan §15r E9.S2a own review round m4: one closed answer for both
/// systems, never a tuple): the handle, or the system's error code. Disposing it closes an opened handle.</summary>
internal abstract record NativeOpen : IDisposable
{
    private NativeOpen()
    {
    }

    public sealed record Opened(SafeFileHandle Handle) : NativeOpen
    {
        public override void Dispose() => Handle.Dispose();
    }

    public sealed record Failed(int Error) : NativeOpen
    {
        public override void Dispose()
        {
        }
    }

    public abstract void Dispose();

    /// <summary>The system's error code of a failed open; 0 for an opened one.</summary>
    public int ErrorCode => this is Failed failed ? failed.Error : 0;
}

/// <summary>What <c>GetFileInformationByHandle</c> said about an open Windows file; <see cref="Unknown"/> when it could not say.</summary>
internal readonly record struct WindowsFileInfo(bool Known, uint Attributes, uint Links, uint VolumeSerial, ulong Index)
{
    public static WindowsFileInfo Unknown { get; } = new(false, 0, 0, 0, 0);

    public FileIdentity Identity => new(VolumeSerial, Index);

    /// <summary>No reparse point, and a folder exactly when <paramref name="folder"/> is the directory attribute (0: a file).</summary>
    public bool Is(uint folder) => Known && (Attributes & (BeneathWrites.WindowsReparsePoint | BeneathWrites.WindowsDirectory)) == folder;

    /// <summary>A regular file — no folder, no reparse point — of ONE link.</summary>
    public bool IsPlainFile => Is(0) && Links == 1;
}

/// <summary>
/// The archive seam's native calls (plan §15r E9.S2a) — the ONLY place outside <see cref="PhysicalFileSystem"/> that may name
/// <c>renameat2</c>, <c>unlinkat</c>, <c>mkdirat</c>, a rename by handle (<c>FILE_RENAME_INFO</c>) or a delete disposition (the
/// architecture scan holds every other file to that). Linux: every folder is opened from the previous one's descriptor with
/// <c>O_NOFOLLOW</c>, from the file system's root along the path the policy judged (own review round, security m1), so a link
/// anywhere on the way is refused, never followed. Windows: a handle opened for exactly the access it needs, a reparse point never
/// followed, and asked afterwards where it really is.
/// </summary>
internal static partial class BeneathWrites
{
    /// <summary>A Linux call's answer: a descriptor (or 0 for a call that returns none), or the errno.</summary>
    internal readonly record struct Native(int Value, int Errno)
    {
        public bool Failed => Value < 0;
    }

    /// <summary>A folder chain's answer: the last folder's descriptor, or the errno and the component that failed.</summary>
    internal readonly record struct Chain(Native Folder, string FailedAt);

    internal const int NoEntry = 2;
    internal const int Exists = 17;
    internal const int InvalidArgument = 22;
    internal const int NotEmpty = 39;
    internal const int TooManyLinks = 40;
    internal const int NotSupported = 95;

    /// <summary>The folder at <paramref name="start"/> (its last component never a link), then each of <paramref name="components"/>
    /// from the previous one's descriptor with <c>O_NOFOLLOW</c> — for <c>*at</c> calls (<c>O_PATH</c>); the last one opened
    /// <paramref name="readable"/> when the caller must <c>fsync</c> or create in it.</summary>
    [SupportedOSPlatform("linux")]
    internal static Chain OpenChain(string start, IReadOnlyList<string> components, bool readable)
    {
        var first = Linux.Open(start, FolderFlags(components.Count == 0 && readable), 0);
        if (first < 0)
        {
            return new Chain(new Native(-1, Marshal.GetLastPInvokeError()), start);
        }

        var folder = first;
        for (var i = 0; i < components.Count; i++)
        {
            var next = Linux.OpenAt(folder, components[i], FolderFlags(i == components.Count - 1 && readable), 0);
            var errno = Marshal.GetLastPInvokeError();
            _ = Linux.Close(folder);
            if (next < 0)
            {
                return new Chain(new Native(-1, errno), components[i]);
            }

            folder = next;
        }

        return new Chain(new Native(folder, 0), string.Empty);
    }

    [SupportedOSPlatform("linux")]
    private static int FolderFlags(bool readable) => (readable ? Linux.ReadOnly : Linux.PathOnly) | Linux.Directory | Linux.NoFollow | Linux.CloseOnExec;

    /// <summary>A folder opened from <paramref name="folder"/>'s descriptor, never through a link.</summary>
    [SupportedOSPlatform("linux")]
    internal static Native OpenFolderAt(int folder, string name, bool readable) => Call(Linux.OpenAt(folder, name, FolderFlags(readable), 0));

    [SupportedOSPlatform("linux")]
    internal static Native MakeFolderAt(int folder, string name) =>
        Linux.MkdirAt(folder, name, Linux.PrivateFolder) == 0 ? new Native(0, 0) : new Native(-1, Marshal.GetLastPInvokeError());

    [SupportedOSPlatform("linux")]
    internal static NativeOpen OpenReadAt(int folder, string name) => Handle(Linux.OpenAt(folder, name, Linux.ReadOnly | Linux.NonBlocking | Linux.NoFollow | Linux.CloseOnExec, 0));

    [SupportedOSPlatform("linux")]
    internal static NativeOpen CreateExclusiveAt(int folder, string name) =>
        Handle(Linux.OpenAt(folder, name, Linux.WriteOnly | Linux.Create | Linux.Exclusive | Linux.NoFollow | Linux.CloseOnExec, Linux.PrivateFile));

    [SupportedOSPlatform("linux")]
    internal static Native RenameNoReplace(int folder, string from, string to) => Call(Linux.RenameAt2(folder, from, folder, to, Linux.NoReplace));

    [SupportedOSPlatform("linux")]
    internal static Native UnlinkAt(int folder, string name, bool directory) => Call(Linux.UnlinkAt(folder, name, directory ? Linux.RemoveDirectory : 0));

    [SupportedOSPlatform("linux")]
    internal static Native Sync(int descriptor) => Call(Linux.Fsync(descriptor));

    /// <summary>A WRITE LEASE on an open file (plan §15r E9.S2a, risk consult 9/9.2): the kernel grants it only when no OTHER open
    /// file description of the file exists anywhere — a descriptor a child inherited by fork, a writable shared mapping whose
    /// descriptor was closed, another account's open — and while it is held a new open of the file must first break it. Its break
    /// is announced by SIGURG (ignored by default), never by SIGIO (which would end this process): the holder asks
    /// <see cref="LeaseHeld"/> instead.</summary>
    [SupportedOSPlatform("linux")]
    internal static Native TakeWriteLease(int descriptor) =>
        Linux.Fcntl(descriptor, Linux.SetSignal, Linux.UrgentSignal) < 0 ? new Native(-1, Marshal.GetLastPInvokeError()) : Call(Linux.Fcntl(descriptor, Linux.SetLease, Linux.WriteLock));

    /// <summary>Whether the write lease taken on <paramref name="descriptor"/> is still whole — no open arrived to break it.</summary>
    [SupportedOSPlatform("linux")]
    internal static bool LeaseHeld(int descriptor) => Linux.Fcntl(descriptor, Linux.GetLease, 0) == Linux.WriteLock;

    [SupportedOSPlatform("linux")]
    internal static void Close(int descriptor) => _ = Linux.Close(descriptor);

    /// <summary>The descriptor number inside an open handle — valid only while the caller holds the handle.</summary>
    internal static int Descriptor(SafeFileHandle handle) => (int)handle.DangerousGetHandle();

    /// <summary>The identity and kind of a descriptor (<paramref name="name"/> empty) or of a name in a folder, never following a
    /// link; <see cref="LinuxStatus.None"/> (not <see cref="LinuxStatus.Known"/>) when <c>statx</c> failed.</summary>
    [SupportedOSPlatform("linux")]
    internal static LinuxStatus Stat(int folder, string name)
    {
        var buffer = new byte[Linux.StatxSize];
        var flags = name.Length == 0 ? Linux.EmptyPath : Linux.SymlinkNoFollow;
        return Linux.Statx(folder, name, flags, Linux.StatxBasic, buffer) == 0 ? LinuxStatus.From(buffer) : LinuxStatus.None;
    }

    private static Native Call(int result) => result < 0 ? new Native(-1, Marshal.GetLastPInvokeError()) : new Native(result, 0);

    private static NativeOpen Handle(int result) =>
        result < 0 ? new NativeOpen.Failed(Marshal.GetLastPInvokeError()) : new NativeOpen.Opened(new SafeFileHandle(result, ownsHandle: true));

    /// <summary>What a <c>statx</c> said; <see cref="None"/> is "it could not be read".</summary>
    internal readonly record struct LinuxStatus(bool Known, int Type, uint Links, uint Owner, ulong Inode, uint DeviceMajor, uint DeviceMinor, long Size, DateTimeOffset LastWriteUtc, long BornNanoseconds)
    {
        public const int Regular = 0x8000;

        public static LinuxStatus None { get; } = new(false, 0, 0, 0, 0, 0, 0, 0, DateTimeOffset.UnixEpoch, 0);

        public bool IsRegular => Known && Type == Regular;

        /// <summary>A regular file of ONE link.</summary>
        public bool IsPlainFile => IsRegular && Links == 1;

        /// <summary>The device, the inode and — where the file system keeps it — the birth time: what names one file on Linux. The
        /// birth time tells a file from a later one that reused a freed inode number (seen on ext4: a file removed and another
        /// created at once get the same inode).</summary>
        public FileIdentity Identity => new(((ulong)DeviceMajor << 32) | DeviceMinor, Inode, BornNanoseconds);

        public bool SameFile(LinuxStatus other) => Known && other.Known && Identity == other.Identity;

        /// <summary>The fields of the 256-byte <c>struct statx</c>: <c>stx_nlink</c> (u32 at 16), <c>stx_uid</c> (u32 at 20), <c>stx_mode</c>
        /// (u16 at 28), <c>stx_ino</c> (u64 at 32), <c>stx_size</c> (u64 at 40), <c>stx_mtime</c> (i64 + u32 at 112),
        /// <c>stx_dev_major</c> / <c>stx_dev_minor</c> (u32 at 136 / 140), and <c>stx_btime</c> (i64 + u32 at 80) when <c>stx_mask</c>
        /// (u32 at 0) holds <c>STATX_BTIME</c> — 0 where the file system keeps none.</summary>
        public static LinuxStatus From(byte[] statx) => new(
            true,
            BitConverter.ToUInt16(statx, 28) & 0xF000,
            BitConverter.ToUInt32(statx, 16),
            BitConverter.ToUInt32(statx, 20),
            BitConverter.ToUInt64(statx, 32),
            BitConverter.ToUInt32(statx, 136),
            BitConverter.ToUInt32(statx, 140),
            (long)BitConverter.ToUInt64(statx, 40),
            DateTimeOffset.FromUnixTimeSeconds(BitConverter.ToInt64(statx, 112)).AddTicks(BitConverter.ToUInt32(statx, 120) / 100),
            (BitConverter.ToUInt32(statx, 0) & BirthTimeMask) != 0 ? (BitConverter.ToInt64(statx, 80) * NanosecondsPerSecond) + BitConverter.ToUInt32(statx, 88) : 0);

        private const uint BirthTimeMask = 0x800;
        private const long NanosecondsPerSecond = 1_000_000_000;
    }

    [SupportedOSPlatform("linux")]
    private static partial class Linux
    {
        public const int ReadOnly = 0;
        public const int WriteOnly = 1;
        public const int Create = 0x40;
        public const int Exclusive = 0x80;
        public const int NonBlocking = 0x800;
        public const int CloseOnExec = 0x80000;
        public const int PathOnly = 0x200000;
        public const int PrivateFile = 0x180;
        public const int PrivateFolder = 0x1C0;
        public const int RemoveDirectory = 0x200;
        public const int SymlinkNoFollow = 0x100;
        public const int EmptyPath = 0x1000;
        public const uint NoReplace = 1;
        public const int SetSignal = 10;
        public const int SetLease = 1024;
        public const int GetLease = 1025;
        public const int WriteLock = 1;
        public const int UrgentSignal = 23;
        public const uint StatxBasic = 0xFFF;
        public const int StatxSize = 256;

        private const string Libc = "libc.so.6";

        /// <summary><c>O_NOFOLLOW</c> and <c>O_DIRECTORY</c> differ by architecture (arm64's own uapi fcntl.h).</summary>
        public static int NoFollow => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x8000 : 0x20000;

        public static int Directory => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x4000 : 0x10000;

        [LibraryImport(Libc, EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int Open(string path, int flags, int mode);

        [LibraryImport(Libc, EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int OpenAt(int dirfd, string path, int flags, int mode);

        [LibraryImport(Libc, EntryPoint = "mkdirat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int MkdirAt(int dirfd, string path, int mode);

        [LibraryImport(Libc, EntryPoint = "renameat2", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int RenameAt2(int olddirfd, string oldpath, int newdirfd, string newpath, uint flags);

        [LibraryImport(Libc, EntryPoint = "unlinkat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int UnlinkAt(int dirfd, string path, int flags);

        [LibraryImport(Libc, EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int Statx(int dirfd, string path, int flags, uint mask, byte[] buffer);

        [LibraryImport(Libc, EntryPoint = "fcntl", SetLastError = true)]
        internal static partial int Fcntl(int fd, int command, int argument);

        [LibraryImport(Libc, EntryPoint = "fsync", SetLastError = true)]
        internal static partial int Fsync(int fd);

        [LibraryImport(Libc, EntryPoint = "close", SetLastError = true)]
        internal static partial int Close(int fd);
    }

    /// <summary>Opens <paramref name="path"/> for reading its bytes and — with <paramref name="delete"/> — for deleting it through the
    /// same handle; sharing READ only while it is held (no other process may write, rename or delete it meanwhile); a reparse point
    /// opened as itself, never followed; with <paramref name="unbuffered"/> read past the system cache (<c>FILE_FLAG_NO_BUFFERING</c>).</summary>
    [SupportedOSPlatform("windows")]
    internal static NativeOpen OpenWindows(string path, bool delete, bool unbuffered)
    {
        var access = Windows.GenericRead | (delete ? Windows.Delete : 0);
        var flags = Windows.OpenReparsePoint | Windows.SequentialScan | (unbuffered ? Windows.NoBuffering : 0);
        return Handle(Windows.CreateFile(path, access, Windows.ShareRead, IntPtr.Zero, Windows.OpenExisting, flags, IntPtr.Zero));
    }

    /// <summary>A session file opened to COPY it: read only, sharing read, write and delete (the agent keeps appending and may delete
    /// it — never blocked by the archive), the reparse point itself opened, never followed.</summary>
    [SupportedOSPlatform("windows")]
    internal static NativeOpen OpenSourceWindows(string path) =>
        Handle(Windows.CreateFile(path, Windows.GenericRead, Windows.ShareRead | Windows.ShareWrite | Windows.ShareDelete, IntPtr.Zero, Windows.OpenExisting, Windows.OpenReparsePoint | Windows.SequentialScan, IntPtr.Zero));

    /// <summary>A Windows folder held for the archive's creates (gate round findings 3 and 4): the folder itself, never a reparse
    /// point followed (<c>FILE_FLAG_OPEN_REPARSE_POINT</c>); <c>FILE_ADD_FILE</c>, the right <c>FlushFileBuffers</c> needs on a
    /// folder (measured: read-only or attributes-only handles answer error 5); sharing READ and WRITE but never DELETE, so while
    /// it is held neither the folder nor any folder above it can be renamed (measured on NTFS) — nothing can swap it for a link.</summary>
    [SupportedOSPlatform("windows")]
    internal static NativeOpen HoldFolderWindows(string path) =>
        Handle(Windows.CreateFile(path, Windows.AddFile, Windows.ShareRead | Windows.ShareWrite, IntPtr.Zero, Windows.OpenExisting, Windows.BackupSemantics | Windows.OpenReparsePoint, IntPtr.Zero));

    /// <summary>A file or a folder opened to rename or remove it THROUGH this handle (gate round finding 3): <c>DELETE</c> and
    /// attributes, the reparse point itself never followed, sharing everything as a rename by path does — so an agent's open
    /// blocks no more than before.</summary>
    [SupportedOSPlatform("windows")]
    internal static NativeOpen OpenToChangeWindows(string path) =>
        Handle(Windows.CreateFile(path, Windows.Delete | Windows.ReadAttributes, Windows.ShareRead | Windows.ShareWrite | Windows.ShareDelete, IntPtr.Zero, Windows.OpenExisting, Windows.BackupSemantics | Windows.OpenReparsePoint, IntPtr.Zero));

    private static NativeOpen Handle(SafeFileHandle handle)
    {
        if (!handle.IsInvalid)
        {
            return new NativeOpen.Opened(handle);
        }

        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        return new NativeOpen.Failed(error);
    }

    /// <summary>Flushes an open file or a held folder to the disk; 0 or the Win32 error.</summary>
    [SupportedOSPlatform("windows")]
    internal static int FlushWindows(SafeFileHandle handle) => Windows.FlushFileBuffers(handle) ? 0 : Marshal.GetLastPInvokeError();

    /// <summary>Where the open file really is — every link on the way resolved by the system, <c>\\?\</c> taken off; empty when the
    /// system could not say.</summary>
    [SupportedOSPlatform("windows")]
    internal static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new char[Windows.LongestPath];
        var length = handle.IsInvalid ? 0 : Windows.GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        return length == 0 || length >= buffer.Length ? string.Empty : WithoutDevicePrefix(new string(buffer, 0, (int)length));
    }

    private static string WithoutDevicePrefix(string path) =>
        path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + path[8..]
        : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..]
        : path;

    /// <summary>The attributes, link count and identity of an open Windows file.</summary>
    [SupportedOSPlatform("windows")]
    internal static WindowsFileInfo Describe(SafeFileHandle handle) =>
        Windows.GetFileInformationByHandle(handle, out var info)
            ? new WindowsFileInfo(true, info.FileAttributes, info.NumberOfLinks, info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow)
            : WindowsFileInfo.Unknown;

    /// <summary>Marks the open file for deletion when its handle closes — set only by the caller AFTER it checked what it removes:
    /// POSIX semantics (the name goes at once). Where the file system has none, the classic disposition (the name stays, delete-
    /// pending, until every handle closes) only when <paramref name="classicAllowed"/> — never under an AI agent's folder, where an
    /// agent re-creating the name would be refused (own review round m2); 0 or the Win32 error.</summary>
    [SupportedOSPlatform("windows")]
    internal static int MarkForDeletion(SafeFileHandle handle, bool classicAllowed)
    {
        var posix = Windows.DispositionDelete | Windows.DispositionPosix;
        if (Windows.SetFileInformationByHandle(handle, Windows.FileDispositionInfoEx, ref posix, sizeof(uint)))
        {
            return 0;
        }

        var posixError = Marshal.GetLastPInvokeError();
        var classic = 1u;
        return !classicAllowed ? posixError
            : Windows.SetFileInformationByHandle(handle, Windows.FileDispositionInfo, ref classic, 1) ? 0
            : Marshal.GetLastPInvokeError();
    }

    /// <summary>Renames the OPEN file to <paramref name="destination"/> (a full path), never replacing: <c>FILE_RENAME_INFO</c> with
    /// <c>ReplaceIfExists</c> false and no root handle (its layout: the flag padded to a pointer, the root handle, the name's byte
    /// length, the name). Measured: a bare name is taken relative to the CURRENT folder, and a root handle is refused (error 87) —
    /// so the caller holds the folder (<see cref="HoldFolderWindows"/>), which pins the full path. 0 or the Win32 error.</summary>
    [SupportedOSPlatform("windows")]
    internal static int RenameNoReplaceWindows(SafeFileHandle handle, string destination)
    {
        var lengthOffset = 2 * IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(uint);
        var nameBytes = destination.Length * sizeof(char);
        var buffer = new byte[nameOffset + nameBytes + sizeof(char)];
        BitConverter.TryWriteBytes(buffer.AsSpan(lengthOffset), (uint)nameBytes);
        System.Text.Encoding.Unicode.GetBytes(destination, buffer.AsSpan(nameOffset));
        return Windows.SetFileInformationByHandle(handle, Windows.FileRenameInfo, buffer, (uint)buffer.Length) ? 0 : Marshal.GetLastPInvokeError();
    }

    internal const int WindowsNotFound = 2;
    internal const int WindowsPathNotFound = 3;
    internal const int WindowsSharing = 32;
    internal const int WindowsFileExists = 80;
    internal const int WindowsAlreadyExists = 183;
    internal const int WindowsFolderNotEmpty = 145;
    internal const uint WindowsReparsePoint = 0x400;
    internal const uint WindowsDirectory = 0x10;

    [SupportedOSPlatform("windows")]
    private static partial class Windows
    {
        public const uint GenericRead = 0x80000000;
        public const uint Delete = 0x00010000;
        public const uint ShareRead = 1;
        public const uint ShareWrite = 2;
        public const uint ShareDelete = 4;
        public const uint AddFile = 2;
        public const uint ReadAttributes = 0x80;
        public const int FileRenameInfo = 3;
        public const uint OpenExisting = 3;
        public const uint BackupSemantics = 0x02000000;
        public const uint OpenReparsePoint = 0x00200000;
        public const int LongestPath = 32768;
        public const uint SequentialScan = 0x08000000;
        public const uint NoBuffering = 0x20000000;
        public const int FileDispositionInfo = 4;
        public const int FileDispositionInfoEx = 21;
        public const uint DispositionDelete = 1;
        public const uint DispositionPosix = 2;

        [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        public static partial SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref uint information, uint size);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, byte[] information, uint size);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool FlushFileBuffers(SafeFileHandle handle);

        [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        public static partial uint GetFinalPathNameByHandle(SafeFileHandle handle, [Out] char[] buffer, uint size, uint flags);
    }

    /// <summary><c>BY_HANDLE_FILE_INFORMATION</c>, field for field (each <c>FILETIME</c> as its two halves).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationLow;
        public uint CreationHigh;
        public uint AccessLow;
        public uint AccessHigh;
        public uint WriteLow;
        public uint WriteHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
