using System.Globalization;

using WslCare.Core.Actions.Engine;
using WslCare.Core.Archive;
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
/// lease itself, so a long archive never suppresses a watch sample, and a second archive is refused by the lease naming its holder.</para>
/// </remarks>
public static class ArchiveBody
{
    /// <summary>What a dry window runs: the existing read-only verb.</summary>
    public static IReadOnlyList<string> PreviewArguments { get; } = ["archive", "preview"];

    /// <summary>What a real run runs: the existing verb that moves sessions.</summary>
    public static IReadOnlyList<string> RunArguments { get; } = ["archive", "run"];

    /// <summary>The body's choice for this run: the worker's arguments (without <c>--json</c>) and the decision that made it.</summary>
    public sealed record Choice(IReadOnlyList<string> Arguments, DryRunDecision Decision);

    /// <summary>Takes the timer's dry-run decision (which starts the first-week stamp when it is missing) and names the worker it picks.</summary>
    public static Choice Choose(EffectiveConfig config, IHostPaths paths, IFileSystem files, DateTimeOffset now) => Of(DryRunWindow.Decide(RunTrigger.Timer, config, paths, files, now));

    /// <summary>The worker a decision picks: dry previews, real runs.</summary>
    public static Choice Of(DryRunDecision decision) => new(decision.DryRun ? PreviewArguments : RunArguments, decision);

    /// <summary>The line the supervisor says before a dry run's worker starts.</summary>
    public static string DryNote(DryRunDecision decision) => $"a dry run ({decision.Reason}): the worker is \"archive preview\", which moves nothing; end the window with \"config set dryRun false\" once the week has passed";

    /// <summary>The run-log sentence of a preview: what a real run would move now (enabled agents only — a disabled one never moves).</summary>
    public static string WouldMove(ArchivePreviewReport report)
    {
        var due = report.Agents.Where(a => a.Enabled).ToList();
        return string.Create(CultureInfo.InvariantCulture, $"would move {due.Sum(a => a.DueUnits)} session(s), {due.Sum(a => a.DueBytes)} bytes");
    }
}
