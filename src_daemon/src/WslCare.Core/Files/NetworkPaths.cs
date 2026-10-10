using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Microsoft.Win32.SafeHandles;

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

    /// <summary>The guards after the live gate, G2 (owner question 2, 2026-10-10): why a NETWORK handle's answers are not trusted — its
    /// protocol says it answers from an offline cache (<c>REMOTE_PROTOCOL_FLAG_OFFLINE</c>, Windows Offline Files), or the protocol
    /// could not be asked (unknown keeps). Empty for a trusted share and for a local path, which is never asked. Measured on the owner's
    /// NAS 2026-10-10: SMB 3.1, flags 0x10 (integrity), the offline flag clear.</summary>
    internal static string OfflineCacheProblem(uint flags, int error, bool remote) =>
        !remote ? string.Empty
        : error != 0 ? string.Create(CultureInfo.InvariantCulture, $"whether the share answers from the Offline Files cache could not be asked (error {error}); what it answers is not trusted")
        : (flags & OfflineFlag) != 0 ? "the share answers from the Offline Files cache, so a copy it shows may not be on the server; what it answers is not trusted"
        : string.Empty;

    /// <summary>A folder flush's error as the caller should read it: 0 when it flushed — or when a NETWORK path answered
    /// <see cref="InvalidFunction"/>, which is SMB's "no folder flush here"; every other error as it came.</summary>
    internal static int FolderFlushed(int error, bool remote) => error == InvalidFunction && remote ? 0 : error;

    /// <summary><c>REMOTE_PROTOCOL_FLAG_OFFLINE</c> of <c>FILE_REMOTE_PROTOCOL_INFO.Flags</c>.</summary>
    internal const uint OfflineFlag = 0x2;

    /// <summary>The remote protocol's flags of an open handle, or the Win32 error of asking (a local handle answers one).</summary>
    [SupportedOSPlatform("windows")]
    internal static (uint Flags, int Error) RemoteProtocolFlags(SafeFileHandle handle)
    {
        var buffer = new byte[Native.RemoteProtocolInfoBytes];
        return Native.GetFileInformationByHandleEx(handle, Native.FileRemoteProtocolInfo, buffer, (uint)buffer.Length)
            ? (BitConverter.ToUInt32(buffer, Native.FlagsOffset), 0)
            : (0, Marshal.GetLastPInvokeError());
    }

    /// <summary>Why a handle opened at <paramref name="path"/> is not trusted (<see cref="OfflineCacheProblem"/>); a local path is never asked.</summary>
    [SupportedOSPlatform("windows")]
    internal static string OfflineCacheProblemOf(SafeFileHandle handle, string path) =>
        IsRemote(path, NetworkRootOf) && RemoteProtocolFlags(handle) is var (flags, error) ? OfflineCacheProblem(flags, error, remote: true) : string.Empty;

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

    /// <summary>The share <paramref name="drive"/> maps to: asked with a room that fits every ordinary share name, and once more with the
    /// room the system names when it does not fit (the code round: a 64 KiB buffer per question added up over thousands of files).</summary>
    [SupportedOSPlatform("windows")]
    private static string Connection(string drive)
    {
        var length = Native.FirstRemoteNameRoom;
        var first = Asked(drive, ref length);
        return first.Error == Native.MoreData && length <= Native.LongestRemoteName ? Asked(drive, ref length).Name : first.Name;
    }

    [SupportedOSPlatform("windows")]
    private static (int Error, string Name) Asked(string drive, ref int length)
    {
        var buffer = new char[length];
        var error = Native.WNetGetConnection(drive, buffer, ref length);
        return (error, error == 0 ? new string(buffer, 0, Math.Max(0, Array.IndexOf(buffer, '\0'))) : string.Empty);
    }

    [SupportedOSPlatform("windows")]
    private static partial class Native
    {
        public const uint DriveRemote = 4;

        /// <summary>The longest remote name <c>WNetGetConnectionW</c> is given room for (a UNC path's documented ceiling).</summary>
        public const int LongestRemoteName = 32768;

        /// <summary>The first room asked with: a server and a share name fit it many times over.</summary>
        public const int FirstRemoteNameRoom = 512;

        /// <summary><c>ERROR_MORE_DATA</c>: the room was too small, and the length now says how much is needed.</summary>
        public const int MoreData = 234;

        /// <summary><c>FileRemoteProtocolInfo</c> of <c>FILE_INFO_BY_HANDLE_CLASS</c>.</summary>
        public const int FileRemoteProtocolInfo = 13;

        /// <summary>Room for <c>FILE_REMOTE_PROTOCOL_INFO</c> with room to spare (the measured call took a 148-byte buffer).</summary>
        public const int RemoteProtocolInfoBytes = 256;

        /// <summary>Where <c>Flags</c> lies in it: after two USHORTs, a ULONG and four USHORTs.</summary>
        public const int FlagsOffset = 16;

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, [Out] byte[] buffer, uint size);

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
