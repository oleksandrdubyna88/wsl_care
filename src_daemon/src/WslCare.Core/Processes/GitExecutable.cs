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
/// <param name="reuseJudgement">Remember a candidate that passed, and trust the permissions of its folders on a later call as long as the program files
/// themselves are the SAME files (path, length, creation and last-write time) — measured at 12 ms a call without it (50 calls 601 ms, 2026-10-21), and a
/// scan makes hundreds. A replaced program is judged afresh, and so is everything once 60 seconds have passed since the full pass; the identity covers the
/// PROGRAM FILES only — not the DLLs they load, nor a permission changed under an unchanged program inside that minute.</param>
/// <param name="clock">The clock the 60 seconds are read from; the system's unless a test moves one.</param>
public sealed class GitExecutable(Func<IReadOnlyList<string>> candidates, IAdminOnlyPath acl, bool reuseJudgement = false, TimeProvider? clock = null)
{
    private static readonly string[] ProgramFilesVariables = ["ProgramFiles", "ProgramW6432"];

    /// <summary>How long a passed judgement may be reused: a minute from the full pass that made it. The identity check covers the PROGRAM FILES (path,
    /// length, creation and last-write time), not the DLLs they load nor the permissions of the folders above; this bound is what limits that gap. A fixed
    /// bound of a security rule, not a tuning: it stays a constant (and a test pins it), not a key a user could stretch.</summary>
    private static readonly TimeSpan ReuseFor = TimeSpan.FromSeconds(60);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Identity, DateTimeOffset At)> _passed = new(StringComparer.OrdinalIgnoreCase);

    private TimeProvider Clock => clock ?? TimeProvider.System;

    /// <summary>The product's: the Program Files install (and the machine layer's choice), judged by the real security descriptors, the judgement
    /// reused while the program files are unchanged.</summary>
    public static GitExecutable Product { get; } = new(ProductCandidates, new WindowsAdminOnlyPath(), reuseJudgement: true);

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
        var refused = new List<(string Path, string Problem)>();
        foreach (var candidate in candidates())
        {
            var problem = Problem(candidate);
            if (problem.Length == 0)
            {
                return new ResolvedExecutable.Found(candidate);
            }

            refused.Add((candidate, problem));
        }

        return new ResolvedExecutable.NotFound(NotFoundReason(refused));
    }

    private static string NotFoundReason(IReadOnlyList<(string Path, string Problem)> verdicts) =>
        verdicts.Count == 0
            ? "git.exe was not found: no Program Files install and no worktrees.gitPath (it is never looked up on PATH)"
            : "git.exe is not started: " + string.Join("; ", verdicts.Select(v => $"{v.Path} {v.Problem}")) + " (it is never looked up on PATH)";

    /// <summary>Why <paramref name="path"/> is not a git this product may start; empty when it is.</summary>
    private string Problem(string path)
    {
        var own = FileProblem(path);
        if (own.Length > 0)
        {
            _passed.TryRemove(path, out _);
            return own;
        }

        var identity = reuseJudgement ? Identity(path) : string.Empty;
        var now = Clock.GetUtcNow();
        if (reuseJudgement && _passed.TryGetValue(path, out var before) && before.Identity == identity && now - before.At < ReuseFor)
        {
            return string.Empty;
        }

        var chain = ChainProblem(path);
        if (reuseJudgement && chain.Length == 0)
        {
            _passed[path] = (identity, now);
        }
        else
        {
            _passed.TryRemove(path, out _);
        }

        return chain;
    }

    /// <summary>Which files the program is: path, length, creation and last-write time of every program file of the install (the one given and the real
    /// <c>git.exe</c> of each DLL folder). A replaced file changes it; an unreadable one makes it unique, so it is never reused.</summary>
    private static string Identity(string path)
    {
        try
        {
            return string.Join('|', Checks(path).Where(c => c.Role == AdminOnlyRole.ProgramFile).Select(c => new FileInfo(c.Path)).Select(f => $"{f.FullName}:{f.Length}:{f.CreationTimeUtc.Ticks}:{f.LastWriteTimeUtc.Ticks}"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Guid.NewGuid().ToString("N");
        }
    }

    private static string FileProblem(string path) => path switch
    {
        _ when !OperatingSystem.IsWindows() => "can be started on Windows only",
        _ when !File.Exists(path) => "is not a file",
        _ when !HasPeHeader(path) => "is not a Windows program (no MZ header)",
        _ when DllFolders(path).Count == 0 => $"has none of {string.Join(", ", DllFolderNames.Select(n => n + @"\bin"))} beside it, so the program it starts and the DLLs it loads cannot be named",
        _ => string.Empty,
    };

    /// <summary>The folders git for Windows keeps its real <c>git.exe</c> and its DLLs in, by architecture; <c>cmd\git.exe</c> only starts the one
    /// of them that exists.</summary>
    private static readonly string[] DllFolderNames = ["mingw64", "mingw32", "clangarm64"];

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

    /// <summary>Every path whose change would change what runs: the file; the real <c>git.exe</c> in each DLL folder (<c>cmd\git.exe</c> starts it, so
    /// it is the program that actually runs); the folders they and the DLLs load from; and all above.</summary>
    internal static IReadOnlyList<(string Path, AdminOnlyRole Role)> Checks(string exe)
    {
        var folder = Path.GetDirectoryName(exe) ?? string.Empty;
        var dlls = DllFolders(exe);
        var loaders = new[] { folder }.Concat(dlls).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var programs = new[] { exe }.Concat(dlls.Select(d => Path.Combine(d, "git.exe")).Where(File.Exists)).Distinct(StringComparer.OrdinalIgnoreCase);
        var above = loaders.SelectMany(Ancestors).Distinct(StringComparer.OrdinalIgnoreCase).Where(a => !loaders.Contains(a, StringComparer.OrdinalIgnoreCase));
        return
        [
            .. programs.Select(p => (p, AdminOnlyRole.ProgramFile)),
            .. loaders.Select(p => (p, AdminOnlyRole.ProgramFolder)),
            .. above.Select(p => (p, AdminOnlyRole.Ancestor)),
        ];
    }

    /// <summary>The <c>&lt;arch&gt;\bin</c> folders of this install that exist: under the install's root — the folder above <c>cmd</c>, or above
    /// <c>&lt;arch&gt;\bin</c> when the file given is the real <c>git.exe</c> itself.</summary>
    private static IReadOnlyList<string> DllFolders(string exe)
    {
        var folder = Path.GetDirectoryName(exe) ?? string.Empty;
        var root = InstallRoot(folder);
        return root.Length == 0 ? [] : [.. DllFolderNames.Select(n => Path.Combine(root, n, "bin")).Where(Directory.Exists)];
    }

    private static string InstallRoot(string exeFolder)
    {
        var parent = Path.GetDirectoryName(exeFolder) ?? string.Empty;
        var inDllFolder = string.Equals(Path.GetFileName(exeFolder), "bin", StringComparison.OrdinalIgnoreCase) && DllFolderNames.Contains(Path.GetFileName(parent), StringComparer.OrdinalIgnoreCase);
        return inDllFolder ? Path.GetDirectoryName(parent) ?? string.Empty : parent;
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
