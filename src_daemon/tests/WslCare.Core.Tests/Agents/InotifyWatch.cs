using System.Runtime.InteropServices;

using FluentAssertions;

namespace WslCare.Core.Tests.Agents;

/// <summary>Plan §15q H3 at the SYSCALL level: an inotify instance with <c>IN_OPEN | IN_ACCESS</c> on every folder under a root
/// (memory included). Opening a FOLDER to list it is reported with <c>IN_ISDIR</c> and is not counted. Linux only.</summary>
internal sealed partial class InotifyWatch : IDisposable
{
    private const uint InAccess = 0x00000001;
    private const uint InOpen = 0x00000020;
    private const uint InIsDir = 0x40000000;
    private const int InNonBlock = 0x800;

    private readonly int _fd;
    private readonly Dictionary<int, string> _folders = [];

    private InotifyWatch(int fd) => _fd = fd;

    public static InotifyWatch Over(string root)
    {
        var watch = new InotifyWatch(InotifyInit1(InNonBlock));
        watch._fd.Should().BeGreaterThanOrEqualTo(0, "inotify_init1 must work for the test to mean anything");
        foreach (var folder in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Prepend(root))
        {
            var wd = InotifyAddWatch(watch._fd, folder, InOpen | InAccess);
            wd.Should().BeGreaterThanOrEqualTo(0, $"a watch on {folder}");
            watch._folders[wd] = folder;
        }

        return watch;
    }

    /// <summary>Every open or read of a FILE seen so far, as "folder/name".</summary>
    public IReadOnlyList<string> FileEvents()
    {
        var events = new List<string>();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = Read(_fd, buffer, buffer.Length)) > 0)
        {
            events.AddRange(Parse(buffer.AsSpan(0, read)));
        }

        return events;
    }

    private IEnumerable<string> Parse(ReadOnlySpan<byte> bytes)
    {
        var found = new List<string>();
        for (var at = 0; at + 16 <= bytes.Length;)
        {
            var wd = BitConverter.ToInt32(bytes[at..]);
            var mask = BitConverter.ToUInt32(bytes[(at + 4)..]);
            var length = BitConverter.ToInt32(bytes[(at + 12)..]);
            var name = System.Text.Encoding.UTF8.GetString(bytes.Slice(at + 16, length)).TrimEnd('\0');
            if ((mask & InIsDir) == 0 && (mask & (InOpen | InAccess)) != 0)
            {
                found.Add($"{_folders.GetValueOrDefault(wd, "?")}/{name}");
            }

            at += 16 + length;
        }

        return found;
    }

    public void Dispose() => _ = Close(_fd);

    [LibraryImport("libc.so.6", EntryPoint = "inotify_init1", SetLastError = true)]
    private static partial int InotifyInit1(int flags);

    [LibraryImport("libc.so.6", EntryPoint = "inotify_add_watch", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int InotifyAddWatch(int fd, string path, uint mask);

    [LibraryImport("libc.so.6", EntryPoint = "read", SetLastError = true)]
    private static partial int Read(int fd, [Out] byte[] buffer, int count);

    [LibraryImport("libc.so.6", EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int fd);
}
