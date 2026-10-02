using System.Diagnostics;

namespace WslCare.TestSupport;

/// <summary>
/// Makes a directory link for a test, by whatever means this machine allows: a symbolic link
/// where the account may create one (Linux; Windows with Developer Mode), otherwise a junction on
/// Windows (<c>mklink /J</c> needs no privilege). Measured 2026-10-02 on the owner's machine:
/// <c>Directory.CreateSymbolicLink</c> is refused ("a required privilege is not held"), and a
/// junction is seen by <c>FileInfo.LinkTarget</c> exactly like a symlink.
/// </summary>
public static class DirectoryLinks
{
    /// <summary><c>true</c> when a link now exists at <paramref name="linkPath"/>; <c>false</c> when this machine
    /// cannot make one — the caller skips with that reason rather than passing vacuously.</summary>
    public static bool TryCreate(string linkPath, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, target);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return OperatingSystem.IsWindows() && TryJunction(linkPath, target);
        }
    }

    private static bool TryJunction(string linkPath, string target)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(linkPath);
        start.ArgumentList.Add(target);
        using var process = Process.Start(start);
        if (process is null)
        {
            return false;
        }

        if (!process.WaitForExit(TimeSpan.FromSeconds(15)))
        {
            process.Kill(entireProcessTree: true);
            return false;
        }

        return process.ExitCode == 0 && new FileInfo(linkPath).LinkTarget is not null;
    }
}
