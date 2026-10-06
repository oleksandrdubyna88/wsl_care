using System.Text.Json;

using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Records;

namespace WslCare.Core.Actions.Engine;

/// <summary><c>{state}/first-timer-run.json</c>: when the TIMER first reached the actions — the start of its 7-day dry run.</summary>
public sealed record FirstTimerRun(int SchemaVersion, DateTimeOffset At);

/// <summary>Whether this run only previews, and why.</summary>
public sealed record DryRunDecision(bool DryRun, string Reason);

/// <summary>
/// Plan §5 <i>Global guards</i>: <c>dryRun = true</c> for the first 7 days for the TIMER; buttons always preview, then
/// execute on confirmation.
/// </summary>
/// <remarks>
/// <para><b>Where the 7 days start (E3.S1 decision).</b> At the first TIMER run that reaches the action pass — recorded once
/// in <c>{state}/first-timer-run.json</c> (root-written, like the rest of the state, plan §15b #3) — not at install: the
/// week is a week of the timer's own decisions, and an installer that ran days before any action existed must not have
/// used it up. A button never writes it.</para>
/// <para><b>Two locks on one door.</b> The timer is dry while EITHER the setting <c>dryRun</c> is on (its default) OR the
/// 7 days since that first run have not passed — switching <c>dryRun</c> off on day 2 does not end the week.</para>
/// <para><b>Conservative on every edge.</b> A stamp that cannot be read or parsed is rewritten with now (the week
/// restarts); a stamp that cannot be WRITTEN leaves the run dry. A stamp in the future (a clock jump) keeps the run dry
/// until 7 days after it.</para>
/// </remarks>
public static class DryRunWindow
{
    public const string FileName = "first-timer-run.json";

    public static TimeSpan Length => Tuning.Current.Days(ConfigKeys.Timer.FirstDryWindowDays);

    public static string File(IHostPaths paths) => paths.Rules.Join(paths.StateDirectory, FileName);

    public static DryRunDecision Decide(RunTrigger trigger, EffectiveConfig config, IHostPaths paths, IFileSystem files, DateTimeOffset now)
    {
        if (trigger != RunTrigger.Timer)
        {
            return new DryRunDecision(false, "a button or the CLI never dry-runs: it previews, then runs on confirmation (plan §5)");
        }

        var (start, note) = Start(paths, files, now);
        if (config.Bool(ConfigKeys.DryRun))
        {
            return new DryRunDecision(true, $"the setting dryRun is on{note}");
        }

        return Week(start, now, note);
    }

    /// <summary>With <c>dryRun</c> off: dry while the week since <paramref name="start"/> runs, or while its start is unknown.</summary>
    private static DryRunDecision Week(DateTimeOffset? start, DateTimeOffset now, string note) => start switch
    {
        null => new DryRunDecision(true, $"the start of the dry-run week is not recorded{note}"),
        { } at when now < at + Length => new DryRunDecision(true, $"the timer's first {Length.TotalDays:0} days run dry: until {(at + Length).UtcDateTime:yyyy-MM-dd HH:mm}Z{note}"),
        { } at => new DryRunDecision(false, $"dryRun is off and the {Length.TotalDays:0} days since the first timer run ({at.UtcDateTime:yyyy-MM-dd}) have passed"),
    };

    /// <summary>The recorded start, writing it when it is missing or unreadable; <c>null</c> when it could not be written.</summary>
    private static (DateTimeOffset? Start, string Note) Start(IHostPaths paths, IFileSystem files, DateTimeOffset now)
    {
        var read = files.ReadFile(File(paths), RootFileCaps.State);
        if (Recorded(read) is { } stamp)
        {
            return (stamp.At, string.Empty);
        }

        var why = WhyWritten(read, paths);
        return Write(paths, files, now) is { Length: > 0 } failure
            ? (null, $" ({why}; it could not be recorded: {failure})")
            : (now, $" ({why})");
    }

    private static FirstTimerRun? Recorded(FileReadResult read) => read is FileReadResult.Content content ? Parse(content.Bytes) : null;

    private static string WhyWritten(FileReadResult read, IHostPaths paths) =>
        read is FileReadResult.Missing ? "the first timer run starts the dry-run week" : $"{File(paths)} could not be read, so the week restarts";

    private static FirstTimerRun? Parse(byte[] bytes)
    {
        try
        {
            return JsonSerializer.Deserialize(bytes, WslCareJsonContext.Default.FirstTimerRun) is { At: var at } stamp && at != default ? stamp : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Empty when written; otherwise why not.</summary>
    private static string Write(IHostPaths paths, IFileSystem files, DateTimeOffset now)
    {
        try
        {
            files.CreateDirectory(paths.StateDirectory);
            var json = JsonSerializer.SerializeToUtf8Bytes(new FirstTimerRun(Core.SchemaVersion.Current, now), WslCareJsonContext.Default.FirstTimerRun);
            return files.WriteFileAtomically(File(paths), json, new DeletionScope(paths.StateDirectory, "dry-run-window")) is DeletionVerdict.Refused refused ? refused.Reason : string.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }
}
