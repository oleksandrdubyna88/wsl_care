using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;

namespace WslCare.TestSupport;

/// <summary>One scripted stream (<see cref="ICommandRunner.StreamAsync"/>): the lines the child prints, how it
/// ends, and what happens once the lines are out — a test cancels its token there to stop a follower.</summary>
public sealed record StreamScript(IReadOnlyList<string> Lines, CommandOutcome Outcome)
{
    /// <summary>Runs after the last line was delivered, before the outcome is returned.</summary>
    public Action Then { get; init; } = static () => { };
}

/// <summary>
/// The <see cref="ICommandRunner"/> tests hand to actions and collectors: it starts nothing, keeps
/// every request it was given, and answers the outcome scripted for that argv (or a scripted default).
/// </summary>
/// <remarks>Three kinds of script, asked in this order: an EXACT argv; a PREDICATE over the argv with a
/// sequence of outcomes (one per call, the last one repeating) — for an argv that carries an instant the test
/// does not want to recompute; and, for <see cref="StreamAsync"/>, a queue of <see cref="StreamScript"/>s. An
/// exhausted stream queue blocks until the caller's token is cancelled — the shape of a live stream nobody
/// interrupts.</remarks>
public sealed class RecordingCommandRunner : ICommandRunner
{
    private readonly List<CommandRequest> _requests = [];
    private readonly Dictionary<string, CommandOutcome> _scripted = new(StringComparer.Ordinal);
    private readonly List<(Func<IReadOnlyList<string>, bool> Match, Queue<CommandOutcome> Outcomes)> _matching = [];
    private readonly Queue<StreamScript> _streams = new();
    private readonly List<(Func<IReadOnlyList<string>, bool> Match, Func<CommandRequest, CommandOutcome> Effect)> _effects = [];
    private readonly object _gate = new();

    /// <summary>What an unscripted command answers: exit 0, nothing printed.</summary>
    public CommandOutcome Default { get; init; } = new CommandOutcome.Exited(0, CapturedText.Empty, CapturedText.Empty, TimeSpan.Zero);

    public IReadOnlyList<CommandRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Every argv, each as one display string, in the order it was asked for.</summary>
    public IReadOnlyList<string> Commands => [.. Requests.Select(r => r.Display)];

    /// <summary>Script what <paramref name="argv"/> answers; matched on the whole argv, exactly.</summary>
    public RecordingCommandRunner Script(IReadOnlyList<string> argv, CommandOutcome outcome)
    {
        _scripted[string.Join('\u0001', argv)] = outcome;
        return this;
    }

    public RecordingCommandRunner Script(IReadOnlyList<string> argv, int exitCode, string stdout = "", string stderr = "") =>
        Script(argv, Exited(exitCode, stdout, stderr));

    /// <summary>Script every argv <paramref name="match"/> accepts: the outcomes in order, one per call, the last repeating.</summary>
    public RecordingCommandRunner Script(Func<IReadOnlyList<string>, bool> match, params CommandOutcome[] outcomes)
    {
        _matching.Add((match, new Queue<CommandOutcome>(outcomes)));
        return this;
    }

    /// <summary>Script every argv <paramref name="match"/> accepts with an answer COMPUTED at the call — what a test needs when
    /// the command has an effect it must play (a vacuum that removes files) before the outcome returns.</summary>
    public RecordingCommandRunner ScriptEffect(Func<IReadOnlyList<string>, bool> match, Func<CommandRequest, CommandOutcome> effect)
    {
        _effects.Add((match, effect));
        return this;
    }

    /// <summary>Queue the next stream a <see cref="StreamAsync"/> call plays.</summary>
    public RecordingCommandRunner Stream(StreamScript script)
    {
        _streams.Enqueue(script);
        return this;
    }

    /// <summary>When set, every request is reviewed first exactly as <c>ProcessCommandRunner</c> reviews it: a refused one is
    /// kept in <see cref="Requests"/> and answered <see cref="CommandOutcome.Refused"/>, and nothing scripted is played.</summary>
    public CommandPolicy? Policy { get; init; }

    /// <summary>The pid a started stream announces (<see cref="CommandRequest.OnStarted"/>) — nothing is started, a test names it.</summary>
    public int StartedPid { get; init; } = 4242;

    public static CommandOutcome.Exited Exited(int exitCode, string stdout = "", string stderr = "") =>
        new(exitCode, new CapturedText(stdout, false), new CapturedText(stderr, false), TimeSpan.Zero);

    public Task<CommandOutcome> RunAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Func<CommandRequest, CommandOutcome>? effect;
        lock (_gate)
        {
            _requests.Add(request);
            if (Policy?.Review(request) is CommandVerdict.Refused refused)
            {
                return Task.FromResult<CommandOutcome>(new CommandOutcome.Refused(refused.Reason));
            }

            effect = _effects.FirstOrDefault(e => e.Match(request.Argv)).Effect;
        }

        return Task.FromResult(Killed(request, effect is null ? Answered(request) : Played(request, effect)));
    }

    /// <summary>An effect plays a child that started: the start is told first, as the real runner tells it right after the start.</summary>
    private CommandOutcome Played(CommandRequest request, Func<CommandRequest, CommandOutcome> effect)
    {
        request.OnStarted(StartedPid);
        return effect(request);
    }

    /// <summary>A scripted answer: the start is told unless the outcome says nothing was started.</summary>
    private CommandOutcome Answered(CommandRequest request)
    {
        CommandOutcome outcome;
        lock (_gate)
        {
            outcome = Answer(request.Argv);
        }

        if (outcome is not (CommandOutcome.FailedToStart or CommandOutcome.Refused))
        {
            request.OnStarted(StartedPid);
        }

        return outcome;
    }

    /// <summary>A child that ran past its ceiling: the kill is told first, as the real runner tells it while the tree is whole.</summary>
    private CommandOutcome Killed(CommandRequest request, CommandOutcome outcome)
    {
        if (outcome is CommandOutcome.TimedOut)
        {
            request.OnKilling(StartedPid);
        }

        return outcome;
    }

    public async Task<CommandOutcome> StreamAsync(CommandRequest request, Action<string> onStdoutLine, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StreamScript? script;
        lock (_gate)
        {
            _requests.Add(request);
            if (Policy?.Review(request) is CommandVerdict.Refused refused)
            {
                return new CommandOutcome.Refused(refused.Reason);
            }

            script = _streams.Count > 0 ? _streams.Dequeue() : null;
        }

        request.OnStarted(StartedPid);

        if (script is null)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally
            {
                request.OnKilling(StartedPid);
            }

            throw new OperationCanceledException(cancellationToken);
        }

        foreach (var line in script.Lines)
        {
            onStdoutLine(line);
        }

        script.Then();
        cancellationToken.ThrowIfCancellationRequested();
        return Killed(request, script.Outcome);
    }

    private CommandOutcome Answer(IReadOnlyList<string> argv)
    {
        if (_scripted.TryGetValue(string.Join('\u0001', argv), out var exact))
        {
            return exact;
        }

        foreach (var (match, outcomes) in _matching)
        {
            if (match(argv))
            {
                return outcomes.Count > 1 ? outcomes.Dequeue() : outcomes.Peek();
            }
        }

        return Default;
    }
}
