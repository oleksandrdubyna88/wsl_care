using WslCare.Core.Collectors;

namespace WslCare.Core.Worktrees;

/// <summary>
/// The inverse of <see cref="Health.HealthCollector.InDistro"/> (E19.S3, PLAN_disk_hygiene_windows.md §3.3, E19 stories §3 decision 8): a
/// pointer a worktree made inside WSL wrote (<c>/mnt/d/work/x/.git</c>) read as the Windows path it is (<c>D:\work\x\.git</c>). Only
/// through the automount roots the configuration lists (<c>worktrees.wslAutomountRoots</c>) — the Windows side cannot read the distro's
/// <c>/etc/wsl.conf</c> — and only in the shape the automounter makes: the root, one LOWERCASE ASCII letter, then <c>/</c> or the end;
/// the prefix is matched case-sensitively, and a <c>.</c> or <c>..</c> segment or a control character refuses the whole path (it could
/// climb out of the drive's folder). Anything else is not translated, and the caller reads it as FOREIGN: the safe failure.
/// </summary>
public static class DistroPaths
{
    public static Reading<string> ToWindows(string posixPath, IReadOnlyList<string> automountRoots)
    {
        var root = automountRoots.FirstOrDefault(r => r.EndsWith('/') && posixPath.StartsWith(r, StringComparison.Ordinal));
        return root is null || posixPath.Any(char.IsControl)
            ? NotTranslated(posixPath, "it is under no automount root of worktrees.wslAutomountRoots, or holds a control character")
            : Drive(posixPath, posixPath[root.Length..]);
    }

    private static Reading<string> Drive(string posixPath, string afterRoot) =>
        afterRoot.Length == 0 || !char.IsAsciiLetterLower(afterRoot[0]) || (afterRoot.Length > 1 && afterRoot[1] != '/')
            ? NotTranslated(posixPath, "no single lowercase drive letter follows the automount root")
            : Segments(posixPath, char.ToUpperInvariant(afterRoot[0]), afterRoot.Length > 1 ? afterRoot[2..].Split('/', StringSplitOptions.RemoveEmptyEntries) : []);

    private static Reading<string> Segments(string posixPath, char letter, string[] segments) =>
        segments.Any(s => s is "." or "..")
            ? NotTranslated(posixPath, "it holds a . or .. segment")
            : Reading.Of($"{letter}:\\{string.Join('\\', segments)}");

    private static Reading<string> NotTranslated(string posixPath, string why) =>
        Reading.Missing<string>($"\"{posixPath}\" is not a Windows drive path the automount makes: {why}");
}
