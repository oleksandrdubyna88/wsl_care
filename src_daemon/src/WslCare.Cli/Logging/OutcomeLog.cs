using Serilog;

using WslCare.Core.Actions.Engine;

namespace WslCare.Cli.Logging;

/// <summary>One action outcome as a run-log line — the ONE form for an <c>act</c> and a full run's timer pass: a failure is an
/// error, a deferral or a refusal a warning (plan §5: a deferral is logged), anything else information; each with its count
/// and freed bytes.</summary>
internal static class OutcomeLog
{
    private const string Template = "{Action} {Status}: {Reason} (count {Count}, freed {FreedBytes} bytes)";

    public static void Log(ILogger log, ActionOutcome outcome) =>
        At(log, outcome.Status)(Template, [outcome.Id, outcome.Status, outcome.Reason, Count(outcome), outcome.Run?.FreedBytes]);

    /// <summary>The level of an outcome's line: a failure an error, a deferral or a refusal a warning, anything else information.</summary>
    private static Action<string, object?[]?> At(ILogger log, string status) => status switch
    {
        ActionStatus.Failed => log.Error,
        ActionStatus.Deferred or ActionStatus.Refused => log.Warning,
        _ => log.Information,
    };

    /// <summary>The count the run measured, or — previewed only — the preview's.</summary>
    private static int? Count(ActionOutcome outcome) => outcome.Run is { } run ? run.Count : outcome.Preview?.Count;
}
