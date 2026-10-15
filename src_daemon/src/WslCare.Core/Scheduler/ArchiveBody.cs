using WslCare.Core.Actions.Engine;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Records;

namespace WslCare.Core.Scheduler;

/// <summary>
/// The archive body of <c>tasks run archive</c> (WR-D5 of PLAN_windows_runner.md §3.5): which of the archive's two existing verbs the
/// supervised worker is. The timer's dry-run decision on the Windows paths (<see cref="DryRunWindow.Decide"/>, its own first-week stamp
/// under <c>%LOCALAPPDATA%\wsl-care</c>) is taken HERE, in the supervising process, before the worker's argv is chosen: a dry window
/// is <c>archive preview</c> (read-only, nothing is opened or written), a real one is <c>archive run</c>. Both spell the same closed
/// table — no free text reaches the argv.
/// </summary>
/// <remarks>
/// <para>The body takes NO lock of its own — not the run lock, never held across a wait: <c>archive run</c> takes the side's lock and
/// lease itself, so a long archive never suppresses a watch sample, and a second archive is refused (the side's lock first, then the lease) naming its holder.</para>
/// </remarks>
public static class ArchiveBody
{
    /// <summary>What a dry window runs: the existing read-only verb.</summary>
    private static IReadOnlyList<string> PreviewArguments { get; } = ["archive", "preview"];

    /// <summary>What a real run runs: the existing verb that moves sessions.</summary>
    private static IReadOnlyList<string> RunArguments { get; } = ["archive", "run"];

    /// <summary>The timer's decision for this run, gated BEFORE the dry-run stamp is touched (WatchRun.Windows' order): an observe-only
    /// configuration (a layer in error — <c>ConfigLoader</c> still merges the other entries, so <c>dryRun</c> may read false) and an
    /// elevated process (its worker refuses itself) both preview, and neither starts the first-week stamp. Otherwise
    /// <see cref="DryRunWindow.Decide"/> on the Windows paths, which writes the stamp when it is missing.</summary>
    public static DryRunDecision Decide(ConfigLoadResult loaded, bool elevated, IHostPaths paths, IFileSystem files, DateTimeOffset now) => loaded switch
    {
        { IsObserveOnly: true } => new DryRunDecision(true, $"{ActionEngine.ObserveOnlyReason}: {string.Join("; ", loaded.Errors.Select(e => e.Display))}"),
        _ when elevated => new DryRunDecision(true, "an elevated process: the worker refuses itself, and the dry-run week is not started"),
        _ => DryRunWindow.Decide(RunTrigger.Timer, loaded.Config, paths, files, now),
    };

    /// <summary>The worker a decision picks — the ONLY mapping from a decision to the archive's argv: dry previews, real runs.</summary>
    public static IReadOnlyList<string> ArgumentsFor(DryRunDecision decision) => decision.DryRun ? PreviewArguments : RunArguments;

    /// <summary>The line the supervisor says before the worker starts: why it is a preview, or why it is a real run.</summary>
    public static string Note(DryRunDecision decision) => decision.DryRun
        ? $"a dry run ({decision.Reason}): the worker is \"archive preview\", which moves nothing; end the window with \"config set dryRun false\" once the week has passed"
        : $"a real run ({decision.Reason}): the worker is \"archive run\"";
}
