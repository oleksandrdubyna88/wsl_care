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

    /// <summary>Where a self-invocation template's binary is (plan §15r D1): the product's own, checked again for every request; a
    /// test passes its own.</summary>
    public Func<SelfBinaryResult> Self { get; init; } = SelfBinary.Product;

    /// <summary>
    /// A template the action declared STREAMED (plan §15r D8; every other template is refused here): each stdout line goes to
    /// <paramref name="stream"/>'s line callback as it arrives, and the command's ceiling is the one the action chose for this
    /// request — never above the template's own. Recorded as every command is.
    /// </summary>
    public async Task<CommandOutcome> StreamAsync(CommandTemplate template, IReadOnlyList<string> values, StreamRequest stream, CancellationToken cancellationToken)
    {
        var request = template.Streamed ? Request(template, values) : new UserCommand.Refused($"{action.Id} did not declare {template.Name} streamed; an action streams only what it declared streamed");
        var refusal = request is UserCommand.Refused refused ? refused.Reason : CeilingRefusal(template, stream.Ceiling);
        if (refusal.Length > 0)
        {
            return Record(template, $"{template.Shape} ({string.Join(' ', values)})", new CommandOutcome.Refused(refusal));
        }

        var ready = Limited(((UserCommand.Ready)request).Request, stream);
        return Record(template, ready.Display, await runner.StreamAsync(ready, stream.OnLine, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Why the ceiling an action chose cannot be a stream's: not positive, or above the template's own; empty otherwise.</summary>
    private static string CeilingRefusal(CommandTemplate template, TimeSpan ceiling) =>
        ceiling <= TimeSpan.Zero || ceiling > template.Ceiling ? $"{template.Name}: a ceiling of {ceiling} is outside (0, {template.Ceiling}]" : string.Empty;

    /// <summary>The request with the ceiling the action chose and its start callback.</summary>
    private static CommandRequest Limited(CommandRequest request, StreamRequest stream) =>
        new(request.Argv, stream.Ceiling, request.WorkingDirectory)
        {
            OutputCapChars = request.OutputCapChars,
            Environment = request.Environment,
            StdinClosed = request.StdinClosed,
            OnStarted = stream.OnStarted,
            OnKilling = stream.OnKilling,
        };

    public Task<CommandOutcome> RunAsync(CommandTemplate template, IReadOnlyList<string> values, CancellationToken cancellationToken) =>
        RunAsync(template, values, ProcessHooks.None, cancellationToken);

    /// <summary>A whole command whose start and kill <paramref name="hooks"/> hear (the archive's children, S-M1).</summary>
    public async Task<CommandOutcome> RunAsync(CommandTemplate template, IReadOnlyList<string> values, ProcessHooks hooks, CancellationToken cancellationToken)
    {
        var request = Request(template, values);
        if (request is UserCommand.Refused refused)
        {
            return Record(template, $"{template.Shape} ({string.Join(' ', values)})", new CommandOutcome.Refused(refused.Reason));
        }

        var ready = ((UserCommand.Ready)request!).Request with { OnStarted = hooks.OnStarted, OnKilling = hooks.OnKilling };
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

        return template.SelfInvocation ? SelfLocated(Self())
            : targetUser is TargetUserResult.Found ? ExecutableResolver.ResolveIn(template.Executable, [.. userBinFolders.Select(f => f.OnDisk)], OperatingSystem.IsWindows())
            : new ResolvedExecutable.NotFound(targetUser.Refusal);
    }

    /// <summary>A self-invocation's executable is the product's own checked binary, never a lookup (plan §15r D1).</summary>
    private static ResolvedExecutable SelfLocated(SelfBinaryResult self) => self switch
    {
        SelfBinaryResult.Found found => new ResolvedExecutable.Found(found.Path),
        SelfBinaryResult.Refused refused => new ResolvedExecutable.NotFound(refused.Reason),
        _ => throw new System.Diagnostics.UnreachableException("SelfBinaryResult is a closed set"),
    };

    /// <summary>
    /// This executor as an <see cref="ICommandRunner"/>, for the product's own READ machinery (the Docker collector) to run
    /// through: a request runs only when it is an instance of a MACHINE-scoped template this action declared, or of one of
    /// the collectors' shared read-only templates (<see cref="ReadCommandTemplates.All"/> — gate finding #3: an action is not
    /// coupled to the private queries of the collector it borrows), and is recorded like every other command; anything else
    /// is refused and recorded. The runner's policy still judges every argv. It never streams.
    /// </summary>
    public ICommandRunner AsRunner() => new DeclaredOnly(this);

    private async Task<CommandOutcome> RunDeclaredAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        var template = action.Commands.Concat(ReadCommandTemplates.All).FirstOrDefault(t => IsInstance(t, request));
        if (template is null)
        {
            var undeclared = new CommandTemplate("undeclared", CommandScope.Machine, request.Argv[0], [], request.Timeout, request.OutputCapChars);
            return Record(undeclared, request.Display, new CommandOutcome.Refused($"{action.Id} declares no template that {request.Display} is an instance of; an action runs only what it declares"));
        }

        return Record(template, request.Display, await runner.RunAsync(request, cancellationToken).ConfigureAwait(false));
    }

    private static bool IsInstance(CommandTemplate template, CommandRequest request) =>
        template.Scope == CommandScope.Machine && string.Equals(template.Executable, request.Argv[0], StringComparison.Ordinal) && template.Matches([.. request.Argv.Skip(1)]);

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
            ? TargetUserCommands.Build(template, arguments, found.User, userBinFolders, Self)
            : new UserCommand.Refused(targetUser.Refusal);

    private CommandOutcome Record(CommandTemplate template, string display, CommandOutcome outcome)
    {
        // The policy judged the bare program; when the launcher started a file the system drive fallback found, the record
        // names both (plan §17 #1).
        display = outcome.StartedFrom.Length > 0 ? $"{display} (started from {outcome.StartedFrom})" : display;
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

/// <summary>One streamed command (plan §15r D8): the ceiling the action chose for it, where each stdout line goes, and who hears
/// the started process's id (the archive records its children, risk consult 9/9.4 #1).</summary>
public sealed record StreamRequest(TimeSpan Ceiling, Action<string> OnLine)
{
    public Action<int> OnStarted { get; init; } = static _ => { };

    /// <summary>Who hears the child's id right before its tree is killed (<see cref="CommandRequest.OnKilling"/>).</summary>
    public Action<int> OnKilling { get; init; } = static _ => { };
}

/// <summary>Who hears a WHOLE command's start and, when it runs past its ceiling, its kill (plan §15r E9.S4 own review round S-M1:
/// the archive records every child it starts, not only the streamed ones).</summary>
public sealed record ProcessHooks(Action<int> OnStarted, Action<int> OnKilling)
{
    public static ProcessHooks None { get; } = new(static _ => { }, static _ => { });
}
