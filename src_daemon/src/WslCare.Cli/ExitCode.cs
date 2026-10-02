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
    /// the schema refuses.</summary>
    Usage = 2,

    /// <summary>A defect in this binary: something it should have handled escaped. Always a bug.</summary>
    Internal = 70,

    /// <summary>Another run holds the lock (<c>collect</c>), or another follower runs (<c>events follow</c>);
    /// nothing was done. 75 is <c>EX_TEMPFAIL</c>: try again later.</summary>
    Busy = 75,

    /// <summary>Stopped by Ctrl+C or SIGTERM before it finished (128 + SIGINT, the shell convention).</summary>
    Interrupted = 130,
}
