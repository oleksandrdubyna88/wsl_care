using WslCare.Core.Processes;

namespace WslCare.Core.Worktrees;

/// <summary>What one git call came to — the process ended with an exit code and output, or it did not give an answer at all.</summary>
public abstract record GitReply
{
    private GitReply()
    {
    }

    /// <summary>The process ran to its end.</summary>
    public sealed record Done(int ExitCode, string Stdout, string Stderr, bool Cut) : GitReply;

    /// <summary>No answer: the call timed out, could not start, or was refused; <paramref name="Reason"/> is the sentence the report quotes.</summary>
    public sealed record Failed(string Reason) : GitReply;

    /// <summary>The reply for <paramref name="outcome"/> of a call that had <paramref name="timeout"/> to answer.</summary>
    public static GitReply Of(CommandOutcome outcome, TimeSpan timeout) => outcome switch
    {
        CommandOutcome.Exited e => new Done(e.ExitCode, e.Stdout.Text, e.Stderr.Text, e.Stdout.Truncated),
        CommandOutcome.TimedOut => new Failed(GitReasons.TimedOut(timeout)),
        CommandOutcome.FailedToStart f => new Failed(GitReasons.NotRun(f.Reason)),
        CommandOutcome.Refused r => new Failed(GitReasons.NotRun(r.Reason)),
        _ => throw new System.Diagnostics.UnreachableException("CommandOutcome is a closed set"),
    };

    /// <summary>This reply when it ended with one of <paramref name="exitCodes"/> and its output was not cut; otherwise a failure
    /// naming git's own first line of complaint. A non-zero exit that git did not explain is still a refusal — an exit code is read
    /// as an answer only where git documents it as one.</summary>
    public GitReply Expect(params int[] exitCodes) => this switch
    {
        Done { Cut: true } => new Failed(GitReasons.Cut),
        Done d when exitCodes.Contains(d.ExitCode) => d,
        Done d => new Failed(GitReasons.Refused(Complaint(d))),
        _ => this,
    };

    private static string Complaint(Done done) =>
        GitReasons.FirstLine(done.Stderr, GitReasons.MaxChars) is { Length: > 0 } line ? line : $"exit code {done.ExitCode}";
}

/// <summary>How <c>check-ignore -v -n</c> decided one name.</summary>
public enum IgnoreLine
{
    /// <summary>A rule ignores it.</summary>
    Ignored,

    /// <summary>No rule does, or the rule that decides is a negation (<c>!pattern</c>): both read "not ignored by git".</summary>
    NotIgnored,

    /// <summary>The line is not the shape git documents, so it answers nothing.</summary>
    Unreadable,
}

/// <summary>The readers of git's two answers — pure over text, so every shape git printed (measured 2026-10-15, git 2.55.0.windows.3) is a test.</summary>
public static class GitReplies
{
    /// <summary>The tracked paths <c>ls-files -z</c> printed (NUL-terminated), <c>/</c>-separated.</summary>
    public static IReadOnlyList<string> TrackedPaths(string stdout) =>
        [.. stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(Slashed)];

    /// <summary>Whether any of <paramref name="tracked"/> is <paramref name="name"/> or inside it.</summary>
    public static bool HoldsTracked(IReadOnlyList<string> tracked, string name)
    {
        var folder = Slashed(name).TrimEnd('/');
        return tracked.Any(path => string.Equals(path, folder, StringComparison.OrdinalIgnoreCase) || path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Each line <c>&lt;source&gt;:&lt;line&gt;:&lt;pattern&gt;\t&lt;path&gt;</c> (or <c>::\t&lt;path&gt;</c> for a path no rule ignores), keyed by path. The line
    /// is split at its FIRST tab; a path named twice is ignored only if every line says so.</summary>
    public static IReadOnlyDictionary<string, IgnoreLine> IgnoreLines(string stdout)
    {
        var lines = new Dictionary<string, IgnoreLine>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var tab = line.IndexOf('\t');
            if (tab >= 0)
            {
                lines[Slashed(line[(tab + 1)..])] = Worst(lines, Slashed(line[(tab + 1)..]), Classify(line[..tab]));
            }
        }

        return lines;
    }

    private static IgnoreLine Worst(Dictionary<string, IgnoreLine> lines, string path, IgnoreLine now) =>
        lines.TryGetValue(path, out var before) && before != IgnoreLine.Ignored ? before : now;

    private static IgnoreLine Classify(string left)
    {
        if (left == "::")
        {
            return IgnoreLine.NotIgnored;
        }

        var pattern = PatternStart(left);
        return pattern < 0 ? IgnoreLine.Unreadable : left[pattern] == '!' ? IgnoreLine.NotIgnored : IgnoreLine.Ignored;
    }

    /// <summary>Where the pattern starts in <c>source:line:pattern</c>: after the first <c>:digits:</c> — the source may hold a drive
    /// colon, the pattern any colon; -1 when there is none or the pattern is empty.</summary>
    private static int PatternStart(string left)
    {
        for (var colon = left.IndexOf(':'); colon >= 0; colon = left.IndexOf(':', colon + 1))
        {
            var end = left.IndexOf(':', colon + 1);
            if (end > colon + 1 && left[(colon + 1)..end].All(char.IsAsciiDigit))
            {
                return end + 1 < left.Length ? end + 1 : -1;
            }
        }

        return -1;
    }

    /// <summary>A path with <c>/</c> separators.</summary>
    public static string Slashed(string path) => path.Replace('\\', '/');
}
