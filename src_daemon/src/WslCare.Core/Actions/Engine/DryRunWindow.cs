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
/// The dry-run state as a timer run NOW would find it, read without writing anything (<see cref="DryRunWindow.Peek"/>, WR-D1 of
/// PLAN_windows_runner.md): what <c>tasks render</c> shows the person before the first task run.
/// </summary>
/// <param name="DryRun">Whether a timer run now would only preview.</param>
/// <param name="Setting">Whether the setting <c>dryRun</c> is on.</param>
/// <param name="Week">The dry-run week: one of <see cref="DryWeek"/>.</param>
/// <param name="Until">When the running week ends; <c>null</c> unless <see cref="Week"/> is <see cref="DryWeek.Running"/>.</param>
/// <param name="Reason">The sentence a person reads.</param>
public sealed record DryRunPeek(bool DryRun, bool Setting, string Week, DateTimeOffset? Until, string Reason);

/// <summary>The states of the dry-run week as <see cref="DryRunPeek.Week"/> names them.</summary>
public static class DryWeek
{
    /// <summary>No stamp yet: the first timer (or task) run starts the week.</summary>
    public const string NotStarted = "notStarted";

    /// <summary>A stamp that cannot be read: the next timer run rewrites it, so the week restarts.</summary>
    public const string Restarts = "restarts";

    /// <summary>The week runs until <see cref="DryRunPeek.Until"/>.</summary>
    public const string Running = "running";

    /// <summary>The week has passed.</summary>
    public const string Passed = "passed";
}

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

    /// <summary>What <see cref="Decide"/> would find for a timer run now — READ-ONLY (WR-D1 of PLAN_windows_runner.md): the stamp is
    /// read and never written, so showing the state (<c>tasks render</c>) never starts the week. A missing or unreadable stamp is a
    /// dry run, because the timer run that finds it writes now and starts the week there.</summary>
    public static DryRunPeek Peek(IHostPaths paths, IFileSystem files, EffectiveConfig config, DateTimeOffset now)
    {
        var setting = config.Bool(ConfigKeys.DryRun);
        var week = PeekWeek(ReadStamp(paths, files), paths, TimeSpan.FromDays(config.Int(ConfigKeys.Timer.FirstDryWindowDays)), now);
        return new DryRunPeek(setting || week.Dry, setting, week.State, week.Until, setting ? $"the setting dryRun is on; {week.Reason}" : week.Reason);
    }

    /// <summary>The week as a peek sees it.</summary>
    private sealed record WeekPeek(bool Dry, string State, DateTimeOffset? Until, string Reason);

    private static WeekPeek PeekWeek(FileReadResult read, IHostPaths paths, TimeSpan length, DateTimeOffset now) => (read, Recorded(read)) switch
    {
        (FileReadResult.Missing, _) => new(true, DryWeek.NotStarted, null, "the dry-run week has not started: the first timer or task run starts it"),
        (_, null) => new(true, DryWeek.Restarts, null, $"{File(paths)} could not be read: the next timer or task run restarts the dry-run week"),
        (_, { At: var at }) when now < at + length => new(true, DryWeek.Running, at + length, $"the first {length.TotalDays:0} days run dry: until {(at + length).UtcDateTime:yyyy-MM-dd HH:mm}Z"),
        (_, { At: var at }) => new(false, DryWeek.Passed, null, $"the {length.TotalDays:0} days since the first timer run ({at.UtcDateTime:yyyy-MM-dd}) have passed"),
    };

    /// <summary>The stamp, read the one way <see cref="Decide"/> and <see cref="Peek"/> both read it.</summary>
    private static FileReadResult ReadStamp(IHostPaths paths, IFileSystem files) => files.ReadFile(File(paths), RootFileCaps.State);

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
        var read = ReadStamp(paths, files);
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
