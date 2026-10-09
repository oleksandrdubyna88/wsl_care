using System.Text;

using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Archive;

/// <summary>
/// Plan §15r risk consult 9/9.4 #2: the archive's children start through <c>runuser</c> without <c>-l</c>, whose PAM stack on
/// Ubuntu 24.04 names no <c>pam_systemd</c> — so no login session is made and the child stays in the service's cgroup, under its
/// limits. A stack that names <c>pam_systemd</c> (or a file it pulls in does) would move the child into a user session root does not
/// bound: the archive's actions refuse there, saying why.
/// </summary>
/// <remarks>
/// <para>The S4 own review round S-M2 — the gate fails CLOSED. It reads the stack PAM itself would read: the service's file in
/// <c>/etc/pam.d</c>, else in the vendor folder <c>/usr/lib/pam.d</c> (openSUSE ships <c>runuser</c> there), else the stack of the
/// service <c>other</c> PAM falls back to (Ubuntu's names <c>pam_systemd</c> through <c>common-session</c>); an included file is
/// looked up the same way. A stack root cannot find, a file that exists but cannot be read as root's own (a link — NixOS-WSL links
/// <c>/etc/pam.d</c> into its store —, not root's, past its cap), or an include by a path root does not follow refuses: any of them
/// could name the module for all root knows.</para>
/// <para>Every file is read as root's own (<see cref="IFileSystem.ReadStateFile"/>: owned by root, no link, a cap), each once, so a loop
/// of includes ends.</para>
/// </remarks>
public static class RunuserPam
{
    private const string Service = "runuser";
    private const string Fallback = "other";
    private const string Module = "pam_systemd";

    /// <summary>Where PAM looks for a service's stack and an included file, in order: the administrator's, then the vendor's.</summary>
    private static readonly IReadOnlyList<string> Folders = ["/etc/pam.d", "/usr/lib/pam.d"];

    /// <summary>Why the archive's children may not start through <c>runuser</c> here; empty when the stack PAM would read names no
    /// <c>pam_systemd</c>, nor does any file it pulls in.</summary>
    public static string Problem(LinuxHostPaths paths, IFileSystem files)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return Found(paths, files, Service) is { Missing: false } stack ? Judged(paths, files, stack, seen)
            : Found(paths, files, Fallback) is { Missing: false } other ? Judged(paths, files, other, seen)
            : $"no PAM stack for {Service} nor for {Fallback} was found in {string.Join(" or ", Folders)}, so what a child started through runuser would get cannot be judged; the archive does not start one here (risk consult 9/9.4 #2)";
    }

    /// <summary>One PAM file as PAM would find it: its path, its significant lines — or that none exists, or why it cannot be read.</summary>
    private sealed record StackFile(string Path, bool Missing, string Unreadable, IReadOnlyList<string> Lines);

    /// <summary>The first of <see cref="Folders"/> that holds <paramref name="name"/> — as PAM picks it, by existence: one that exists
    /// but cannot be read is that file, never skipped for the next.</summary>
    private static StackFile Found(LinuxHostPaths paths, IFileSystem files, string name) =>
        Folders.Select(folder => Read(paths, files, $"{folder}/{name}")).FirstOrDefault(file => !file.Missing, new StackFile(name, true, string.Empty, []));

    /// <summary><paramref name="file"/> names the module, or one of the files it pulls in (not yet seen) does; empty when none does.</summary>
    private static string Judged(LinuxHostPaths paths, IFileSystem files, StackFile file, HashSet<string> seen) =>
        file.Unreadable.Length > 0 ? Unchecked(file.Path, file.Unreadable)
        : file.Lines.Any(NamesTheModule) ? $"{file.Path} names {Module}: a child started through runuser would get a login session root does not bound, so the archive does not start one here (risk consult 9/9.4 #2)"
        : seen.Add(file.Path) ? Included(paths, files, file, seen)
        : string.Empty;

    private static string Included(LinuxHostPaths paths, IFileSystem files, StackFile file, HashSet<string> seen) =>
        file.Lines.Select(IncludedName).Where(name => name.Length > 0).Select(name => IncludedProblem(paths, files, file.Path, name, seen)).FirstOrDefault(problem => problem.Length > 0, string.Empty);

    private static string IncludedProblem(LinuxHostPaths paths, IFileSystem files, string from, string name, HashSet<string> seen) =>
        !IsPlainName(name) ? $"{from} pulls in {name} by a path, which root does not follow — it could name {Module}, so the archive does not start a child here (risk consult 9/9.4 #2)"
        : Found(paths, files, name) is { Missing: false } found ? Judged(paths, files, found, seen)
        : Unchecked($"{Folders[0]}/{name}", $"it is in none of {string.Join(", ", Folders)}");

    private static string Unchecked(string path, string why) =>
        $"{path} could not be checked ({why}; it must be a regular file only root may write) — it could name {Module}, so the archive does not start a child here (risk consult 9/9.4 #2)";

    private static StackFile Read(LinuxHostPaths paths, IFileSystem files, string path) =>
        files.ReadStateFile(paths.DistroPath(path), Tuning.Current.Int(ConfigKeys.Records.MaxStateFileBytes)) switch
        {
            FileReadResult.Content content => new StackFile(path, false, string.Empty, Significant(content.Bytes)),
            FileReadResult.Unreadable unreadable => new StackFile(path, false, unreadable.Reason, []),
            _ => new StackFile(path, true, string.Empty, []),
        };

    private static IReadOnlyList<string> Significant(byte[] bytes) =>
        [.. Encoding.UTF8.GetString(bytes).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && l[0] != '#')];

    private static bool NamesTheModule(string line) =>
        Words(line).Any(word => word is Module or $"{Module}.so" || word.EndsWith($"/{Module}.so", StringComparison.Ordinal));

    /// <summary>The file a line pulls in: <c>@include &lt;name&gt;</c>, or a line whose control is <c>include</c> or <c>substack</c> —
    /// after the type, however the type is spelt, and after a bracketed control too; empty for any other line.</summary>
    private static string IncludedName(string line)
    {
        var words = Words(line);
        return words is ["@include", var file, ..] ? file : ControlIncluded(words);
    }

    /// <summary>The word after the first <c>include</c> / <c>substack</c> past the type; empty when there is none.</summary>
    private static string ControlIncluded(List<string> words)
    {
        var at = words.Count > 1 ? words.FindIndex(1, word => word is "include" or "substack") : -1;
        return at > 0 && at + 1 < words.Count ? words[at + 1] : string.Empty;
    }

    /// <summary>A name PAM looks up in its folders — never a path, never a dot name.</summary>
    private static bool IsPlainName(string name) => !name.Contains('/', StringComparison.Ordinal) && !name.StartsWith('.');

    private static List<string> Words(string line) => [.. line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries)];
}
