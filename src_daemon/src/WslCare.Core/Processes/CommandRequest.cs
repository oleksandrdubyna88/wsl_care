namespace WslCare.Core.Processes;

/// <summary>
/// One external command: an executable and its arguments as a LIST (never a shell string), the
/// ceiling on how long it may run, and how much of its output is kept.
/// </summary>
/// <remarks>
/// <para>The timeout is a constructor argument rather than an optional property because a wait
/// without a ceiling is the defect the family's reliability rule exists for; a caller that wants a
/// long one says so in numbers. The output cap bounds memory against a child that prints forever —
/// what is kept is the first <see cref="OutputCapChars"/> characters of each stream, and the outcome
/// says it was cut.</para>
/// <para>No shell is ever involved: <c>Argv[0]</c> is started directly and the rest are passed as
/// separate arguments, so nothing in them is interpreted. Whether <c>Argv</c> is an ALLOWED command
/// is the <see cref="ICommandPolicy"/>'s question, asked by the runner before the start.</para>
/// </remarks>
public sealed record CommandRequest
{
    /// <summary>Longer than any action the daemon runs (systemd stops the unit at 10 minutes, plan §8).</summary>
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromHours(24);

    public const int DefaultOutputCapChars = 1024 * 1024;

    public CommandRequest(IReadOnlyList<string> argv, TimeSpan timeout, string workingDirectory = "")
    {
        if (argv.Count == 0 || string.IsNullOrWhiteSpace(argv[0]))
        {
            throw new ArgumentException("a command needs an executable as its first element", nameof(argv));
        }

        if (timeout <= TimeSpan.Zero || timeout > MaxTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, $"a command's timeout must be positive and at most {MaxTimeout}");
        }

        Argv = argv;
        Timeout = timeout;
        WorkingDirectory = workingDirectory;
    }

    public IReadOnlyList<string> Argv { get; }

    public TimeSpan Timeout { get; }

    /// <summary>Empty means the daemon's own working directory.</summary>
    public string WorkingDirectory { get; }

    public int OutputCapChars { get; init; } = DefaultOutputCapChars;

    /// <summary>The environment the child starts with: this process's own (every read command of E2), or a CLEAN one
    /// holding exactly the variables given — what a tool run as the target user gets (plan §15c #2).</summary>
    public CommandEnvironment Environment { get; init; } = CommandEnvironment.Inherited;

    /// <summary>The command as a person would read it in a log — for messages only, never executed.</summary>
    public string Display => string.Join(' ', Argv);
}

/// <summary>The environment a child starts with — a closed set, so the runner's choice is complete by inspection.</summary>
public abstract record CommandEnvironment
{
    private CommandEnvironment()
    {
    }

    /// <summary>This process's environment, unchanged.</summary>
    public static readonly CommandEnvironment Inherited = new InheritedEnvironment();

    /// <summary>Nothing of this process's environment: exactly <paramref name="Variables"/>.</summary>
    public sealed record Clean(IReadOnlyDictionary<string, string> Variables) : CommandEnvironment;

    private sealed record InheritedEnvironment : CommandEnvironment;
}
