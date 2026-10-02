namespace WslCare.Core.Processes;

/// <summary>
/// One read-only invocation of an external tool the collectors use (<c>docker</c>, <c>systemctl</c>,
/// <c>journalctl</c>): a stable name (for the log, the live contract's capture and the fixture files), the
/// executable, the arguments as a list — never a shell string — its ceiling and how much output is kept.
/// </summary>
/// <param name="Executable">Started by name; the operating system resolves it on <c>PATH</c>.</param>
/// <param name="Name">A stable short name — also the fixture file a capture of it is replayed from.</param>
/// <param name="Arguments">Everything after the executable.</param>
/// <param name="Ceiling">How long it may run before its process tree is killed (reliability rule).</param>
/// <param name="OutputCapChars">How much of each stream is kept; an answer cut by the cap is not parsed.</param>
public sealed record ToolCommand(string Executable, string Name, IReadOnlyList<string> Arguments, TimeSpan Ceiling, int OutputCapChars)
{
    /// <summary>The whole argv: the executable and its arguments.</summary>
    public IReadOnlyList<string> Argv => [Executable, .. Arguments];

    /// <summary>The command as a person reads it in a reason or a log line — never executed.</summary>
    public string Display => string.Join(' ', Argv);

    /// <summary>A short name for a reason a person reads, where <see cref="Display"/> would be a wall of text (the
    /// inspect template and a hundred ids); empty means <see cref="Display"/> is short enough.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>What a reason quotes: <see cref="Summary"/>, or the whole command.</summary>
    public string Shown => Summary.Length > 0 ? Summary : Display;

    public CommandRequest ToRequest() => new(Argv, Ceiling) { OutputCapChars = OutputCapChars };
}
