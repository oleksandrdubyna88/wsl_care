using WslCare.Core.Hosting;

namespace WslCare.Core.Actions.WindowsTemp;

/// <summary>What resolving the cleanup's root produced — a closed set.</summary>
public abstract record TempRootResult
{
    private TempRootResult()
    {
    }

    public sealed record Resolved(string Root) : TempRootResult;

    public sealed record Refused(string Reason) : TempRootResult;
}

/// <summary>
/// The root of the cleanup (PLAN_windows_runner.md §3.12; the story's plan round, finding 2). There is no shell known folder for
/// TEMP, so the root is the shell folder LocalApplicationData plus <c>\Temp</c>, cross-checked against the process' temp path
/// (<c>GetTempPath</c>): both must name the same folder, and it must lie strictly inside the user's profile — otherwise the run
/// REFUSES with the reason and deletes nothing. A <c>TEMP</c> / <c>TMP</c> that names ANOTHER folder, or a short 8.3 spelling of the
/// profile, differs in spelling and is a refusal (fail closed). A junction AT <c>%LOCALAPPDATA%\Temp</c> that leads to another drive is
/// NOT a refusal — both spellings agree, and the deletion policy and the delete walk judge the REAL path (the walk compares every
/// path with the real root plus its relative path), so nothing outside the real temp folder can be reached.
/// </summary>
public static class TempRoots
{
    public static TempRootResult Resolve(IHostPaths paths) =>
        paths is WindowsHostPaths windows ? Check(paths.Rules, windows.Folders) : new TempRootResult.Refused("the Windows %TEMP% cleanup runs on the Windows host only");

    private static TempRootResult Check(PathRules rules, WindowsEnvironment folders)
    {
        var shell = rules.Join(folders.LocalAppData, "Temp");
        if (!rules.PathEquals(shell, folders.Temp))
        {
            return new TempRootResult.Refused($"the shell folder says the temp folder is {shell} but this process' temp path is {folders.Temp}: they name different folders, so nothing is deleted");
        }

        return rules.IsStrictlyUnder(shell, folders.UserProfile) && !rules.IsRoot(shell)
            ? new TempRootResult.Resolved(rules.Normalize(shell))
            : new TempRootResult.Refused($"{shell} is not inside the user's profile folder, so nothing is deleted");
    }
}
