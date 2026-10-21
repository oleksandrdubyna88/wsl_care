using System.Globalization;

using WslCare.Core.Config;

namespace WslCare.Core.Worktrees;

/// <summary>What kind of folder git is asked about — a closed set, so the question is complete by inspection.</summary>
public abstract record GitFolder
{
    private GitFolder()
    {
    }

    /// <summary>A working tree or a submodule: git is asked for its top level (which must be <paramref name="ExpectedTopLevel"/>), the
    /// tracked files under each name, and the rule that decides each name.</summary>
    public sealed record Repository(string ExpectedTopLevel) : GitFolder;

    /// <summary>An orphan — a folder whose <c>.git</c> file points at an admin folder that is gone: git is asked, from the orphan's own
    /// folder and with the repository <paramref name="AdminFolder"/> names, which rule decides each name. The index is NOT read, so the
    /// tracked files stay unknown. <paramref name="AdminFolder"/> is the path the classifier read from the <c>.git</c> file under a
    /// configured root — never a path from anywhere else; the command's slot re-checks its SHAPE, and this type's caller its source.</summary>
    public sealed record Orphan(string AdminFolder) : GitFolder;
}

/// <summary>
/// One folder and the names under it the scan wants git's word on.
/// </summary>
/// <param name="Folder">The working directory git runs in — the candidates' parent folder. It must be at or beneath <paramref name="Root"/>;
/// this code checks that, the command policy never judges a working directory.</param>
/// <param name="Names">The candidates, relative to <paramref name="Folder"/> (<c>bin</c>, <c>obj/</c>, <c>node_modules</c>; <c>/</c> or <c>\</c> separated).</param>
/// <param name="Root">The configured worktree root the folder is under; it is also git's discovery ceiling, so git can never answer for a
/// repository above it.</param>
/// <param name="Kind">A working tree (or submodule) with the top level it must be, or an orphan with its admin folder.</param>
public sealed record GitQuestion(string Folder, IReadOnlyList<string> Names, string Root, GitFolder Kind);

/// <summary>What git said about one name — a closed set.</summary>
public abstract record GitVerdict
{
    private GitVerdict()
    {
    }

    /// <summary>Git confirms the name is ignored. <paramref name="TrackedFilesChecked"/> is false for an orphan: its index was not read, so
    /// "ignore rules confirmed; tracked files NOT checked" is all that is known.</summary>
    public sealed record Confirmed(bool TrackedFilesChecked) : GitVerdict
    {
        public string Note => TrackedFilesChecked ? "ignored by git; no tracked file inside" : GitReasons.OrphanIgnoreOnly;
    }

    /// <summary>Git did not confirm it, or could not be asked; <paramref name="Reason"/> says which, in a sentence the report can count by.</summary>
    public sealed record Retained(string Reason) : GitVerdict;
}

/// <summary>One name with git's verdict.</summary>
public sealed record GitAnswer(string Name, GitVerdict Verdict);

/// <summary>The words a retained candidate is reported under — one place, so the report groups by them and the tests read them.</summary>
public static class GitReasons
{
    public const string AnotherRepository = "git answered for another repository";
    public const string Tracked = "holds tracked files";
    public const string NotIgnored = "not ignored by git";
    public const string TooLong = "too long for one git call";
    public const string NoAnswer = "git gave no answer for it";
    public const string BadName = "not a name git may be asked about";
    public const string OutsideRoot = "outside the configured root, so git was not asked";
    public const string OrphanNotConfirmed = "not confirmed by git";
    public const string OrphanIgnoreOnly = "ignore rules confirmed; tracked files NOT checked";
    public const string Cut = "git's answer was cut at the output cap";

    public static string Refused(string firstLine) => $"git refused: {firstLine}";

    public static string TimedOut(TimeSpan timeout) => string.Create(CultureInfo.InvariantCulture, $"git did not answer within {timeout.TotalSeconds:0.###} s");

    public static string NotRun(string why) => $"git could not be run: {why}";

    /// <summary>The first line of git's complaint, control characters replaced and cut — a reason is one line.</summary>
    /// <summary>The closed sentence a reason is COUNTED under in the report: git's own words after a refusal vary, the group does not.</summary>
    public static string GroupOf(string reason) => reason switch
    {
        _ when reason.StartsWith("git refused:", StringComparison.Ordinal) => "git refused the question",
        _ when reason.StartsWith("git did not answer", StringComparison.Ordinal) => "git did not answer in time",
        _ when reason.StartsWith("git could not be run:", StringComparison.Ordinal) => "git could not be run",
        _ => reason,
    };

    /// <summary>Whether a retained reason says git could not be ASKED (as against git answering no): the verdict then cannot be trusted as "none".</summary>
    public static bool IsAFailureToAsk(string reason) => GroupOf(reason) is "git refused the question" or "git did not answer in time" or "git could not be run" or Cut or NoAnswer;

    public static string FirstLine(string stderr, int maxChars)
    {
        var line = stderr.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        var clean = new string([.. line.Select(c => char.IsControl(c) ? '?' : c)]);
        return clean.Length > maxChars ? clean[..maxChars] : clean;
    }

    /// <summary>The widest a reason may be: the report's own cap (<c>records.maxReasonChars</c>).</summary>
    public static int MaxChars => Tuning.Current.Int(ConfigKeys.Records.MaxReasonChars);
}
