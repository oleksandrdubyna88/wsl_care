using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Microsoft.Win32.SafeHandles;

namespace WslCare.Core.Files;

/// <summary>
/// Reading a file a caller NAMED (A4's <c>--only</c> list, read as root) only when it is a REGULAR file, and never past a
/// hard byte cap — whatever its length claims. A directory, a FIFO, a socket or a device is refused with the reason, and is
/// never waited on: a FIFO with no writer would hold a plain open forever, a device could stream forever (independent review
/// of E3, 2026-10-03).
/// </summary>
/// <remarks>
/// <para><b>Linux.</b> The file is opened with <c>O_NONBLOCK</c> (an open that cannot block on a FIFO or a device), its TYPE is
/// read from the OPEN descriptor (<c>statx(fd, "", AT_EMPTY_PATH, STATX_TYPE)</c> — <c>struct statx</c> has one layout on
/// every architecture, unlike <c>struct stat</c>), and only a regular file is read. Asking the descriptor, not the path, leaves
/// no window for a swap between the check and the read.</para>
/// <para><b>Windows.</b> A directory or a device-flagged path is refused before the open; after it, a stream that cannot seek
/// (a pipe, a console) is refused too.</para>
/// <para>The cap counts the bytes actually read: one byte more than <c>maxBytes</c> is a refusal.</para>
/// </remarks>
public static partial class RegularFiles
{
    public const string NotRegular = "not a regular file";

    public static FileReadResult Read(string path, int maxBytes)
    {
        try
        {
            return OperatingSystem.IsLinux() ? ReadLinux(path, maxBytes) : ReadOther(path, maxBytes);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new FileReadResult.Missing();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new FileReadResult.Unreadable(e.Message);
        }
    }

    /// <summary>
    /// A file root wrote under the state directory and another process will trust (E6.S0 review S1: the request files):
    /// everything <see cref="Read"/> guarantees, and — not a symbolic link (<c>O_NOFOLLOW</c>; on Windows a reparse point is
    /// refused) — and, on Linux, owned by <paramref name="owner"/> (uid 0 on an installed machine) with no group or other
    /// write bit, all asked of the OPEN descriptor in one <c>statx</c>, so nothing can be swapped between the check and the read.
    /// </summary>
    public static FileReadResult ReadOwned(string path, int maxBytes, uint owner)
    {
        try
        {
            return OperatingSystem.IsLinux() ? ReadLinuxOwned(path, maxBytes, owner) : ReadOtherNoLink(path, maxBytes);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new FileReadResult.Missing();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new FileReadResult.Unreadable(e.Message);
        }
    }

    /// <summary>This process's effective uid on Linux (what a sandboxed test trusts as the state's owner); 0 elsewhere.</summary>
    public static uint EffectiveUid() => OperatingSystem.IsLinux() ? Native.GetEffectiveUid() : 0;

    /// <summary>Why a state file with this owner and mode may not be trusted; empty when it may (pure, so it is a unit test).</summary>
    public static string OwnershipProblem(uint fileOwner, int mode, uint owner) =>
        fileOwner != owner ? $"owned by uid {fileOwner}, not uid {owner}"
        : (mode & Native.GroupOrOtherWrite) != 0 ? $"writable by group or others (mode {Convert.ToString(mode & 0xFFF, 8)})"
        : string.Empty;

    private static FileReadResult ReadOtherNoLink(string path, int maxBytes) =>
        File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)
            ? new FileReadResult.Unreadable("a symbolic link or another reparse point, never followed")
            : ReadOther(path, maxBytes);

    [SupportedOSPlatform("linux")]
    private static FileReadResult ReadLinuxOwned(string path, int maxBytes, uint owner)
    {
        var fd = Native.Open(path, Native.OpenReadOnlyNonBlocking | Native.NoFollow, 0);
        if (fd < 0)
        {
            return OwnedErrno(Marshal.GetLastPInvokeError(), path);
        }

        using var handle = new SafeFileHandle(fd, ownsHandle: true);
        if (Judged(fd, owner) is { Length: > 0 } problem)
        {
            return new FileReadResult.Unreadable(problem);
        }

        using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 0);
        return Capped(stream, maxBytes);
    }

    /// <summary>The descriptor's type, owner and mode judged in one <c>statx</c>; empty when the file may be read.</summary>
    [SupportedOSPlatform("linux")]
    private static string Judged(int fd, uint owner)
    {
        var buffer = new byte[Native.StatxSize];
        if (Native.Statx(fd, string.Empty, Native.AtEmptyPath, Native.StatxType | Native.StatxMode | Native.StatxUid, buffer) != 0)
        {
            return $"its type and owner could not be read (errno {Marshal.GetLastPInvokeError()})";
        }

        var mode = BitConverter.ToUInt16(buffer, Native.StatxModeOffset);
        var type = mode & Native.TypeMask;
        return type != Native.TypeRegular ? $"{NotRegular} ({Describe(type)})" : OwnershipProblem(BitConverter.ToUInt32(buffer, Native.StatxUidOffset), mode, owner);
    }

    private static FileReadResult OwnedErrno(int errno, string path) => errno switch
    {
        Native.TooManyLinks => new FileReadResult.Unreadable("a symbolic link, never followed"),
        _ => Errno(errno, path),
    };

    /// <summary>The bytes of an open stream, at most <paramref name="maxBytes"/> — or the refusal when there is more.</summary>
    internal static FileReadResult Capped(Stream stream, int maxBytes)
    {
        var buffer = new byte[maxBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
        {
            total += read;
        }

        return total > maxBytes
            ? new FileReadResult.Unreadable($"larger than {maxBytes} bytes")
            : new FileReadResult.Content(buffer[..total]);
    }

    private static FileReadResult ReadOther(string path, int maxBytes)
    {
        if (IsDirectoryOrDevice(path))
        {
            return new FileReadResult.Unreadable($"{NotRegular} (a directory or a device)");
        }

        using var stream = PhysicalFileSystem.OpenForReading(path);
        return stream.CanSeek ? Capped(stream, maxBytes) : new FileReadResult.Unreadable($"{NotRegular} (a pipe or a device)");
    }

    private static bool IsDirectoryOrDevice(string path) =>
        Directory.Exists(path) || (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.Device));

    [SupportedOSPlatform("linux")]
    private static FileReadResult ReadLinux(string path, int maxBytes)
    {
        var fd = Native.Open(path, Native.OpenReadOnlyNonBlocking, 0);
        if (fd < 0)
        {
            return Errno(Marshal.GetLastPInvokeError(), path);
        }

        using var handle = new SafeFileHandle(fd, ownsHandle: true);
        var type = TypeOf(fd);
        if (type != Native.TypeRegular)
        {
            return new FileReadResult.Unreadable(type < 0 ? $"its type could not be read (errno {-type})" : $"{NotRegular} ({Describe(type)})");
        }

        using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 0);
        return Capped(stream, maxBytes);
    }

    /// <summary>The <c>S_IFMT</c> bits of the open descriptor, or <c>-errno</c>.</summary>
    [SupportedOSPlatform("linux")]
    private static int TypeOf(int fd)
    {
        var buffer = new byte[Native.StatxSize];
        return Native.Statx(fd, string.Empty, Native.AtEmptyPath, Native.StatxType, buffer) == 0
            ? BitConverter.ToUInt16(buffer, Native.StatxModeOffset) & Native.TypeMask
            : -Marshal.GetLastPInvokeError();
    }

    private static FileReadResult Errno(int errno, string path) => errno switch
    {
        Native.NoEntry => new FileReadResult.Missing(),
        _ => new FileReadResult.Unreadable($"{path} could not be opened (errno {errno})"),
    };

    private static string Describe(int type) => type switch
    {
        0x4000 => "a directory",
        0x1000 => "a FIFO",
        0xC000 => "a socket",
        0x2000 or 0x6000 => "a device",
        _ => "an unknown kind of file",
    };

    /// <summary>glibc, by its full soname — loaded only on Linux. The constants are the same on x86-64 and arm64 (the two
    /// Linux RIDs the daemon ships): <c>O_NONBLOCK</c> 0x800, <c>O_CLOEXEC</c> 0x80000, <c>O_NOCTTY</c> 0x100,
    /// <c>AT_EMPTY_PATH</c> 0x1000, <c>STATX_TYPE</c> 1; <c>stx_mode</c> is the u16 at offset 28 of the 256-byte
    /// <c>struct statx</c>.</summary>
    private static partial class Native
    {
        public const int OpenReadOnlyNonBlocking = 0x800 | 0x80000 | 0x100;
        public const int AtEmptyPath = 0x1000;
        public const uint StatxType = 1;
        public const int StatxSize = 256;
        public const int StatxModeOffset = 28;
        public const int TypeMask = 0xF000;
        public const int TypeRegular = 0x8000;
        public const int NoEntry = 2;

        /// <summary><c>STATX_MODE</c> and <c>STATX_UID</c>; <c>stx_uid</c> is the u32 at offset 20.</summary>
        public const uint StatxMode = 2;
        public const uint StatxUid = 8;
        public const int StatxUidOffset = 20;

        /// <summary><c>S_IWGRP | S_IWOTH</c>.</summary>
        public const int GroupOrOtherWrite = 0x12;

        /// <summary><c>ELOOP</c>: what <c>O_NOFOLLOW</c> answers for a symbolic link.</summary>
        public const int TooManyLinks = 40;

        private const string Libc = "libc.so.6";

        /// <summary><c>O_NOFOLLOW</c> differs by architecture: 0x20000 on x86-64, 0x8000 on arm64 (its own uapi fcntl.h).</summary>
        public static int NoFollow => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x8000 : 0x20000;

        [LibraryImport(Libc, EntryPoint = "geteuid")]
        internal static partial uint GetEffectiveUid();

        [LibraryImport(Libc, EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int Open(string path, int flags, int mode);

        [LibraryImport(Libc, EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int Statx(int dirfd, string path, int flags, uint mask, byte[] buffer);
    }
}
