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
        if (Directory.Exists(path) || (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.Device)))
        {
            return new FileReadResult.Unreadable($"{NotRegular} (a directory or a device)");
        }

        using var stream = PhysicalFileSystem.OpenForReading(path);
        return stream.CanSeek ? Capped(stream, maxBytes) : new FileReadResult.Unreadable($"{NotRegular} (a pipe or a device)");
    }

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

        private const string Libc = "libc.so.6";

        [LibraryImport(Libc, EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int Open(string path, int flags, int mode);

        [LibraryImport(Libc, EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int Statx(int dirfd, string path, int flags, uint mask, byte[] buffer);
    }
}
