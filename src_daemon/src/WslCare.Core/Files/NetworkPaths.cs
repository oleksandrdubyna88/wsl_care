using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WslCare.Core.Files;

/// <summary>
/// Plan §15r, *E9 live gate step 8, first run (2026-10-10)* — how a base on a network share is told apart from a link and from a
/// local folder. Measured on the owner's NAS: <c>GetFinalPathNameByHandle</c> answers a mapped drive's files under their UNC root
/// (<c>V:\x</c> → <c>\\server\share\x</c>), and <c>FlushFileBuffers</c> on a FOLDER handle answers <c>ERROR_INVALID_FUNCTION</c> over
/// SMB — the redirector has no folder flush to give.
/// </summary>
/// <remarks>The archive does not rest on that flush alone: no source is removed until phase 2, a later run in a new process,
/// re-opens every archived copy in the base and re-hashes it against its index line; a copy an SMB server lost marks the entry
/// damaged and the source stays.</remarks>
internal static partial class NetworkPaths
{
    /// <summary><c>ERROR_INVALID_FUNCTION</c>: what the SMB redirector answers a folder flush.</summary>
    internal const int InvalidFunction = 1;

    /// <summary>Whether <paramref name="actual"/> (where the open folder really is) is <paramref name="judged"/>: the same path, or — on
    /// a mapped network drive — the same path under the drive's network root. Exact after that one prefix swap, so a link inside the
    /// share, another share or a local drive still differ.</summary>
    internal static bool InPlace(string actual, string judged, Func<string, string> networkRootOf) =>
        actual.Length > 0 && (Same(actual, judged) || Same(actual, UnderNetworkRoot(judged, networkRootOf)));

    /// <summary>A folder flush's error as the caller should read it: 0 when it flushed — or when a NETWORK path answered
    /// <see cref="InvalidFunction"/>, which is SMB's "no folder flush here"; every other error as it came.</summary>
    internal static int FolderFlushed(int error, bool remote) => error == InvalidFunction && remote ? 0 : error;

    /// <summary>Whether <paramref name="path"/> is on a network share: a UNC path, or a drive the system maps to one.</summary>
    internal static bool IsRemote(string path, Func<string, string> networkRootOf) =>
        IsUnc(path) || (DriveOf(path) is { Length: > 0 } drive && networkRootOf(drive).Length > 0);

    /// <summary>The network root this process's session maps <paramref name="drive"/> (<c>V:</c>) to; empty for a local drive or when
    /// the system cannot say.</summary>
    [SupportedOSPlatform("windows")]
    internal static string NetworkRootOf(string drive) => MappingOf(drive).Root;

    /// <summary>What <paramref name="drive"/> is in this process's logon session: a network drive and the share it maps to (empty when
    /// the system cannot say, a disconnected drive), or a local one.</summary>
    [SupportedOSPlatform("windows")]
    internal static DriveMapping MappingOf(string drive) =>
        Native.GetDriveType(drive + "\\") == Native.DriveRemote ? new DriveMapping(true, Connection(drive)) : DriveMapping.Local;

    /// <summary>The mapping on this machine — every drive local off Windows.</summary>
    internal static DriveMapping MappingOnThisMachine(string drive) => OperatingSystem.IsWindows() ? MappingOf(drive) : DriveMapping.Local;

    private static bool Same(string a, string b) => b.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The judged path under its drive's network root (a root's own trailing separator dropped, the own review, 4); empty when
    /// the drive is none.</summary>
    private static string UnderNetworkRoot(string judged, Func<string, string> networkRootOf) =>
        DriveOf(judged) is { Length: > 0 } drive && networkRootOf(drive) is { Length: > 0 } root ? root.TrimEnd('\\') + judged[drive.Length..] : string.Empty;

    /// <summary>The drive of a <c>X:\…</c> path (<c>X:</c>); empty for anything else.</summary>
    internal static string DriveOf(string path) => path is [var letter, ':', '\\', ..] && char.IsAsciiLetter(letter) ? path[..2] : string.Empty;

    private static bool IsUnc(string path) => path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal) && !path.StartsWith(@"\\.\", StringComparison.Ordinal);

    [SupportedOSPlatform("windows")]
    private static string Connection(string drive)
    {
        var buffer = new char[Native.LongestRemoteName];
        var length = buffer.Length;
        return Native.WNetGetConnection(drive, buffer, ref length) == 0 ? new string(buffer, 0, Math.Max(0, Array.IndexOf(buffer, '\0'))) : string.Empty;
    }

    [SupportedOSPlatform("windows")]
    private static partial class Native
    {
        public const uint DriveRemote = 4;

        /// <summary>The longest remote name <c>WNetGetConnectionW</c> is given room for (a UNC path's documented ceiling).</summary>
        public const int LongestRemoteName = 32768;

        [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW", StringMarshalling = StringMarshalling.Utf16)]
        public static partial uint GetDriveType(string root);

        [LibraryImport("mpr.dll", EntryPoint = "WNetGetConnectionW", StringMarshalling = StringMarshalling.Utf16)]
        public static partial int WNetGetConnection(string localName, [Out] char[] remoteName, ref int length);
    }
}

/// <summary>A drive as this logon session maps it: <paramref name="Remote"/> a network drive, <paramref name="Root"/> its share (empty
/// when the system cannot say). Drive letters are per logon session, so a mapping is asked, never remembered.</summary>
internal sealed record DriveMapping(bool Remote, string Root)
{
    public static DriveMapping Local { get; } = new(false, string.Empty);
}
