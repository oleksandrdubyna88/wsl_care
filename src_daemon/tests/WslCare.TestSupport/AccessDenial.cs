using System.Security.Principal;

namespace WslCare.TestSupport;

/// <summary>
/// Takes this account's access to one directory away for a test, and gives it back on dispose:
/// <c>chmod 000</c> on Linux, an inherited deny entry for the current user on Windows
/// (<c>icacls &lt;dir&gt; /deny *&lt;sid&gt;:(OI)(CI)(F)</c>, removed with <c>/remove:d</c>).
/// </summary>
/// <remarks>Measured 2026-10-02 on both families (Windows 11 and WSL Ubuntu, .NET 10): inside such a
/// directory <c>FileInfo.LinkTarget</c> answers <c>null</c> — it never throws — while
/// <c>FileInfo.Attributes</c> throws <see cref="UnauthorizedAccessException"/>. A denial that does
/// not take (an elevated or root account) is reported as <c>null</c> so the caller skips with that
/// reason instead of passing vacuously.</remarks>
public sealed class AccessDenial : IAsyncDisposable
{
    private readonly string _directory;
    private readonly string _sid;

    private AccessDenial(string directory, string sid)
    {
        _directory = directory;
        _sid = sid;
    }

    /// <summary>The denial, in force; <c>null</c> when this machine or account cannot be denied.</summary>
    public static async Task<AccessDenial?> TryDenyAsync(string directory)
    {
        var denial = new AccessDenial(directory, CurrentSid());
        await denial.ApplyAsync(deny: true);
        if (IsDenied(directory))
        {
            return denial;
        }

        await denial.DisposeAsync();
        return null;
    }

    public async ValueTask DisposeAsync() => await ApplyAsync(deny: false);

    private async Task ApplyAsync(bool deny)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_directory, deny ? UnixFileMode.None : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return;
        }

        string[] args = deny ? [_directory, "/deny", $"*{_sid}:(OI)(CI)(F)"] : [_directory, "/remove:d", $"*{_sid}"];
        var result = await ChildProcess.RunAsync("icacls.exe", args, new Dictionary<string, string?>());
        if (result.Exit != 0)
        {
            throw new InvalidOperationException($"icacls {string.Join(' ', args)} exited {result.Exit}: {result.Stdout}{result.Stderr}");
        }
    }

    private static string CurrentSid()
    {
        if (!OperatingSystem.IsWindows())
        {
            return string.Empty;
        }

        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new InvalidOperationException("the current Windows identity has no SID");
    }

    private static bool IsDenied(string directory)
    {
        try
        {
            _ = Directory.EnumerateFileSystemEntries(directory).Count();
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
