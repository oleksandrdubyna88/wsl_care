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

    /// <summary>The arguments could not be read or accepted — an unknown verb, extra words, a value
    /// the schema refuses.</summary>
    Usage = 2,

    /// <summary>A defect in this binary: something it should have handled escaped. Always a bug.</summary>
    Internal = 70,

    /// <summary>Stopped by Ctrl+C or SIGTERM before it finished (128 + SIGINT, the shell convention).</summary>
    Interrupted = 130,
}
