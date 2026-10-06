using System.Globalization;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Health;
using WslCare.Core.Hosting;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Systemd;
using WslCare.Core.Thresholds;

namespace WslCare.Core.Actions;

/// <summary>One journal file as a walk found it.</summary>
/// <param name="Archived">Its name holds <c>@</c> (<c>system@….journal</c>): journald has closed it. Only archived files
/// are ever vacuumed — the active <c>system.journal</c> never.</param>
public sealed record JournalFile(string Path, long Bytes, DateTimeOffset ModifiedAt, bool Archived);

/// <summary>
/// A10 (plan §5): <c>journalctl --vacuum-time=&lt;journal.keepDays&gt;d</c> — journald removes the ARCHIVED journal files
/// whose newest entry is older than the limit; the active ones are never touched. Auto trigger: the journal above 1 GiB
/// (<see cref="ThresholdRules.JournalWarnGib"/>, the same figure the health report shows). The REFERENCE action of E3.S1:
/// the safest one, machine-scoped, light (never waits for idle), and journald's own command.
/// </summary>
/// <remarks>
/// <para><b>Preview</b> (live): journald's own size (<c>journalctl --disk-usage</c>, the figure <c>collect</c> reports and
/// the trigger compares) and, for what would go, the archived files under <c>/var/log/journal</c> and
/// <c>/run/log/journal</c> whose LAST WRITE is older than the limit — an archived file's last write is when its newest
/// entry was written, which is what journald compares; an estimate, and named as one in the basis.</para>
/// <para><b>Freed bytes are MEASURED</b> (plan §5): the walk before and after the command, and the freed figure is the size
/// (read before) of exactly the files that are gone after; the totals before and after are recorded beside it.</para>
/// </remarks>
public sealed class JournalVacuum : ICleanupAction
{
    /// <summary>The fact the trigger reads: journald's own size in bytes.</summary>
    public const string JournalBytesFact = "journalBytes";

    private const long Gib = 1L << 30;

    /// <summary>journald's size — the read the health collector makes too.</summary>
    public static readonly CommandTemplate DiskUsage = CommandTemplate.Fixed(() => SystemdCommands.JournalDiskUsage);

    /// <summary>The vacuum: one slot, the age in whole days, bounded as the setting is.</summary>
    public static readonly CommandTemplate Vacuum = new(
        "journalctl-vacuum-time",
        CommandScope.Machine,
        SystemdCommands.Journalctl,
        [new ArgPart.Slot("keep", new SlotKind.Prefixed("--vacuum-time=", new SlotKind.Number(1, 3650, "d")))],
        ConfigKeys.Journal.VacuumTimeoutSeconds,
        ConfigKeys.Commands.OutputCapBytes);

    public ActionId Id { get; } = ActionId.Find("A10")!;

    public string Summary => "journalctl --vacuum-time: remove archived journal files older than journal.keepDays";

    public CommandScope Scope => CommandScope.Machine;

    public IdleRule Idle => IdleRule.Never;

    public IReadOnlyList<HostSide> Sides { get; } = [HostSide.Wsl];

    public IReadOnlyList<CommandTemplate> Commands { get; } = [DiskUsage, Vacuum];

    public async Task<ActionPreview> PreviewAsync(ActionContext context, ActionCommands commands, CancellationToken cancellationToken)
    {
        var keep = context.Config.Int(ConfigKeys.Journal.KeepDays);
        var what = $"archived journal files with nothing newer than {keep} days (journalctl --vacuum-time={keep}d)";
        if (context.Paths is not LinuxHostPaths linux)
        {
            return ActionPreview.Unavailable(what, "the journal is the WSL distro's");
        }

        var usage = ToolAnswers.Read(SystemdCommands.JournalDiskUsage, await commands.RunAsync(DiskUsage, [], cancellationToken).ConfigureAwait(false)).Bind(JournalDiskUsage.Parse);
        if (usage is not Reading<long>.Available { Value: var bytes })
        {
            return ActionPreview.Unavailable(what, usage.ReasonOrEmpty);
        }

        var cutoff = context.Clock.GetUtcNow().AddDays(-keep);
        var old = Walk(linux, context.Files).Where(f => f.Archived && f.ModifiedAt < cutoff).ToList();
        return new ActionPreview(
            what,
            true,
            null,
            old.Count,
            old.Sum(f => f.Bytes),
            "archived journal files whose last write is older than the limit (an estimate: journald compares each file's newest entry)",
            new Dictionary<string, long>(StringComparer.Ordinal) { [JournalBytesFact] = bytes },
            string.Empty,
            [.. old.Take(ActionPreview.MaxItems).Select(f => new ActionItem("journal file", f.Path, f.Bytes, $"last write {f.ModifiedAt.UtcDateTime:yyyy-MM-dd}"))]);
    }

    /// <summary>Plan §5 A10: the journal above 1 GiB.</summary>
    public TriggerDecision Trigger(ActionPreview preview, EffectiveConfig config)
    {
        var limit = (long)(ThresholdRules.JournalWarnGib * Gib);
        return preview.Facts.TryGetValue(JournalBytesFact, out var bytes)
            ? new TriggerDecision(bytes > limit, string.Create(CultureInfo.InvariantCulture, $"the journal takes {bytes / (double)Gib:0.00} GiB; the trigger is above {ThresholdRules.JournalWarnGib:0.#} GiB"))
            : new TriggerDecision(false, "the journal's size was not read");
    }

    public async Task<ActionRun> RunAsync(ActionContext context, ActionPreview preview, ActionCommands commands, CancellationToken cancellationToken)
    {
        var linux = (LinuxHostPaths)context.Paths;
        var keep = context.Config.Int(ConfigKeys.Journal.KeepDays);
        var before = Walk(linux, context.Files);
        var outcome = await commands.RunAsync(Vacuum, [string.Create(CultureInfo.InvariantCulture, $"--vacuum-time={keep}d")], cancellationToken).ConfigureAwait(false);
        var after = Walk(linux, context.Files).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var removed = before.Where(f => !after.Contains(f.Path)).ToList();
        var afterBytes = Walk(linux, context.Files).Sum(f => f.Bytes);
        return new ActionRun(
            removed.Count,
            removed.Sum(f => f.Bytes),
            "the sizes, read before the vacuum, of the journal files that are gone after it",
            before.Sum(f => f.Bytes),
            afterBytes,
            [.. removed.Select(f => new ActionItem("journal file", f.Path, f.Bytes, $"last write {f.ModifiedAt.UtcDateTime:yyyy-MM-dd}"))],
            commands.Ran,
            Failure(outcome));
    }

    /// <summary>Every journal file in journald's two stores, through the seam (a stat each, nothing read).</summary>
    public static IReadOnlyList<JournalFile> Walk(LinuxHostPaths paths, IFileSystem files) =>
        [.. paths.JournalDirectories
            .SelectMany(root => files.ListFiles(root).Concat(files.ListDirectories(root).SelectMany(files.ListFiles)))
            .Where(IsJournal)
            .SelectMany(path => files.FileSize(path) is FileSizeResult.Measured m ? [new JournalFile(path, m.Bytes, m.ModifiedAt, Name(path).Contains('@'))] : Array.Empty<JournalFile>())];

    private static bool IsJournal(string path) => Name(path).EndsWith(".journal", StringComparison.Ordinal) || Name(path).EndsWith(".journal~", StringComparison.Ordinal);

    private static string Name(string path) => path[(path.Replace('\\', '/').LastIndexOf('/') + 1)..];

    private static string Failure(CommandOutcome outcome) => outcome switch
    {
        CommandOutcome.Exited { ExitCode: 0 } => string.Empty,
        CommandOutcome.Exited e => $"journalctl --vacuum-time exited {e.ExitCode.ToString(CultureInfo.InvariantCulture)}{ToolAnswers.Said(e.Stderr.Text)}",
        CommandOutcome.TimedOut t => $"journalctl --vacuum-time did not end within {t.Timeout.TotalMinutes:0} min; its process tree was killed",
        CommandOutcome.FailedToStart f => $"journalctl could not be started: {f.Reason}",
        CommandOutcome.Refused r => $"the command was refused: {r.Reason}",
        _ => throw new System.Diagnostics.UnreachableException("CommandOutcome is a closed set"),
    };
}
