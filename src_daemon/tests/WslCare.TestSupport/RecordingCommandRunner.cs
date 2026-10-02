using WslCare.Core.Processes;

namespace WslCare.TestSupport;

/// <summary>
/// The <see cref="ICommandRunner"/> tests hand to actions and collectors: it starts nothing, keeps
/// every request it was given, and answers the outcome scripted for that argv (or a scripted default).
/// </summary>
public sealed class RecordingCommandRunner : ICommandRunner
{
    private readonly List<CommandRequest> _requests = [];
    private readonly Dictionary<string, CommandOutcome> _scripted = new(StringComparer.Ordinal);

    /// <summary>What an unscripted command answers: exit 0, nothing printed.</summary>
    public CommandOutcome Default { get; init; } = new CommandOutcome.Exited(0, CapturedText.Empty, CapturedText.Empty, TimeSpan.Zero);

    public IReadOnlyList<CommandRequest> Requests => _requests;

    /// <summary>Every argv, each as one display string, in the order it was asked for.</summary>
    public IReadOnlyList<string> Commands => [.. _requests.Select(r => r.Display)];

    /// <summary>Script what <paramref name="argv"/> answers; matched on the whole argv, exactly.</summary>
    public RecordingCommandRunner Script(IReadOnlyList<string> argv, CommandOutcome outcome)
    {
        _scripted[string.Join('\u0001', argv)] = outcome;
        return this;
    }

    public RecordingCommandRunner Script(IReadOnlyList<string> argv, int exitCode, string stdout = "", string stderr = "") =>
        Script(argv, new CommandOutcome.Exited(exitCode, new CapturedText(stdout, false), new CapturedText(stderr, false), TimeSpan.Zero));

    public Task<CommandOutcome> RunAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _requests.Add(request);
        return Task.FromResult(_scripted.GetValueOrDefault(string.Join('\u0001', request.Argv), Default));
    }
}

/// <summary>A policy that refuses everything, for proving a refusal prevents the start.</summary>
public sealed class RefuseAllCommandPolicy(string reason = "refused by the test policy") : ICommandPolicy
{
    public CommandVerdict Review(IReadOnlyList<string> argv) => CommandVerdict.Refuse(reason);
}
