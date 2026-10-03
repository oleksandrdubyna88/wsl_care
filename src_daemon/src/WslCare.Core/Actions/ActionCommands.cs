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

    /// <summary>
    /// Where <paramref name="template"/>'s executable would be started from — on this process's <c>PATH</c> for a
    /// machine-scoped one, in the target user's bin folders for a user-scoped one — or why it would not be: how an action
    /// tells "not installed" (a SKIP, E3.S2) from a failure BEFORE it runs anything. A template it did not declare is
    /// refused here too.
    /// </summary>
    public ResolvedExecutable Locate(CommandTemplate template)
    {
        if (!action.Commands.Any(declared => ReferenceEquals(declared, template)))
        {
            return new ResolvedExecutable.NotFound($"{action.Id} did not declare the template {template.Name}");
        }

        if (template.Scope == CommandScope.Machine)
        {
            return ExecutableResolver.Resolve(template.Executable);
        }

        return targetUser is TargetUserResult.Found
            ? ExecutableResolver.ResolveIn(template.Executable, [.. userBinFolders.Select(f => f.OnDisk)], OperatingSystem.IsWindows())
            : new ResolvedExecutable.NotFound(targetUser.Refusal);
    }

    /// <summary>
    /// This executor as an <see cref="ICommandRunner"/>, for the product's own READ machinery (the Docker collector) to run
    /// through: a request runs only when it is an instance of a MACHINE-scoped template this action declared, and is
    /// recorded like every other command; anything else is refused and recorded. It never streams.
    /// </summary>
    public ICommandRunner AsRunner() => new DeclaredOnly(this);

    private async Task<CommandOutcome> RunDeclaredAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        var template = action.Commands.FirstOrDefault(t =>
            t.Scope == CommandScope.Machine && string.Equals(t.Executable, request.Argv[0], StringComparison.Ordinal) && t.Matches([.. request.Argv.Skip(1)]));
        if (template is null)
        {
            var undeclared = new CommandTemplate("undeclared", CommandScope.Machine, request.Argv[0], [], request.Timeout, request.OutputCapChars);
            return Record(undeclared, request.Display, new CommandOutcome.Refused($"{action.Id} declares no template that {request.Display} is an instance of; an action runs only what it declares"));
        }

        return Record(template, request.Display, await runner.RunAsync(request, cancellationToken).ConfigureAwait(false));
    }

    private sealed class DeclaredOnly(ActionCommands owner) : ICommandRunner
    {
        public Task<CommandOutcome> RunAsync(CommandRequest request, CancellationToken cancellationToken) => owner.RunDeclaredAsync(request, cancellationToken);

        public Task<CommandOutcome> StreamAsync(CommandRequest request, Action<string> onStdoutLine, CancellationToken cancellationToken) =>
            Task.FromResult<CommandOutcome>(new CommandOutcome.Refused("an action does not stream"));
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
