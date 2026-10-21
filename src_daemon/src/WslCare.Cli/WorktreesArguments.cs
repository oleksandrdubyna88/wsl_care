namespace WslCare.Cli;

/// <summary>
/// The arguments of the <c>worktrees</c> verbs (E19.S3): <c>--json</c>, once, and nothing else. The folders of <c>worktrees roots set</c>
/// arrive on STDIN — never as words on the command line (a Windows command line holds 32 767 characters, a path with a space
/// needs quoting, and a path in a process listing is a path leaked) — so a word that is not <c>--json</c> is refused saying so.
/// </summary>
internal static class WorktreesArguments
{
    internal static Request Parse(string verb, IReadOnlyList<string> rest, Func<bool, Request> make, string hint) => rest switch
    {
        [] => make(false),
        [CommandLine.JsonFlag] => make(true),
        _ => new Request.Failed($"\"{CommandLine.BinaryName} {verb}\" takes only {CommandLine.JsonFlag}, once{hint}; got \"{WslCare.Core.Processes.Redaction.ShownText.Printable(string.Join(' ', rest))}\"."),
    };

    internal const string FoldersOnStdin = "; the folders come on stdin, one absolute path per line";
}
