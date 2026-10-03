using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>Runs the release scripts of <c>.github/scripts/</c> (E4.S2) as release.yml runs them: under bash, as a child
/// process with an argv list and a ceiling — on Linux, where the release legs that matter for them run.</summary>
internal static class ReleaseScripts
{
    public const string LinuxOnly = "the release scripts are bash over GNU coreutils and tar: covered on the Linux legs (and by hand in WSL)";

    public static Task<ChildResult> RunAsync(string script, IReadOnlyList<string> args, string workingDirectory, IReadOnlyDictionary<string, string?>? environment = null) =>
        ChildProcess.RunAsync("/bin/bash", [ReleaseFiles.Script(script), .. args], environment ?? new Dictionary<string, string?>(), workingDirectory, TimeSpan.FromSeconds(60));

    /// <summary>An archive's file name as the asset contract itself computes it (<c>daemon_archive_name</c>), never retyped.</summary>
    public static async Task<string> ArchiveNameAsync(string version, string rid)
    {
        var result = await ChildProcess.RunAsync(
            "/bin/bash",
            ["-c", ". \"$1\" && daemon_archive_name \"$2\" \"$3\"", "contract", ReleaseFiles.AssetContract, version, rid],
            new Dictionary<string, string?>());
        return result.Exit == 0 ? result.Stdout.Trim() : throw new InvalidOperationException($"the asset contract names no archive for {rid}: {result.Stderr}");
    }

    /// <summary>The first directory on this process's PATH holding <paramref name="tool"/>, or empty.</summary>
    public static string OnPath(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(dir => File.Exists(Path.Combine(dir, tool))) ?? string.Empty;
}
