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

    /// <summary><c>logs</c> / <c>runs</c> / <c>runs show</c>: <c>history.jsonl</c> exists but could not be read (permissions,
    /// I/O); the answer says why and holds no run from it. A history that does not exist yet is an empty answer, exit 0.</summary>
    RecordsUnreadable = 4,

    /// <summary><c>act --detach</c> / <c>collect --detach</c> on a machine without systemd (no <c>/run/systemd/system</c>): a
    /// detached run needs its template unit, and there is NO synchronous fallback (plan §15j B2). Nothing was written.
    /// 69 is <c>EX_UNAVAILABLE</c>.</summary>
    DetachUnavailable = 69,

    /// <summary>A defect in this binary: something it should have handled escaped. Always a bug.</summary>
    Internal = 70,

    /// <summary><c>--detach</c>: the request was written but <c>systemctl start --no-block wsl-care-act@&lt;runId&gt;.service</c> did
    /// not succeed — the request was REMOVED again, so nothing waits for a unit that will never run (plan §15k #1). 71 is
    /// <c>EX_OSERR</c>.</summary>
    DetachStartFailed = 71,

    /// <summary><c>--detach</c>: the request folder already holds its budget of requests (plan §15k #8 + #17: 32); nothing
    /// was written. 73 is <c>EX_CANTCREAT</c>.</summary>
    QueueFull = 73,

    /// <summary>Another run holds the run lock (<c>collect</c> or <c>act</c> — the second one refuses, it never waits), a
    /// live run is acting, or another follower runs (<c>events follow</c>); nothing was done. 75 is <c>EX_TEMPFAIL</c>:
    /// try again later.</summary>
    Busy = 75,

    /// <summary><c>act</c>: a run is WEDGED — its process is alive and its heartbeat stale — or its process cannot be
    /// inspected (plan §15 #6). Nothing was done and nothing was killed; it waits for a person.</summary>
    Wedged = 76,

    /// <summary><c>act</c> started by a process that is not root (plan §15c #0): refused whole, before the lock or any
    /// state was touched. 77 is <c>EX_NOPERM</c>.</summary>
    NeedsRoot = 77,

    /// <summary><c>act --confirm</c> while a configuration layer is invalid (plan §15a #1: observe-only): nothing runs.
    /// 78 is <c>EX_CONFIG</c>.</summary>
    ObserveOnly = 78,

    /// <summary><c>act</c>: <c>running.json</c> cannot be read or parsed, even after brief retries (gate finding #7) — its own
    /// state, not a wedged live run. Nothing was done and nothing was killed; the answer names the file and the reason.</summary>
    StateUnreadable = 79,

    /// <summary><c>act --request &lt;runId&gt;</c> (the template unit's start): no request names that run — it was swept, or a
    /// stray start. A no-op: no history line, nothing run (plan §15k #2); a success exit of the unit.</summary>
    RequestGone = 80,

    /// <summary>A verb that must run as the USER started as root (plan §15q D4, review C3; E7.S1/S2 review round): <c>agents probe</c>
    /// (it runs as the user who owns the CLI, never as uid 0) and <c>config set</c> / <c>config reset</c> for the target user (a
    /// root-owned file in their home would lock them out) — refused whole, naming the fix.</summary>
    NotAsRoot = 81,

    /// <summary><c>act --request &lt;runId&gt;</c>: the request cannot be used (the hardened reader refuses it) - RECORDED as
    /// <c>refused</c> with the reason and removed; nothing was run. A success exit of the unit (retro round over PR #11, O3: it
    /// used to exit 2, the usage error, which the unit counted as a failure).</summary>
    RequestUnusable = 82,

    /// <summary>Stopped by Ctrl+C or SIGTERM before it finished (128 + SIGINT, the shell convention). Under a unit the only signal
    /// is systemd's SIGTERM - a stop someone asked for (<c>act --stop</c>, <c>systemctl stop</c>, a shutdown) - and the run records
    /// itself <c>interrupted</c> first (or leaves running.json / its request for the sweep), so both units count it as a success
    /// exit (retro round over PR #11, O3).</summary>
    Interrupted = 130,
}
