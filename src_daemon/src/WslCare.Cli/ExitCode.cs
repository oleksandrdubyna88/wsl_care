namespace WslCare.Cli;

/// <summary>
/// Every exit code <c>wsl-care</c> returns, in one place.
/// </summary>
/// <remarks>
/// The extension and the systemd unit read these, so a number never appears at a call site: a
/// caller that compares against a literal is the one still wrong after the code changes here.
/// </remarks>
internal enum ExitCode
{
    /// <summary>The request was answered.</summary>
    Ok = 0,

    /// <summary>The run could not be RECORDED: its detail or its history line failed to write (plan §15b #1), or
    /// <c>events follow</c> was started by a process that may not write the state directory. What was measured is
    /// still printed.</summary>
    RunFailed = 1,

    /// <summary>The arguments could not be read or accepted — an unknown verb, extra words, a value
    /// the schema refuses, an action this build does not hold, an action of the other side.</summary>
    Usage = 2,

    /// <summary><c>act</c>: the run was recorded and at least one action FAILED (the others ran — plan §5: a failing
    /// action is logged and the run continues). The answer names which and why.</summary>
    ActionFailed = 3,

    /// <summary><c>logs</c> / <c>runs</c>: <c>history.jsonl</c> exists but could not be read (permissions, I/O); the answer
    /// says why and holds no run. A history that does not exist yet is an empty answer, exit 0.</summary>
    RecordsUnreadable = 4,

    /// <summary>A defect in this binary: something it should have handled escaped. Always a bug.</summary>
    Internal = 70,

    /// <summary>Another run holds the run lock (<c>collect</c> or <c>act</c> — the second one refuses, it never waits), a
    /// live run is acting, or another follower runs (<c>events follow</c>); nothing was done. 75 is <c>EX_TEMPFAIL</c>:
    /// try again later.</summary>
    Busy = 75,

    /// <summary><c>act</c>: a run is WEDGED — its process is alive and its heartbeat stale — or the running state cannot be
    /// told (plan §15 #6). Nothing was done and nothing was killed; it waits for a person.</summary>
    Wedged = 76,

    /// <summary><c>act</c> started by a process that is not root (plan §15c #0): refused whole, before the lock or any
    /// state was touched. 77 is <c>EX_NOPERM</c>.</summary>
    NeedsRoot = 77,

    /// <summary><c>act --confirm</c> while a configuration layer is invalid (plan §15a #1: observe-only): nothing runs.
    /// 78 is <c>EX_CONFIG</c>.</summary>
    ObserveOnly = 78,

    /// <summary>Stopped by Ctrl+C or SIGTERM before it finished (128 + SIGINT, the shell convention).</summary>
    Interrupted = 130,
}
