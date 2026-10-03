using WslCare.Core.Processes;

namespace WslCare.Core.Actions;

/// <summary>How a command an action ran ended, in the words its run detail uses — the same for every cleanup.</summary>
public static class CommandFailures
{
    /// <summary>Empty for a command that ran to an exit; otherwise why it did not.</summary>
    public static string NotRun(string shown, CommandOutcome outcome) => outcome switch
    {
        CommandOutcome.Exited => string.Empty,
        CommandOutcome.TimedOut t => $"{shown} did not end within {t.Timeout.TotalMinutes:0} min; its process tree was killed",
        CommandOutcome.FailedToStart f => $"{shown} could not be started: {f.Reason}",
        CommandOutcome.Refused r => $"{shown} was refused: {r.Reason}",
        _ => throw new System.Diagnostics.UnreachableException("CommandOutcome is a closed set"),
    };

    /// <summary>Empty for exit 0; otherwise the exit code and what the tool said.</summary>
    public static string Of(string shown, CommandOutcome outcome) => outcome switch
    {
        CommandOutcome.Exited { ExitCode: 0 } => string.Empty,
        CommandOutcome.Exited e => $"{shown} exited {e.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)}{Health.ToolAnswers.Said(e.Stderr.Text)}",
        _ => NotRun(shown, outcome),
    };
}
