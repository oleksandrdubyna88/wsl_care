using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using WslCare.Core.Collectors;

namespace WslCare.Core.Mcp;

/// <summary>
/// Windows' boot counter and unbiased interrupt clock (E14 S7b.1) — READ-ONLY by construction: <c>RegGetValueW</c> reads one DWORD,
/// <c>BootId</c> under <c>HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters</c> (Windows
/// counts its boots there; readable unelevated, measured 117 on 2026-10-09), and <c>QueryUnbiasedInterruptTime</c> reads the
/// interrupt time WITHOUT the time the host slept — the ledger's denominator, as Linux's <c>CLOCK_MONOTONIC</c> is in the distro.
/// Nothing is opened for writing; no key is created.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class Win32Boot : IWindowsBoot
{
    private const string PrefetchParameters = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters";

    /// <summary><c>HKEY_LOCAL_MACHINE</c>, a predefined handle.</summary>
    private static readonly nint LocalMachine = unchecked((nint)(int)0x80000002);

    /// <summary><c>RRF_RT_REG_DWORD</c>: only a DWORD value is accepted.</summary>
    private const uint OnlyDword = 0x00000010;

    /// <summary>The unbiased interrupt time's unit: 100 ns.</summary>
    private const long TicksPerMillisecond = 10_000;

    public Reading<string> BootId()
    {
        var size = (uint)sizeof(uint);
        var status = RegGetValueW(LocalMachine, PrefetchParameters, "BootId", OnlyDword, 0, out var value, ref size);
        return status == 0
            ? Reading.Of("windows-" + value.ToString(CultureInfo.InvariantCulture))
            : Reading.Missing<string>("the Windows boot counter (PrefetchParameters\\BootId) could not be read: error " + status.ToString(CultureInfo.InvariantCulture));
    }

    public Reading<long> UnbiasedMilliseconds() =>
        QueryUnbiasedInterruptTime(out var ticks)
            ? Reading.Of((long)(ticks / TicksPerMillisecond))
            : Reading.Missing<long>("QueryUnbiasedInterruptTime failed: the unbiased clock could not be read");

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegGetValueW(nint key, string subKey, string value, uint flags, nint type, out uint data, ref uint dataSize);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);
}
