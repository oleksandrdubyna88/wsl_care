using WslCare.Core.Config;
using WslCare.Core.Worktrees;

namespace WslCare.Core.Processes;

/// <summary>
/// The ONE place the product learns which <c>git.exe</c> it may start (PLAN_disk_hygiene_windows_E19.md §3 decision 9, E19.S4). It is
/// <b>never looked up on <c>PATH</c></b>: a <c>PATH</c> entry is the user's to write, and a program planted there would run with the
/// scan's reach. A candidate is a fixed list — <c>%ProgramFiles%\Git\cmd\git.exe</c>, the 64-bit Program Files when the process is
/// 32-bit, then the machine layer's <c>worktrees.gitPath</c> — and is admitted only when it exists as a file, starts with the two bytes
/// <c>MZ</c> (a PE file), and no non-administrator could replace it or plant a file beside it
/// (<see cref="IAdminOnlyPath"/>: the file, its folder, git's <c>mingw64\bin</c> where its DLLs load from, and every folder above them).
/// </summary>
/// <param name="candidates">The files to try, in order; read at each resolve, so the configuration in force is the one asked.</param>
/// <param name="acl">Whether only administrators could change a path.</param>
public sealed class GitExecutable(Func<IReadOnlyList<string>> candidates, IAdminOnlyPath acl)
{
    private static readonly string[] ProgramFilesVariables = ["ProgramFiles", "ProgramW6432"];

    /// <summary>The product's: the Program Files install (and the machine layer's choice), judged by the real security descriptors.</summary>
    public static GitExecutable Product { get; } = new(ProductCandidates, new WindowsAdminOnlyPath());

    private static IReadOnlyList<string> ProductCandidates() =>
        CandidatesOf(name => Environment.GetEnvironmentVariable(name) ?? string.Empty, Tuning.Current.Config.Text(ConfigKeys.Worktrees.GitPath));

    /// <summary>The files to try, in order: the install under each Program Files (the 64-bit one too, for a 32-bit process; once if they are
    /// the same), then the machine layer's <c>worktrees.gitPath</c> when it names one. Never a <c>PATH</c> entry.</summary>
    internal static IReadOnlyList<string> CandidatesOf(Func<string, string> variable, string configured) =>
        [.. ProgramFilesVariables.Select(name => InstallUnder(variable(name))).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase), .. new[] { configured }.Where(c => c.Length > 0)];

    private static string InstallUnder(string programFiles) => programFiles.Length > 0 ? Path.Combine(programFiles, "Git", "cmd", "git.exe") : string.Empty;

    /// <summary>The first candidate that passes every check, or why none did — each candidate named with its own reason.</summary>
    public ResolvedExecutable Resolve()
    {
        var verdicts = candidates().Select(c => (Path: c, Problem: Problem(c))).ToList();
        return verdicts.FirstOrDefault(v => v.Problem.Length == 0) is { Path.Length: > 0 } ok
            ? new ResolvedExecutable.Found(ok.Path)
            : new ResolvedExecutable.NotFound(NotFoundReason(verdicts));
    }

    private static string NotFoundReason(IReadOnlyList<(string Path, string Problem)> verdicts) =>
        verdicts.Count == 0
            ? "git.exe was not found: no Program Files install and no worktrees.gitPath (it is never looked up on PATH)"
            : "git.exe is not started: " + string.Join("; ", verdicts.Select(v => $"{v.Path} {v.Problem}")) + " (it is never looked up on PATH)";

    /// <summary>Why <paramref name="path"/> is not a git this product may start; empty when it is.</summary>
    private string Problem(string path)
    {
        var own = FileProblem(path);
        return own.Length > 0 ? own : ChainProblem(path);
    }

    private static string FileProblem(string path) => path switch
    {
        _ when !OperatingSystem.IsWindows() => "can be started on Windows only",
        _ when !File.Exists(path) => "is not a file",
        _ when !HasPeHeader(path) => "is not a Windows program (no MZ header)",
        _ => string.Empty,
    };

    /// <summary>The first two bytes are <c>MZ</c>. Read with an OS handle and a positioned read — two bytes of a program, not a
    /// file's contents.</summary>
    private static bool HasPeHeader(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> head = stackalloc byte[2];
            return RandomAccess.Read(handle, head, 0) == head.Length && head[0] == (byte)'M' && head[1] == (byte)'Z';
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string ChainProblem(string path)
    {
        var failed = Checks(path).Select(c => (c.Path, Problem: acl.Problem(c.Path, c.Role))).FirstOrDefault(c => c.Problem.Length > 0);
        return failed.Problem is { Length: > 0 } problem ? $"is not admin-only: {failed.Path} {problem}" : string.Empty;
    }

    /// <summary>Every path whose change would change what runs: the file, the folders it and its DLLs load from, and all above.</summary>
    internal static IReadOnlyList<(string Path, AdminOnlyRole Role)> Checks(string exe)
    {
        var folder = Path.GetDirectoryName(exe) ?? string.Empty;
        var loaders = new[] { folder, MingwBin(folder) }.Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var above = loaders.SelectMany(Ancestors).Distinct(StringComparer.OrdinalIgnoreCase).Where(a => !loaders.Contains(a, StringComparer.OrdinalIgnoreCase));
        return
        [
            (exe, AdminOnlyRole.ProgramFile),
            .. loaders.Select(p => (p, AdminOnlyRole.ProgramFolder)),
            .. above.Select(p => (p, AdminOnlyRole.Ancestor)),
        ];
    }

    /// <summary>Git for Windows keeps its DLLs in <c>mingw64\bin</c> beside <c>cmd</c>; empty when this install has no such folder.</summary>
    private static string MingwBin(string exeFolder)
    {
        var root = Path.GetDirectoryName(exeFolder) ?? string.Empty;
        var bin = Path.Combine(root, "mingw64", "bin");
        return root.Length > 0 && Directory.Exists(bin) ? bin : string.Empty;
    }

    private static IEnumerable<string> Ancestors(string folder)
    {
        for (var up = Path.GetDirectoryName(folder); up is not null; up = Path.GetDirectoryName(up))
        {
            yield return up;
        }
    }
}

/// <summary>What the product's runner starts a bare program name as: <c>git.exe</c> only through <see cref="GitExecutable"/>, every other
/// name as it always was (<see cref="ExecutableResolver"/>).</summary>
public static class ProductExecutables
{
    public static ResolvedExecutable Resolve(string name, CancellationToken cancellationToken) => Resolve(name, GitExecutable.Product, cancellationToken);

    public static ResolvedExecutable Resolve(string name, GitExecutable git, CancellationToken cancellationToken = default) =>
        string.Equals(name, GitCommands.Executable, StringComparison.Ordinal) ? git.Resolve() : ExecutableResolver.Resolve(name, cancellationToken);
}
