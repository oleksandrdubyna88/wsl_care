using System.Globalization;

using WslCare.Core.Actions;
using WslCare.Core.Config;
using WslCare.Core.Hosting;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;

namespace WslCare.Core.Archive;

/// <summary>What an archive child printed: its answer line, or why there is none.</summary>
public sealed record ChildText(string Text, string Failure)
{
    public static ChildText Of(string text) => new(text, string.Empty);

    public static ChildText Failed(string why) => new(string.Empty, why);

    /// <summary>What root's record of its children could not keep while this child ran (plan §15r E9.S4 own review round C-7) — a
    /// launch or a worker not written, a record not retired — each in words; empty when every write held.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>The first write that left a LIVE child out of root's record (its launch or its worker); empty when none did. Root
    /// then cannot see a child left stuck, so the action says so and no further child starts (C-7).</summary>
    public string Unrecorded { get; init; } = string.Empty;
}

/// <summary>
/// The parts A13 and A20 share (plan §15r E9.S4): the gates before any child starts (no base, a <c>runuser</c> stack that would
/// make a login session, a product binary that is not root's alone, a recorded child still alive), and how a child is run and
/// read — its answer the one line it prints (whole), or for a streamed one the last line after its progress
/// (<see cref="ArchiveChildStream"/>), its launcher and worker recorded while they live (<see cref="ArchiveChildren"/>).
/// </summary>
public static class ArchiveGates
{
    /// <summary>The preview that stops the action before any child starts; <c>null</c> when none does.</summary>
    public static ActionPreview? Before(ActionContext context, ActionCommands commands, CommandTemplate first, CancellationToken cancellationToken) =>
        context.Config.Text(ConfigKeys.Archive.BaseFolder).Length == 0 ? Skipped(ArchiveAction.NoArchive)
        : StartRefusal(context, commands, first) is { Length: > 0 } refused ? Refused(refused)
        : Gated(ArchiveChildren.Survivor(context, cancellationToken));

    /// <summary>A recorded child still alive skips (it waits for it); a record root cannot keep refuses (C-7); <c>null</c> when free.</summary>
    private static ActionPreview? Gated(ChildGate gate) =>
        !gate.Blocks ? null : gate.Refuses ? Refused(gate.Why) : Skipped(gate.Why);

    /// <summary>Why no child may start here at all: a <c>runuser</c> PAM stack that would make a login session (risk consult 9/9.4
    /// #2), or a product binary that is not root's alone (D1); empty when neither.</summary>
    private static string StartRefusal(ActionContext context, ActionCommands commands, CommandTemplate first) =>
        context.Paths is LinuxHostPaths linux && RunuserPam.Problem(linux, context.Files) is { Length: > 0 } pam ? pam
        : commands.Locate(first) is ResolvedExecutable.NotFound missing ? $"{first.Name} starts only the product's own root-owned binary: {missing.Reason}"
        : string.Empty;

    /// <summary>A13's own first child is the preview.</summary>
    public static ActionPreview? Before(ActionContext context, ActionCommands commands, CancellationToken cancellationToken) =>
        Before(context, commands, ArchiveChildren.Preview, cancellationToken);

    public static ActionPreview Skipped(string why) => Empty() with { Skip = why };

    private static ActionPreview Refused(string why) => Empty() with { Refusal = why };

    private static ActionPreview Empty() => ActionPreview.Of("archive", 0, 0, "nothing was asked of the archive child", new Dictionary<string, long>(), string.Empty, []);

    /// <summary>A run that measured nothing, and why.</summary>
    public static ActionRun Failed(ActionCommands commands, string why) => new(0, null, "nothing was measured", null, null, [], commands.Ran, why);

    public static string UserOf(ActionContext context) => context.TargetUser is TargetUserResult.Found found ? found.User.Name : "the target user";

    /// <summary>A whole child's answer: its one line, when it exited with one of <paramref name="exits"/> and its output was not cut —
    /// its launcher recorded at its start and its worker right before a kill, both retired at its end (the S4 own review round S-M1:
    /// every child root starts is recorded, not only the streamed ones).</summary>
    public static async Task<ChildText> ChildTextAsync(ActionContext context, ActionCommands commands, CommandTemplate template, IReadOnlyList<int> exits, CancellationToken cancellationToken)
    {
        var watch = new ChildWatch(context, template.Name, cancellationToken);
        ChildText text;
        try
        {
            var outcome = await commands.RunAsync(template, [], watch.Hooks, cancellationToken).ConfigureAwait(false);
            text = outcome is CommandOutcome.Exited exited ? Whole(template, exited, exits) : NotAnswered(template, outcome);
        }
        finally
        {
            watch.End();
        }

        return watch.Of(text);
    }

    /// <summary>A preview whose child root could not record (C-7) refuses: no further child starts while a stuck one would not be seen.</summary>
    public static ActionPreview Contained(ActionPreview preview, ChildText text) =>
        text.Unrecorded.Length > 0 && Open(preview) ? preview with { Refusal = Uncontained(text.Unrecorded) } : preview;

    /// <summary>A run carries what root's record could not keep in its notes; a live child left out of it fails the run (C-7).</summary>
    public static ActionRun Contained(ActionRun run, ChildText text) =>
        run with
        {
            Notes = [.. run.Notes, .. text.Notes],
            Failure = text.Unrecorded.Length > 0 && run.Succeeded ? Uncontained(text.Unrecorded) : run.Failure,
        };

    private static bool Open(ActionPreview preview) => preview.Available && preview.Skip.Length == 0 && preview.Refusal.Length == 0;

    /// <summary>Why no further child starts: root could not record one it started (C-7).</summary>
    public static string Uncontained(string why) =>
        $"root could not record the archive child it started ({why}): a child left stuck would not be seen, so no further one starts until {ArchiveChildren.FileName} can be written";

    /// <summary>An exited whole child: its last line, unless its output was cut or its exit is not one it answers with.</summary>
    private static ChildText Whole(CommandTemplate template, CommandOutcome.Exited exited, IReadOnlyList<int> exits) =>
        exited.Stdout.Truncated ? ChildText.Failed($"{template.Name}: its answer passed {ConfigKeys.Archive.ChildOutputCapBytes.Name}")
        : exits.Contains(exited.ExitCode) && LastLine(exited.Stdout.Text) is { Length: > 0 } line ? ChildText.Of(line)
        : NoAnswer(template, exited);

    private static ChildText NoAnswer(CommandTemplate template, CommandOutcome.Exited exited) =>
        ChildText.Failed(string.Create(CultureInfo.InvariantCulture, $"{template.Name} exited {exited.ExitCode} without an answer: {FirstLine(exited.Stderr.Text)}"));

    /// <summary>A child that did not run to its end: killed at its ceiling, not started, or refused.</summary>
    private static ChildText NotAnswered(CommandTemplate template, CommandOutcome outcome) => outcome switch
    {
        CommandOutcome.TimedOut t => ChildText.Failed(string.Create(CultureInfo.InvariantCulture, $"{template.Name} ran past its ceiling ({t.Timeout.TotalMinutes:0.#} min) and was killed; what it confirmed before is in the archive's own index")),
        CommandOutcome.FailedToStart f => ChildText.Failed($"{template.Name} did not start: {f.Reason}"),
        CommandOutcome.Refused r => ChildText.Failed($"{template.Name} was refused: {r.Reason}"),
        _ => throw new System.Diagnostics.UnreachableException("CommandOutcome is a closed set"),
    };

    /// <summary>A STREAMED child: its progress counted and dropped, its last line the answer — or why there is none (a broken stream
    /// contract ends the child at once); its launcher recorded at the start, its worker at its first line, both retired at the end.</summary>
    public static async Task<ChildText> StreamedAsync(ActionContext context, ActionCommands commands, CommandTemplate template, IReadOnlyList<string> values, TimeSpan ceiling, CancellationToken cancellationToken)
    {
        var stream = new ArchiveChildStream(context.Config.Int(ConfigKeys.Archive.ProgressLineMaxBytes), template.OutputCapChars);
        var watch = new ChildWatch(context, template.Name, cancellationToken);
        ChildText text;
        try
        {
            var outcome = await commands.StreamAsync(template, values, new StreamRequest(ceiling, line => watch.Line(stream, line)) { OnStarted = watch.Started, OnKilling = watch.Killing }, cancellationToken).ConfigureAwait(false);
            text = Ended(template, outcome, stream.Answer);
        }
        catch (ArchiveChildStreamRefused refused)
        {
            text = ChildText.Failed($"{template.Name} broke its stream's contract and was stopped: {refused.Message}");
        }
        finally
        {
            watch.End();
        }

        return watch.Of(text);
    }

    /// <summary>A streamed child's end: its answer when it exited with one of the codes a child answers with (the S4 own review round
    /// C-1: busy, 75, too — the command line's own exit, read by the same name) after one.</summary>
    private static ChildText Ended(CommandTemplate template, CommandOutcome outcome, string answer) => outcome switch
    {
        CommandOutcome.Exited e when ArchiveExits.Answering.Contains(e.ExitCode) && answer.Length > 0 => ChildText.Of(answer),
        CommandOutcome.Exited e => NoAnswer(template, e),
        _ => NotAnswered(template, outcome),
    };

    /// <summary>Nothing done this time: the side's lock is another run's (C-1) — no failure, and no "ran" with a count of 0 either.</summary>
    public const string BusyWords = "another archive run of this side holds its lock (the user's own, most likely); nothing was done this time";

    /// <summary>A child's outcome in ROOT's words — one sentence per outcome, never the child's own text (the S4 own review round C-3,
    /// S-m3): what root's world-readable run detail and the journal may say.</summary>
    public static string OutcomeWords(ArchiveRunReport report) => report.Outcome switch
    {
        RunOutcomes.Busy => BusyWords,
        RunOutcomes.Unreachable => $"the base did not answer within {ConfigKeys.Archive.ReachabilitySeconds.Name}; nothing was touched",
        RunOutcomes.Refused => "the archive child refused: the base's rules refuse it, the base is not mounted as when it was first used (D7: mount it as before — or, if it really moved and is the same storage, remove the recorded mount in the user's archive state by hand), or another machine's run holds the side's lease; \"wsl-care archive run\", run as the user, says which",
        RunOutcomes.NoBase => $"the user's archive has no {ConfigKeys.Archive.BaseFolder.Name}: there is nothing to archive into",
        RunOutcomes.Stopped => $"the archive child stopped ({report.StopKind})",
        _ => $"the archive child answered {report.Outcome}",
    };

    private static string LastLine(string text) => text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? string.Empty;

    private static string FirstLine(string text) => text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "(nothing on stderr)";

    /// <summary>One child's launcher and worker, recorded in root's state as they appear (risk consult 9/9.4 #1): the launcher at its
    /// start, the worker at a streamed child's first line and again right before a kill (S-M1), the record retired at the end — and
    /// every write that failed kept in words (C-7), never dropped.</summary>
    private sealed class ChildWatch(ActionContext context, string template, CancellationToken cancellationToken)
    {
        private readonly Lock _gate = new();
        private readonly List<string> _notes = [];
        private string _unrecorded = string.Empty;
        private int _launcher;
        private bool _workerLooked;

        public ProcessHooks Hooks => new(Started, Killing);

        public void Started(int pid)
        {
            lock (_gate)
            {
                _launcher = pid;
                Kept(ArchiveChildren.Launched(context, template, pid, cancellationToken), live: true);
            }
        }

        public void Line(ArchiveChildStream stream, string line)
        {
            stream.Take(line);
            lock (_gate)
            {
                if (!_workerLooked)
                {
                    _workerLooked = true;
                    Worker(cancellationToken);
                }
            }
        }

        /// <summary>Right before the tree is killed — it is still whole, so the worker under the launcher is still there to be seen.</summary>
        public void Killing(int pid)
        {
            lock (_gate)
            {
                Worker(CancellationToken.None);
            }
        }

        public void End()
        {
            lock (_gate)
            {
                Kept(ArchiveChildren.Ended(context, CancellationToken.None), live: false);
            }
        }

        public ChildText Of(ChildText text)
        {
            lock (_gate)
            {
                return text with { Notes = [.. _notes], Unrecorded = _unrecorded };
            }
        }

        private void Worker(CancellationToken token)
        {
            if (_launcher > 0)
            {
                Kept(ArchiveChildren.WorkerSeen(context, template, _launcher, token), live: true);
            }
        }

        /// <summary>A write's failure, kept: a note always, and the first one that left a LIVE child out of the record besides.</summary>
        private void Kept(string failure, bool live)
        {
            if (failure.Length == 0)
            {
                return;
            }

            var note = $"{ArchiveChildren.FileName} could not be written for {template}: {failure}";
            _notes.Add(note);
            _unrecorded = live && _unrecorded.Length == 0 ? note : _unrecorded;
        }
    }
}
