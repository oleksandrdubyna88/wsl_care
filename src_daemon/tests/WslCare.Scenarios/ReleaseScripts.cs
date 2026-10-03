using WslCare.Core.Processes;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>Runs the release scripts of <c>.github/scripts/</c> (E4.S2) as the workflows run them: under bash, as a child
/// process with an argv list and a ceiling — <c>/bin/bash</c> on Linux, Git for Windows' bash on Windows (what
/// <c>shell: bash</c> is on a Windows runner), so a script's Windows behaviour is seen before a release.</summary>
internal static class ReleaseScripts
{
    public const string LinuxOnly = "the release scripts are bash over GNU coreutils and tar: covered on the Linux legs (and by hand in WSL)";

    public const string NoBash = "no bash here: on Windows the release scripts run under Git for Windows' bash, which was not found beside git";

    /// <summary>The bash the workflows use on this OS, or empty: <c>/bin/bash</c>; on Windows the <c>bin\bash.exe</c> of the
    /// Git for Windows installation the <c>git</c> on PATH belongs to — never a bare <c>bash</c>, which on Windows may be
    /// <c>System32\bash.exe</c>, the WSL launcher.</summary>
    public static string Bash => OperatingSystem.IsWindows() ? GitForWindowsBash() : "/bin/bash";

    public static Task<ChildResult> RunAsync(string script, IReadOnlyList<string> args, string workingDirectory, IReadOnlyDictionary<string, string?>? environment = null) =>
        ChildProcess.RunAsync(Bash, [ReleaseFiles.Script(script).Replace('\\', '/'), .. args], environment ?? new Dictionary<string, string?>(), workingDirectory, TimeSpan.FromSeconds(60));

    /// <summary>An archive's file name as the asset contract itself computes it (<c>daemon_archive_name</c>), never retyped.</summary>
    public static async Task<string> ArchiveNameAsync(string version, string rid)
    {
        var result = await ChildProcess.RunAsync(
            Bash,
            ["-c", ". \"$1\" && daemon_archive_name \"$2\" \"$3\"", "contract", ReleaseFiles.AssetContract.Replace('\\', '/'), version, rid],
            new Dictionary<string, string?>());
        return result.Exit == 0 ? result.Stdout.Trim() : throw new InvalidOperationException($"the asset contract names no archive for {rid}: {result.Stderr}");
    }

    /// <summary>git.exe sits in <c>cmd\</c>, <c>bin\</c> or <c>mingw64\bin\</c> of the installation; its <c>bin\bash.exe</c> is
    /// one, two or three folders up.</summary>
    private static string GitForWindowsBash() =>
        ExecutableResolver.Resolve("git") is ResolvedExecutable.Found git
            ? Ancestors(Path.GetDirectoryName(git.Path)).Select(dir => Path.Combine(dir, "bin", "bash.exe")).FirstOrDefault(File.Exists) ?? string.Empty
            : string.Empty;

    private static IEnumerable<string> Ancestors(string? directory)
    {
        for (var dir = directory; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            yield return dir;
        }
    }
}
