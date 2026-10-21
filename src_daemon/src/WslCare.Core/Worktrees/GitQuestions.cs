using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;

namespace WslCare.Core.Worktrees;

/// <summary>
/// git's three read-only questions about the candidates of one folder (PLAN_disk_hygiene_windows_E19.md E19.S4): which working tree is
/// this (<c>rev-parse --show-toplevel</c>), does it track a file inside a candidate (<c>ls-files</c>), and does an ignore rule cover the
/// candidate (<c>check-ignore</c>). A candidate is <see cref="GitVerdict.Confirmed"/> only when all three answers say so; every other
/// outcome is <see cref="GitVerdict.Retained"/> with the reason — a question git could not answer is never an answer.
/// </summary>
/// <remarks>
/// <para>Safety, in the order a call meets it: the folder must be at or beneath the configured root (this code checks it — the command
/// policy never judges a working directory); the process runs in that folder with NO path on its argv but the relative names; its environment
/// is <see cref="GitEnvironment"/> with <c>GIT_CEILING_DIRECTORIES</c> at the root, so it cannot answer for a repository above it; and
/// <c>rev-parse</c> must name the expected top level, or every candidate of the folder is retained as another repository's.</para>
/// <para>Each name is judged by ITS OWN line of <c>check-ignore -v -n</c>, never by the exit code: exit 0 means "at least one is ignored",
/// 1 "none", 128 a refusal, and a batch is a mixture. A negated rule and a path no rule covers print the same <c>::</c> line, and both
/// retain. An exit code outside git's documented ones, a cut output, a timeout and a refusal each retain the whole call by name.</para>
/// </remarks>
/// <param name="runner">Starts the process (the product's carries the policy and <see cref="GitExecutable"/>).</param>
/// <param name="limits">The bounds from the configuration in force.</param>
/// <param name="parentEnvironment">This process's variables by name (<see cref="GitEnvironment.ThisProcess"/>); only four names are copied.</param>
/// <param name="directoryExists">Whether a folder exists — the orphan's repository.</param>
public sealed class GitQuestions(ICommandRunner runner, GitLimits limits, Func<string, string> parentEnvironment, Func<string, bool> directoryExists)
{
    private static readonly int[] OkExits = [0];
    private static readonly int[] IgnoreExits = [0, 1];

    /// <summary>The same, for a caller that is not asynchronous (the scan is a synchronous walk). The runner is never given a synchronization
    /// context to come back to, so this cannot deadlock; the question is bounded by <c>worktrees.gitTimeoutSeconds</c> per call.</summary>
    public IReadOnlyList<GitAnswer> Ask(GitQuestion question, CancellationToken cancellationToken) => AskAsync(question, cancellationToken).GetAwaiter().GetResult();

    /// <summary>One answer per name of <paramref name="question"/>, in its order.</summary>
    public async Task<IReadOnlyList<GitAnswer>> AskAsync(GitQuestion question, CancellationToken cancellationToken)
    {
        var names = question.Names.Select(n => (Original: n, Asked: GitReplies.Slashed(n))).ToList();
        var askable = names.Select(n => n.Asked).Where(new SlotKind.RelativeName().Accepts).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var verdicts = await VerdictsAsync(question, askable, cancellationToken).ConfigureAwait(false);
        return [.. names.Select(n => new GitAnswer(n.Original, VerdictOf(verdicts, n.Asked)))];
    }

    private static GitVerdict VerdictOf(IReadOnlyDictionary<string, GitVerdict> verdicts, string asked) =>
        verdicts.TryGetValue(asked, out var verdict) ? verdict : new GitVerdict.Retained(WhyNone(asked));

    /// <summary>Why a name has no verdict: it was asked and git said nothing about it, it is a word the never-list refuses as a git argument (which
    /// says so by name — an accepted blind spot is only that a path merely CONTAINING the word is not a token), or it is no name git may be asked about.</summary>
    private static string WhyNone(string asked) =>
        new SlotKind.RelativeName().Accepts(asked) ? GitReasons.NoAnswer : SlotKind.RelativeName.IsGitWord(asked) ? GitReasons.UnsafeNameOf(asked) : GitReasons.BadName;

    private async Task<IReadOnlyDictionary<string, GitVerdict>> VerdictsAsync(GitQuestion question, IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        if (!FolderBeneath.AtOrUnder(question.Root, question.Folder))
        {
            return Retain(names, GitReasons.OutsideRoot);
        }

        var (calls, tooLong) = GitBatches.Split(names, limits.NamesPerCall, limits.CommandLineMaxChars, group => LongestCommandLine(question, group));
        var asked = await AskCallsAsync(question, calls, cancellationToken).ConfigureAwait(false);
        return Merge(asked, Retain(tooLong, GitReasons.TooLong));
    }

    private Task<IReadOnlyDictionary<string, GitVerdict>> AskCallsAsync(GitQuestion question, IReadOnlyList<IReadOnlyList<string>> calls, CancellationToken cancellationToken) => question.Kind switch
    {
        GitFolder.Repository repository => RepositoryAsync(question, repository, calls, cancellationToken),
        GitFolder.Orphan orphan => OrphanAsync(question, orphan, calls, cancellationToken),
        _ => throw new System.Diagnostics.UnreachableException("GitFolder is a closed set"),
    };

    private async Task<IReadOnlyDictionary<string, GitVerdict>> RepositoryAsync(GitQuestion question, GitFolder.Repository repository, IReadOnlyList<IReadOnlyList<string>> calls, CancellationToken cancellationToken)
    {
        var everyName = calls.SelectMany(c => c).ToList();
        if (everyName.Count == 0)
        {
            return Retain([], string.Empty);
        }

        var problem = TopLevelProblem(await RunAsync(question, GitCommands.TopLevel(), cancellationToken).ConfigureAwait(false), repository.ExpectedTopLevel);
        if (problem.Length > 0)
        {
            return Retain(everyName, problem);
        }

        var verdicts = new List<IReadOnlyDictionary<string, GitVerdict>>();
        foreach (var call in calls)
        {
            verdicts.Add(await CallAsync(question, call, cancellationToken).ConfigureAwait(false));
        }

        return Merge(verdicts.ToArray());
    }

    private async Task<IReadOnlyDictionary<string, GitVerdict>> CallAsync(GitQuestion question, IReadOnlyList<string> call, CancellationToken cancellationToken)
    {
        var tracked = (await RunAsync(question, GitCommands.TrackedFiles(call), cancellationToken).ConfigureAwait(false)).Expect(OkExits);
        var ignore = (await RunAsync(question, GitCommands.CheckIgnore(call), cancellationToken).ConfigureAwait(false)).Expect(IgnoreExits);
        return (tracked, ignore) switch
        {
            (GitReply.Failed failed, _) => Retain(call, failed.Reason),
            (_, GitReply.Failed failed) => Retain(call, failed.Reason),
            (GitReply.Done t, GitReply.Done i) => Judge(call, GitReplies.TrackedPaths(t.Stdout), GitReplies.IgnoreLines(i.Stdout)),
            _ => throw new System.Diagnostics.UnreachableException("GitReply is a closed set"),
        };
    }

    private static Dictionary<string, GitVerdict> Judge(IReadOnlyList<string> call, IReadOnlyList<string> tracked, IReadOnlyDictionary<string, IgnoreLine> lines) =>
        call.ToDictionary(name => name, name => RepositoryVerdict(name, tracked, lines), StringComparer.OrdinalIgnoreCase);

    private static GitVerdict RepositoryVerdict(string name, IReadOnlyList<string> tracked, IReadOnlyDictionary<string, IgnoreLine> lines) => (GitReplies.HoldsTracked(tracked, name), lines.TryGetValue(name, out var line) ? line : IgnoreLine.Unreadable) switch
    {
        (true, _) => new GitVerdict.Retained(GitReasons.Tracked),
        (_, IgnoreLine.Ignored) => new GitVerdict.Confirmed(TrackedFilesChecked: true),
        (_, IgnoreLine.NotIgnored) => new GitVerdict.Retained(GitReasons.NotIgnored),
        _ => new GitVerdict.Retained(GitReasons.NoAnswer),
    };

    private async Task<IReadOnlyDictionary<string, GitVerdict>> OrphanAsync(GitQuestion question, GitFolder.Orphan orphan, IReadOnlyList<IReadOnlyList<string>> calls, CancellationToken cancellationToken)
    {
        if (!directoryExists(orphan.AdminFolder))
        {
            return Retain([.. calls.SelectMany(c => c)], GitReasons.OrphanNotConfirmed);
        }

        var verdicts = new List<IReadOnlyDictionary<string, GitVerdict>>();
        foreach (var call in calls)
        {
            var reply = (await RunAsync(question, GitCommands.CheckIgnoreOrphan(orphan.AdminFolder, call), cancellationToken).ConfigureAwait(false)).Expect(IgnoreExits);
            verdicts.Add(OrphanVerdicts(call, reply));
        }

        return Merge(verdicts.ToArray());
    }

    private static Dictionary<string, GitVerdict> OrphanVerdicts(IReadOnlyList<string> call, GitReply reply) => reply switch
    {
        GitReply.Done done => OrphanJudge(call, GitReplies.IgnoreLines(done.Stdout)),
        GitReply.Failed failed => Retain(call, failed.Reason),
        _ => throw new System.Diagnostics.UnreachableException("GitReply is a closed set"),
    };

    private static Dictionary<string, GitVerdict> OrphanJudge(IReadOnlyList<string> call, IReadOnlyDictionary<string, IgnoreLine> lines) =>
        call.ToDictionary(name => name, name => OrphanVerdict(lines.TryGetValue(name, out var line) ? line : IgnoreLine.Unreadable), StringComparer.OrdinalIgnoreCase);

    private static GitVerdict OrphanVerdict(IgnoreLine line) => line switch
    {
        IgnoreLine.Ignored => new GitVerdict.Confirmed(TrackedFilesChecked: false),
        IgnoreLine.NotIgnored => new GitVerdict.Retained(GitReasons.NotIgnored),
        _ => new GitVerdict.Retained(GitReasons.NoAnswer),
    };

    /// <summary>Why <paramref name="reply"/> does not show the expected working tree; empty when it does.</summary>
    private static string TopLevelProblem(GitReply reply, string expected) => reply.Expect(OkExits) switch
    {
        GitReply.Failed failed => failed.Reason,
        GitReply.Done done => SameFolder(done.Stdout, expected) ? string.Empty : GitReasons.AnotherRepository,
        _ => throw new System.Diagnostics.UnreachableException("GitReply is a closed set"),
    };

    private static bool SameFolder(string printed, string expected) =>
        string.Equals(Canonical(printed.Split('\n')[0]), Canonical(expected), StringComparison.OrdinalIgnoreCase);

    private static string Canonical(string path) => GitReplies.Slashed(path).Trim().TrimEnd('/');

    private async Task<GitReply> RunAsync(GitQuestion question, IReadOnlyList<string> argv, CancellationToken cancellationToken)
    {
        var request = new CommandRequest(argv, limits.Timeout, question.Folder)
        {
            Environment = GitEnvironment.For(parentEnvironment, question.Root),
            StdinClosed = true,
            Utf8Output = true,
        };
        return GitReply.Of(await runner.RunAsync(request, cancellationToken).ConfigureAwait(false), limits.Timeout);
    }

    /// <summary>The longest command line a call with exactly these names would have, over every command it would run.</summary>
    private static int LongestCommandLine(GitQuestion question, IReadOnlyList<string> names) => question.Kind switch
    {
        GitFolder.Orphan orphan => GitBatches.EncodedLength(GitCommands.CheckIgnoreOrphan(orphan.AdminFolder, names)),
        _ => Math.Max(GitBatches.EncodedLength(GitCommands.TrackedFiles(names)), GitBatches.EncodedLength(GitCommands.CheckIgnore(names))),
    };

    private static Dictionary<string, GitVerdict> Retain(IReadOnlyList<string> names, string reason) =>
        names.ToDictionary(name => name, _ => (GitVerdict)new GitVerdict.Retained(reason), StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, GitVerdict> Merge(params IReadOnlyDictionary<string, GitVerdict>[] parts) =>
        parts.SelectMany(p => p).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
}
