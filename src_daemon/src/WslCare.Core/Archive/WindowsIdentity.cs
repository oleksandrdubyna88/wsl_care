using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Microsoft.Win32.SafeHandles;

using WslCare.Core.Files;

namespace WslCare.Core.Archive;

/// <summary>
/// E9.S0 review round S3 — a folder's identity on Windows, read through a handle opened for its attributes only (no data, no
/// reparse point followed, every share mode): two spellings of one folder — an 8.3 name, a <c>subst</c> drive, a second path to the
/// same volume — have one identity. A base and a protected place overlap when either's identity is in the other's chain (the
/// folder and every existing folder above it).
/// </summary>
public static partial class WindowsIdentity
{
    /// <summary>The identities of <paramref name="path"/> and of every existing folder above it, nearest first; empty when the folder
    /// itself does not exist or cannot be opened — the spelling comparison then stands alone.</summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<FileIdentity> ChainOf(string path)
    {
        var chain = new List<FileIdentity>();
        for (var at = Path.GetFullPath(path); at is not null; at = Path.GetDirectoryName(at))
        {
            if (Of(at) is not [var identity])
            {
                break;
            }

            chain.Add(identity);
        }

        return chain;
    }

    /// <summary>Whether a base whose chain is <paramref name="baseChain"/> is, lies inside or holds the place whose chain is
    /// <paramref name="placeChain"/> — pure, over the two chains.</summary>
    public static bool Overlap(IReadOnlyList<FileIdentity> baseChain, IReadOnlyList<FileIdentity> placeChain) =>
        baseChain.Count > 0 && placeChain.Count > 0 && (baseChain.Contains(placeChain[0]) || placeChain.Contains(baseChain[0]));

    /// <summary>One folder's identity, or nothing when it cannot be opened.</summary>
    [SupportedOSPlatform("windows")]
    private static FileIdentity[] Of(string path)
    {
        using var handle = Native.CreateFile(path, Native.ReadAttributes, Native.ShareAll, IntPtr.Zero, Native.OpenExisting, Native.BackupSemantics | Native.OpenReparsePoint, IntPtr.Zero);
        return !handle.IsInvalid && Native.GetFileInformationByHandle(handle, out var info)
            ? [new FileIdentity(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow)]
            : [];
    }

    private static partial class Native
    {
        public const uint ReadAttributes = 0x80;
        public const uint ShareAll = 0x7;
        public const uint OpenExisting = 3;
        public const uint BackupSemantics = 0x02000000;
        public const uint OpenReparsePoint = 0x00200000;

        [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        public static partial SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);
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
