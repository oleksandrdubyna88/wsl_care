using WslCare.Core.Config;
using System.Globalization;

using WslCare.Core.Collectors;
using WslCare.Core.Processes;

namespace WslCare.Core.Health;

/// <summary>
/// The outcome of one read-only tool command (<c>systemctl</c>, <c>journalctl</c>, <c>timedatectl</c>, <c>snap</c>,
/// <c>powershell.exe</c>) as its stdout, or WHY there is none — in a sentence a person can act on, so every figure
/// that depends on it is unavailable with that reason and never a 0 (plan §15b #7).
/// </summary>
public static class ToolAnswers
{
    private static int QuoteChars => Tuning.Current.Int(ConfigKeys.Records.MaxReasonChars);

    public static async Task<Reading<string>> RunAsync(ICommandRunner runner, ToolCommand command, CancellationToken cancellationToken) =>
        Read(command, await runner.RunAsync(command.ToRequest(), cancellationToken).ConfigureAwait(false));

    /// <summary>Pure, so every failure is a unit test.</summary>
    public static Reading<string> Read(ToolCommand command, CommandOutcome outcome) => outcome switch
    {
        CommandOutcome.Exited { ExitCode: 0, Stdout.Truncated: true } => Reading.Missing<string>($"{command.Shown} printed more than {command.OutputCapChars} characters; the answer was cut and is not read"),
        CommandOutcome.Exited { ExitCode: 0 } exited => Reading.Of(exited.Stdout.Text),
        CommandOutcome.Exited exited when exited.Stderr.Text.Contains("not been booted with systemd", StringComparison.Ordinal) =>
            Reading.Missing<string>($"this system was not booted with systemd, so {command.Executable} cannot answer"),
        CommandOutcome.Exited exited => Reading.Missing<string>($"{command.Shown} exited {exited.ExitCode.ToString(CultureInfo.InvariantCulture)}{Said(exited.Stderr.Text)}"),
        CommandOutcome.TimedOut timedOut => Reading.Missing<string>($"{command.Shown} did not answer within {timedOut.Timeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s; its process tree was killed"),
        CommandOutcome.FailedToStart failed => Reading.Missing<string>($"{command.Executable} could not be started: not installed, or not on PATH ({failed.Reason})"),
        CommandOutcome.Refused refused => Reading.Missing<string>($"the command policy refused {command.Shown}: {refused.Reason}"),
        _ => throw new System.Diagnostics.UnreachableException("CommandOutcome is a closed set"),
    };

    /// <summary>The first non-empty line of stderr, cut — a reason is a sentence, not a log.</summary>
    internal static string Said(string stderr)
    {
        var line = stderr.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
        return line.Length == 0 ? ", nothing on stderr" : ": " + (line.Length <= QuoteChars ? line : line[..QuoteChars] + "...");
    }
}
