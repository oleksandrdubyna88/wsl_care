using System.Text;

using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Archive;

/// <summary>
/// Plan §15r risk consult 9/9.4 #2: the archive's children start through <c>runuser</c> without <c>-l</c>, whose PAM stack on
/// Ubuntu 24.04 names no <c>pam_systemd</c> — so no login session is made and the child stays in the service's cgroup, under its
/// limits. A distribution whose <c>/etc/pam.d/runuser</c> (or a file it includes, one level down) names <c>pam_systemd</c> would
/// move the child into a user session root does not bound: the archive's actions refuse there, saying why.
/// </summary>
/// <remarks>Read as root's own file (<see cref="IFileSystem.ReadStateFile"/>: owned by root, no link, a cap). A stack that cannot
/// be read is not judged here — <c>runuser</c> itself would then fail, as it does for A8 and A17.</remarks>
public static class RunuserPam
{
    private const string Stack = "/etc/pam.d/runuser";
    private const string Folder = "/etc/pam.d";
    private const string Module = "pam_systemd";

    /// <summary>Why the archive's children may not start through <c>runuser</c> here; empty when nothing says so.</summary>
    /// <remarks>The S4 code round, finding 3: every file the stack pulls in is followed — an include of an include too, each file once
    /// (so a loop of includes ends) — and an included file that cannot be read refuses, since it could name the module for all root
    /// knows. The stack file itself unread is not judged: <c>runuser</c> then fails on its own, as it does for A8 and A17.</remarks>
    public static string Problem(LinuxHostPaths paths, IFileSystem files) =>
        Read(paths, files, Stack) is { Readable: true } top ? Judged(paths, files, Stack, top.Lines, new HashSet<string>(StringComparer.Ordinal) { Stack }) : string.Empty;

    /// <summary>One PAM file as read: its significant lines, or that it could not be read.</summary>
    private sealed record StackFile(bool Readable, IReadOnlyList<string> Lines);

    /// <summary><paramref name="path"/> names the module, or one of the files it pulls in (not yet seen) does; empty when none does.</summary>
    private static string Judged(LinuxHostPaths paths, IFileSystem files, string path, IReadOnlyList<string> lines, HashSet<string> seen) =>
        lines.Any(NamesTheModule)
            ? $"{path} names {Module}: a child started through runuser would get a login session root does not bound, so the archive does not start one here (risk consult 9/9.4 #2)"
            : lines.Select(Included).Where(name => name.Length > 0).Select(name => $"{Folder}/{name}").Where(seen.Add)
                .Select(included => IncludedProblem(paths, files, included, seen)).FirstOrDefault(problem => problem.Length > 0, string.Empty);

    private static string IncludedProblem(LinuxHostPaths paths, IFileSystem files, string path, HashSet<string> seen) =>
        Read(paths, files, path) is { Readable: true } file
            ? Judged(paths, files, path, file.Lines, seen)
            : $"{path} could not be checked (missing, or not a regular file only root may write) — it could name {Module}, so the archive does not start a child here (risk consult 9/9.4 #2)";

    private static StackFile Read(LinuxHostPaths paths, IFileSystem files, string path) =>
        files.ReadStateFile(paths.DistroPath(path), Tuning.Current.Int(ConfigKeys.Records.MaxStateFileBytes)) is FileReadResult.Content content
            ? new StackFile(true, [.. Encoding.UTF8.GetString(content.Bytes).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && l[0] != '#')])
            : new StackFile(false, []);

    private static bool NamesTheModule(string line) =>
        line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Any(word => word is Module or $"{Module}.so" || word.EndsWith($"/{Module}.so", StringComparison.Ordinal));

    /// <summary>The file a line pulls in: <c>@include &lt;name&gt;</c>, or <c>&lt;type&gt; include|substack &lt;name&gt;</c> — a
    /// plain name only, never a path; empty for any other line.</summary>
    private static string Included(string line)
    {
        var words = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var name = words switch
        {
            ["@include", var file, ..] => file,
            [_, "include" or "substack", var file, ..] => file,
            _ => string.Empty,
        };
        return name.Contains('/', StringComparison.Ordinal) || name.StartsWith('.') ? string.Empty : name;
    }
}
