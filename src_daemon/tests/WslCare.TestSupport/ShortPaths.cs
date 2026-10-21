using System.Runtime.InteropServices;

namespace WslCare.TestSupport;

/// <summary>
/// The 8.3 short spelling of an existing Windows path (<c>GetShortPathName</c>) — how a test makes the same folder reachable under two
/// names. A volume with 8.3 generation off answers the long path unchanged: the caller skips with that reason rather than passing vacuously.
/// </summary>
public static partial class ShortPaths
{
    public static string Of(string existingPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return existingPath;
        }

        var buffer = new char[1024];
        var length = GetShortPathName(existingPath, buffer, (uint)buffer.Length);
        return length == 0 || length >= buffer.Length ? existingPath : new string(buffer, 0, (int)length);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetShortPathNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetShortPathName(string longPath, [Out] char[] shortPath, uint size);
}
