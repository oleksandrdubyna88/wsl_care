namespace WslCare.Core.Worktrees;

/// <summary>
/// The argv of git's three read-only questions about a build-output folder (PLAN_disk_hygiene_windows_E19.md E19.S4), built in ONE
/// place so the templates of <see cref="Processes.Policy.ReadCommandTemplates"/> and the code that asks hold the same words. No path
/// travels on argv except an orphan's admin folder: the repository is the WORKING DIRECTORY and the names are relative to it, which
/// keeps every path off the never-list's protected-path rule instead of working around it.
/// </summary>
/// <remarks>
/// <para>Every command begins with the same options: no file-system monitor (a monitor daemon started by a read would outlive it),
/// NO user excludes file (an empty <c>core.excludesFile</c>, measured 2026-10-15: without it the user's own rules answer as if they were
/// the repository's), no octal-escaped non-ASCII paths, and no optional locks (a <c>git status</c>-style index refresh must never
/// reset the idle signal the scan reads, M2 of 2026-10-15).</para>
/// <para><c>check-ignore</c> is asked WITHOUT <c>-z</c> (git refuses it without <c>--stdin</c>) and without
/// <c>--literal-pathspecs</c> (git refuses that too): the relative-name slot already refuses every glob and pathspec-magic
/// character, so a name is a literal anyway. <c>ls-files</c> keeps both.</para>
/// </remarks>
public static class GitCommands
{
    /// <summary>The bare program name the policy judges; the file started is <see cref="Processes.GitExecutable"/>'s, never a <c>PATH</c> lookup.</summary>
    public const string Executable = "git.exe";

    /// <summary>The options every question starts with.</summary>
    public static IReadOnlyList<string> Options { get; } = ["-c", "core.fsmonitor=false", "-c", "core.excludesFile=", "-c", "core.quotePath=false", "--no-optional-locks"];

    /// <summary>The word before an orphan's admin folder.</summary>
    public const string GitDirPrefix = "--git-dir=";

    /// <summary><c>rev-parse --show-toplevel</c>: which working tree git believes the working directory is in.</summary>
    public static IReadOnlyList<string> TopLevel() => [Executable, .. Options, "rev-parse", "--show-toplevel"];

    /// <summary><c>ls-files -z --cached</c>: the tracked files under each name.</summary>
    public static IReadOnlyList<string> TrackedFiles(IReadOnlyList<string> names) =>
        [Executable, .. Options, "--literal-pathspecs", "ls-files", "-z", "--cached", "--", .. names];

    /// <summary><c>check-ignore -v -n</c>: the rule that decides each name, one line per name.</summary>
    public static IReadOnlyList<string> CheckIgnore(IReadOnlyList<string> names) =>
        [Executable, .. Options, "check-ignore", "-v", "-n", "--", .. names];

    /// <summary>The orphan form: the repository named by <paramref name="adminFolder"/>, the working tree the working directory, the index
    /// not read (<c>--no-index</c>) — so only the ignore rules answer, and the tracked files are NOT known.</summary>
    public static IReadOnlyList<string> CheckIgnoreOrphan(string adminFolder, IReadOnlyList<string> names) =>
        [Executable, .. Options, GitDirPrefix + adminFolder, "--work-tree=.", "check-ignore", "--no-index", "-v", "-n", "--", .. names];
}
