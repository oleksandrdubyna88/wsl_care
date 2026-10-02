using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;

namespace WslCare.Core.Actions;

/// <summary>
/// The executor an action runs its commands through (plan §5: <c>Run(executor)</c>): it binds ONLY a template the action
/// itself declared (<see cref="ICleanupAction.Commands"/>, compared by identity), wraps a user-scoped one for the target
/// user, hands the request to the <see cref="ICommandRunner"/> — whose real implementation asks the
/// <see cref="CommandPolicy"/> again — and keeps a record of every command for the run detail.
/// </summary>
public sealed class ActionCommands(ICleanupAction action, ICommandRunner runner, TargetUserResult targetUser, IReadOnlyList<UserBinFolder> userBinFolders)
{
    private readonly List<ActionCommandRecord> _ran = [];

    /// <summary>Every command asked for so far, in order — refused ones included.</summary>
    public IReadOnlyList<ActionCommandRecord> Ran => [.. _ran];

    public async Task<CommandOutcome> RunAsync(CommandTemplate template, IReadOnlyList<string> values, CancellationToken cancellationToken)
    {
        var request = Request(template, values);
        if (request is UserCommand.Refused refused)
        {
            return Record(template, $"{template.Shape} ({string.Join(' ', values)})", new CommandOutcome.Refused(refused.Reason));
        }

        var ready = ((UserCommand.Ready)request!).Request;
        return Record(template, ready.Display, await runner.RunAsync(ready, cancellationToken).ConfigureAwait(false));
    }

    private UserCommand Request(CommandTemplate template, IReadOnlyList<string> values)
    {
        if (!action.Commands.Any(declared => ReferenceEquals(declared, template)))
        {
            return new UserCommand.Refused($"{action.Id} did not declare the template {template.Name}; an action runs only what it declares");
        }

        return template.Bind(values) switch
        {
            TemplateBinding.Refused r => new UserCommand.Refused(r.Reason),
            TemplateBinding.Bound b when template.Scope == CommandScope.Machine =>
                new UserCommand.Ready(new CommandRequest([template.Executable, .. b.Arguments], template.Ceiling) { OutputCapChars = template.OutputCapChars }),
            TemplateBinding.Bound b => ForTargetUser(template, b.Arguments),
            _ => throw new System.Diagnostics.UnreachableException("TemplateBinding is a closed set"),
        };
    }

    private UserCommand ForTargetUser(CommandTemplate template, IReadOnlyList<string> arguments) =>
        targetUser is TargetUserResult.Found found
            ? TargetUserCommands.Build(template, arguments, found.User, userBinFolders)
            : new UserCommand.Refused(targetUser.Refusal);

    private CommandOutcome Record(CommandTemplate template, string display, CommandOutcome outcome)
    {
        _ran.Add(outcome switch
        {
            CommandOutcome.Exited e => new ActionCommandRecord(template.Name, display, "exited", e.ExitCode, FirstLine(e.Stderr.Text)),
            CommandOutcome.TimedOut t => new ActionCommandRecord(template.Name, display, "timedOut", null, $"killed after {t.Timeout.TotalSeconds:0} s"),
            CommandOutcome.FailedToStart f => new ActionCommandRecord(template.Name, display, "failedToStart", null, f.Reason),
            CommandOutcome.Refused r => new ActionCommandRecord(template.Name, display, "refused", null, r.Reason),
            _ => throw new System.Diagnostics.UnreachableException("CommandOutcome is a closed set"),
        });
        return outcome;
    }

    private static string FirstLine(string text) => text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
}
