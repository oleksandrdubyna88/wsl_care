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
        : ArchiveChildren.Survivor(context, cancellationToken) is { Length: > 0 } survivor ? Skipped(survivor)
        : null;

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

    /// <summary>A whole child's answer: its one line, when it exited with one of <paramref name="exits"/> and its output was not cut.</summary>
    public static async Task<ChildText> ChildTextAsync(ActionCommands commands, CommandTemplate template, IReadOnlyList<int> exits, CancellationToken cancellationToken)
    {
        var outcome = await commands.RunAsync(template, [], cancellationToken).ConfigureAwait(false);
        return outcome is CommandOutcome.Exited exited ? Whole(template, exited, exits) : NotAnswered(template, outcome);
    }

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
        try
        {
            var outcome = await commands.StreamAsync(template, values, new StreamRequest(ceiling, line => watch.Line(stream, line)) { OnStarted = watch.Started }, cancellationToken).ConfigureAwait(false);
            return Ended(template, outcome, stream.Answer);
        }
        catch (ArchiveChildStreamRefused refused)
        {
            return ChildText.Failed($"{template.Name} broke its stream's contract and was stopped: {refused.Message}");
        }
        finally
        {
            _ = ArchiveChildren.Ended(context, CancellationToken.None);
        }
    }

    /// <summary>A streamed child's end: its answer when it exited 0 or 1 (a refused session) after one.</summary>
    private static ChildText Ended(CommandTemplate template, CommandOutcome outcome, string answer) => outcome switch
    {
        CommandOutcome.Exited { ExitCode: 0 or 1 } when answer.Length > 0 => ChildText.Of(answer),
        CommandOutcome.Exited e => NoAnswer(template, e),
        _ => NotAnswered(template, outcome),
    };

    private static string LastLine(string text) => text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? string.Empty;

    private static string FirstLine(string text) => text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "(nothing on stderr)";

    /// <summary>One streamed child's launcher and worker, recorded in root's state as they appear (risk consult 9/9.4 #1).</summary>
    private sealed class ChildWatch(ActionContext context, string template, CancellationToken cancellationToken)
    {
        private int _launcher;
        private bool _workerLooked;

        public void Started(int pid)
        {
            _launcher = pid;
            _ = ArchiveChildren.Launched(context, template, pid, cancellationToken);
        }

        public void Line(ArchiveChildStream stream, string line)
        {
            stream.Take(line);
            if (!_workerLooked && _launcher > 0)
            {
                _workerLooked = true;
                _ = ArchiveChildren.WorkerSeen(context, template, _launcher, cancellationToken);
            }
        }
    }
}
