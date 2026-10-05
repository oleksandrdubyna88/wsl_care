using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WslCare.Core.Files;

/// <summary>
/// Reading a file another party controls with NO link anywhere between a trusted folder and the file (plan §15q, E7.S0 review
/// S1 and S6): <c>O_NOFOLLOW</c> guards only the LAST component, so a folder on the way that is a link — <c>~/.config/wsl-care</c>
/// → a root-only folder, <c>…/Users/me/.docker</c> → <c>/root/.docker</c> — would take root's read wherever the link points.
/// Here every folder below <c>beneath</c> is opened from the previous one's descriptor with <c>O_PATH | O_DIRECTORY |
/// O_NOFOLLOW</c> (a link there is refused, never followed), and the file itself with <c>O_NOFOLLOW | O_NONBLOCK</c> from the
/// last folder's descriptor — nothing can be swapped between the checks and the read. The refusal names the component, never
/// anything about where a link points (S6: nothing outside the user's tree is described).
/// </summary>
/// <remarks><b>Windows:</b> every component below <c>beneath</c> is asked for a reparse point before the read (the Windows
/// binary reads only its own user's files there).</remarks>
public static partial class BeneathFiles
{
    public const string LinkOnTheWay = "a link (or not a folder) on its way, never followed";

    /// <summary>This operating system's separators: on Linux a backslash is part of a name, never a separator.</summary>
    private static readonly char[] Separators = OperatingSystem.IsWindows() ? ['/', '\\'] : ['/'];

    /// <summary>The file at <paramref name="path"/>, reached from <paramref name="beneath"/> through no link, judged as
    /// <see cref="RegularFiles.ReadOwned"/> judges (regular, capped, and — with an <paramref name="owner"/> — that owner's, no
    /// group / other write). A path not below <paramref name="beneath"/> is reached from its own folder (its last component
    /// is then the only one guarded).</summary>
    public static FileReadResult Read(string beneath, string path, int maxBytes, uint? owner)
    {
        var (start, components) = Split(beneath, path);
        if (components.Count == 0 || components.Any(c => c is "." or ".."))
        {
            return new FileReadResult.Unreadable($"{path}: not a plain path below {start}");
        }

        try
        {
            return OperatingSystem.IsLinux() ? ReadLinux(start, components, maxBytes, owner) : ReadOther(start, components, path, maxBytes, owner);
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

    /// <summary>The folder a Windows-drive path is read beneath (review S1): the folder holding the drive letter's folder
    /// (<c>/mnt</c> of <c>/mnt/c/Users/…</c>), so no component from the drive down may be a link; the file's own folder when the
    /// path names no drive letter.</summary>
    public static string DriveBase(string path)
    {
        var parts = path.Split(Separators);
        var drive = Array.FindIndex(parts, p => p.Length == 1 && char.IsAsciiLetter(p[0]));
        return drive <= 0 ? Path.GetDirectoryName(path) ?? path : string.Join('/', parts[..drive]) is { Length: > 0 } joined ? joined : "/";
    }

    private static (string Start, IReadOnlyList<string> Components) Split(string beneath, string path)
    {
        var root = beneath.TrimEnd(Separators);
        var under = root.Length > 0 && path.Length > root.Length + 1 && path.StartsWith(root, StringComparison.Ordinal) && Separators.Contains(path[root.Length]);
        var start = under ? root : Path.GetDirectoryName(path) ?? path;
        return (start, path[start.Length..].Split(Separators, StringSplitOptions.RemoveEmptyEntries));
    }

    [SupportedOSPlatform("linux")]
    private static FileReadResult ReadLinux(string start, IReadOnlyList<string> components, int maxBytes, uint? owner)
    {
        var folder = Native.Open(start, Native.PathOnly | Native.Directory | Native.CloseOnExec, 0);
        if (folder < 0)
        {
            return Failure(Marshal.GetLastPInvokeError(), start);
        }

        foreach (var component in components.Take(components.Count - 1))
        {
            var next = Native.OpenAt(folder, component, Native.PathOnly | Native.Directory | Native.NoFollow | Native.CloseOnExec, 0);
            var errno = Marshal.GetLastPInvokeError();
            _ = Native.Close(folder);
            if (next < 0)
            {
                return Failure(errno, component);
            }

            folder = next;
        }

        var fd = Native.OpenAt(folder, components[^1], Native.ReadOnlyNonBlocking | Native.NoFollow, 0);
        var lastErrno = Marshal.GetLastPInvokeError();
        _ = Native.Close(folder);
        return fd < 0 ? Failure(lastErrno, components[^1]) : RegularFiles.ReadOpened(fd, maxBytes, owner);
    }

    private static FileReadResult ReadOther(string start, IReadOnlyList<string> components, string path, int maxBytes, uint? owner)
    {
        var walked = start;
        foreach (var component in components)
        {
            walked = Path.Combine(walked, component);
            if ((File.Exists(walked) || Directory.Exists(walked)) && File.GetAttributes(walked).HasFlag(FileAttributes.ReparsePoint))
            {
                return new FileReadResult.Unreadable($"{LinkOnTheWay} (at {component})");
            }
        }

        return owner is { } who ? RegularFiles.ReadOwned(path, maxBytes, who) : RegularFiles.ReadNoFollow(path, maxBytes);
    }

    private static FileReadResult Failure(int errno, string component) => errno switch
    {
        Native.NoEntry => new FileReadResult.Missing(),
        Native.NotADirectory or Native.TooManyLinks => new FileReadResult.Unreadable($"{LinkOnTheWay} (at {component})"),
        _ => new FileReadResult.Unreadable($"{component} could not be opened (errno {errno})"),
    };

    private static partial class Native
    {
        public const int ReadOnlyNonBlocking = 0x800 | 0x80000 | 0x100;
        public const int PathOnly = 0x200000;
        public const int CloseOnExec = 0x80000;
        public const int NoEntry = 2;
        public const int NotADirectory = 20;
        public const int TooManyLinks = 40;

        private const string Libc = "libc.so.6";

        /// <summary><c>O_NOFOLLOW</c> and <c>O_DIRECTORY</c> differ by architecture (arm64's own uapi fcntl.h).</summary>
        public static int NoFollow => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x8000 : 0x20000;

        public static int Directory => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x4000 : 0x10000;

        [LibraryImport(Libc, EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int Open(string path, int flags, int mode);

        [LibraryImport(Libc, EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int OpenAt(int dirfd, string path, int flags, int mode);

        [LibraryImport(Libc, EntryPoint = "close", SetLastError = true)]
        internal static partial int Close(int fd);
    }
}
