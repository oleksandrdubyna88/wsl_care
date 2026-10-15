using WslCare.Core.Collectors;
using WslCare.Core.Hosting;

namespace WslCare.Core.Worktrees;

/// <summary>
/// What git's two pointer files say, read as TEXT (E19.S3 — never by starting git): a worktree's <c>.git</c> FILE holds
/// <c>gitdir: &lt;admin folder&gt;</c>, and the admin folder's own <c>gitdir</c> file holds the bare path of that <c>.git</c> file. A pointer
/// is one line; anything else is not one. <see cref="Resolve"/> turns the path into a Windows path where it can — a drive path as it
/// is, a relative one from the folder it lies in, a <c>/mnt/&lt;letter&gt;/…</c> one through the automount roots — and says FOREIGN
/// for what it cannot (a distro path, a share).
/// </summary>
public static class GitPointers
{
    private const string Prefix = "gitdir:";

    /// <summary>The path a <c>.git</c> file names: <c>gitdir: &lt;path&gt;</c>, one line, a trailing line end tolerated.</summary>
    public static Reading<string> Parse(string text)
    {
        var body = text.TrimEnd('\r', '\n', ' ', '\t');
        return !body.StartsWith(Prefix, StringComparison.Ordinal) ? Reading.Missing<string>($"holds no \"{Prefix} <path>\" line")
            : body.Length == Prefix.Length || body[Prefix.Length] != ' ' ? Reading.Missing<string>($"\"{Prefix}\" is not followed by a space and a path")
            : Bare(body[(Prefix.Length + 1)..]);
    }

    /// <summary>The path an admin folder's <c>gitdir</c> file names: the bare path, one line.</summary>
    public static Reading<string> ParseBare(string text) => Bare(text.TrimEnd('\r', '\n', ' ', '\t'));

    private static Reading<string> Bare(string path) =>
        path.Length == 0 ? Reading.Missing<string>("names no path")
        : path.Any(char.IsControl) ? Reading.Missing<string>("holds more than one line or a control character")
        : Reading.Of(path);

    /// <summary>A pointer's target as a Windows path; <see cref="Reading{T}.Unavailable"/> (the reason says what it is) when it is not one.</summary>
    /// <param name="target">The path as written.</param>
    /// <param name="lyingIn">The folder the pointer file lies in — what a relative path is relative to.</param>
    /// <param name="automountRoots"><c>worktrees.wslAutomountRoots</c>.</param>
    public static Reading<string> Resolve(string target, string lyingIn, IReadOnlyList<string> automountRoots)
    {
        return NotLocal(target) ? Reading.Missing<string>($"\"{target}\" is a share, a device path or relative to a drive")
            : target.StartsWith('/') ? DistroPaths.ToWindows(target, automountRoots)
            : Reading.Of(Clean(PathRules.Windows, IsDrivePath(target) ? target : PathRules.Windows.Join(lyingIn, target.Replace('/', '\\'))));
    }


    private static bool NotLocal(string target) =>
        target.StartsWith(@"\\", StringComparison.Ordinal) || target.StartsWith("//", StringComparison.Ordinal) || (target.Length >= 2 && char.IsAsciiLetter(target[0]) && target[1] == ':' && !IsDrivePath(target));

    private static bool IsDrivePath(string path) => path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');

    /// <summary>Separators made backslashes, <c>.</c> and <c>..</c> applied lexically (a pointer is text; the disk is not asked), the
    /// drive letter upper case.</summary>
    private static string Clean(PathRules rules, string path)
    {
        var (prefix, segments) = rules.Split(path.Replace('/', '\\'));
        var kept = new List<string>();
        foreach (var segment in segments)
        {
            if (segment == "..")
            {
                if (kept.Count > 0)
                {
                    kept.RemoveAt(kept.Count - 1);
                }
            }
            else if (segment != ".")
            {
                kept.Add(segment);
            }
        }

        return char.ToUpperInvariant(prefix[0]) + prefix[1..] + string.Join('\\', kept);
    }
}
