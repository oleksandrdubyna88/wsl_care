namespace WslCare.Core.Processes.Policy;

/// <summary>One command that is never run, by anything — automatic or a button (plan §5 <i>Never</i>, §15c #3).</summary>
/// <param name="Id">A stable short name (the refusal, the tests).</param>
/// <param name="Description">The rule as a person reads it.</param>
/// <param name="Matches">Whether an argv (the executable first) is this command.</param>
public sealed record NeverRule(string Id, string Description, Func<IReadOnlyList<string>, bool> Matches);

/// <summary>
/// The never-list as CODE: every rule is asked of every argv before any template is (deny wins), of the argv as given
/// and — for the one wrapper the product uses, <c>runuser</c> — of the command it wraps. No template, catalogue or
/// caller can switch a rule off.
/// </summary>
/// <remarks>
/// <para>Deliberately BROADER than the plan's list where breadth costs nothing: no action deletes a file by a command
/// (deletion goes through <c>IFileSystem</c> and its <c>DeletionPolicy</c>), so <c>rm</c> is never run at all rather than
/// "never under <c>~/git</c>"; no action needs <c>docker system prune</c> in any form or <c>docker volume prune</c> in any
/// form; a command that runs another command (<c>sudo</c>, <c>env</c>, <c>xargs</c>, <c>nohup</c>, …) would hide its
/// payload from these rules, so it is never run either, except <c>runuser</c> in its one fixed shape, whose payload is
/// judged here too.</para>
/// <para>PowerShell is the one interpreter the product starts: the Windows clock probe (E2.S3,
/// <see cref="Health.HealthCommands.WindowsClock"/>) is a fixed script with no slot. Any OTHER PowerShell command string is
/// never run.</para>
/// </remarks>
public static class NeverList
{
    private static readonly string[] PosixShells = ["sh", "bash", "rbash", "dash", "zsh", "ksh", "mksh", "pdksh", "ash", "csh", "tcsh", "fish", "busybox", "yash", "elvish", "nu"];
    private static readonly string[] Deleters = ["rm", "rmdir", "unlink", "shred", "srm", "wipe", "del", "erase", "rd", "trash", "trash-put", "gio", "truncate", "mv", "remove-item"];
    private static readonly string[] Wrappers = ["sudo", "su", "doas", "pkexec", "env", "nohup", "setsid", "xargs", "chroot", "nsenter", "unshare", "timeout", "nice", "ionice", "stdbuf", "script", "flock", "watch", "chrt", "taskset", "systemd-run", "start-process", "runas", "wsl"];
    private static readonly string[] NameKillers = ["pkill", "killall", "taskkill", "skill", "pgrep"];
    private static readonly string[] AgentFolders = [".claude", ".codex", ".gemini", ".copilot", ".rovodev", ".ollama", "anthropicclaude", "antigravity", "agy"];

    public static IReadOnlyList<NeverRule> Rules { get; } =
    [
        new("control-characters", "an argument holding a control character (a newline, a NUL, an escape)", argv => argv.Any(a => a.Any(char.IsControl))),
        new("shell", "a shell in any form - sh -c, bash -c, bash -ic, cmd /c, or a script (plan §15c #3: no shell anywhere)", IsShellCommandString),
        new("powershell", "PowerShell with anything but the fixed Windows clock probe", IsForeignPowerShell),
        new("inline-code", "an interpreter given inline code: python -c, perl -e, node -e, ruby -e, php -r, awk", IsInlineCode),
        new("git-worktree-prune", "git worktree prune - it unregisters every worktree created from Windows (plan §3, §5)", IsGitWorktreePrune),
        new("docker-system-prune", "docker system prune, in any form (plan §5: never -a; no action needs any form)", argv => IsDocker(argv, "system", "prune")),
        new("docker-volume-prune", "docker volume prune, in any form (plan §5; A4 removes a re-checked list by name)", argv => IsDocker(argv, "volume", "prune")),
        new("drop-caches-not-1", "vm.drop_caches with any value but 1, or any write to /proc/sys/vm/drop_caches (plan §5 A1)", IsDropCachesNotOne),
        new("sysctl-from-file", "sysctl loading settings from a file (-p, --load, --system): its values are not in the argv", IsSysctlFromFile),
        new("wsl-shutdown", "wsl --shutdown (or --terminate, --unregister) from inside the VM (plan §5)", IsWslShutdown),
        new("delete-by-command", "a command that deletes or moves files (rm, rmdir, unlink, shred, mv, find -delete, rsync --delete): files go only through IFileSystem and its DeletionPolicy", IsDeleteByCommand),
        new("protected-path", "a path under ~/git, an AI agent's folder or Claude's temp folder as an argument, to any command (plan §5)", argv => argv.Skip(1).Any(IsProtectedPath)),
        new("sparse-vhd", "enabling sparseVhd (--set-sparse, sparseVhd=) - it corrupted disks and blocks compaction (plan §3 0.1)", argv => argv.Any(a => Has(a, "sparsevhd") || Has(a, "--set-sparse"))),
        new("auto-memory-reclaim-gradual", "autoMemoryReclaim=gradual - it hangs with systemd + Docker Desktop (plan §3 0.1)", argv => argv.Any(a => Has(a, "automemoryreclaim")) && argv.Any(a => Has(a, "gradual"))),
        new("command-wrapper", "a command that runs another command (sudo, su, env, xargs, nohup, timeout, ...); runuser only in its one fixed shape", IsWrapper),
        new("kill-by-name", "killing processes by name (pkill, killall, taskkill /IM): only a pid the product identified", argv => NameKillers.Contains(Exe(argv))),
    ];

    /// <summary>The first rule <paramref name="argv"/> breaks, or <c>null</c> when it breaks none.</summary>
    public static NeverRule? FirstBroken(IReadOnlyList<string> argv) => argv.Count == 0 ? null : Rules.FirstOrDefault(r => r.Matches(argv));

    /// <summary>The executable's bare name, lowercased, without a directory or a Windows program extension.</summary>
    public static string Exe(IReadOnlyList<string> argv)
    {
        var name = argv[0].Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..].ToLowerInvariant();
        foreach (var extension in new[] { ".exe", ".com", ".bat", ".cmd", ".ps1" })
        {
            if (name.EndsWith(extension, StringComparison.Ordinal) && name.Length > extension.Length)
            {
                return name[..^extension.Length];
            }
        }

        return name;
    }

    /// <summary>A shell in ANY form — a command string (<c>-c</c>, <c>-ic</c>, <c>/c</c>) or a script file alike: the
    /// product never needs one (plan §15c #3: no shell anywhere).</summary>
    private static bool IsShellCommandString(IReadOnlyList<string> argv) => Exe(argv) is "cmd" || PosixShells.Contains(Exe(argv));

    /// <summary>PowerShell with anything but the fixed clock probe's arguments.</summary>
    private static bool IsForeignPowerShell(IReadOnlyList<string> argv) =>
        Exe(argv) is "powershell" or "pwsh"
        && !argv.Skip(1).SequenceEqual(Health.HealthCommands.WindowsClock.Arguments, StringComparer.Ordinal);

    private static bool IsInlineCode(IReadOnlyList<string> argv)
    {
        var exe = Exe(argv);
        var flags = exe switch
        {
            _ when exe.StartsWith("python", StringComparison.Ordinal) || exe is "py" or "pypy" or "pypy3" => new[] { "-c" },
            _ when exe.StartsWith("perl", StringComparison.Ordinal) => ["-e", "-E"],
            "node" or "nodejs" or "deno" or "bun" => ["-e", "--eval", "-p", "--print", "eval"],
            "ruby" => ["-e"],
            "php" => ["-r"],
            "lua" or "luajit" => ["-e"],
            _ => [],
        };
        return exe is "awk" or "gawk" or "mawk" or "nawk" or "osascript" || argv.Skip(1).Any(a => flags.Any(f => a.StartsWith(f, StringComparison.Ordinal)));
    }

    private static bool IsGitWorktreePrune(IReadOnlyList<string> argv)
    {
        var lower = argv.Skip(1).Select(a => a.ToLowerInvariant()).ToList();
        var worktree = lower.IndexOf("worktree");
        return Exe(argv) == "git" && worktree >= 0 && lower.Skip(worktree + 1).Contains("prune");
    }

    /// <summary>Docker with <paramref name="noun"/> then <paramref name="verb"/> among its words, in that order.</summary>
    private static bool IsDocker(IReadOnlyList<string> argv, string noun, string verb)
    {
        var lower = argv.Skip(1).Select(a => a.ToLowerInvariant()).ToList();
        var at = lower.IndexOf(noun);
        return Exe(argv) is "docker" or "com.docker.cli" or "podman" && at >= 0 && lower.Skip(at + 1).Contains(verb);
    }

    private static bool IsDropCachesNotOne(IReadOnlyList<string> argv) =>
        argv.Any(a => Has(a, "/proc/sys/vm/drop_caches") || DropCachesValue(a) is { } value && value != "1");

    /// <summary>The value after <c>vm.drop_caches=</c> (or <c>vm/drop_caches=</c>) in an argument; <c>null</c> when it names none.</summary>
    private static string? DropCachesValue(string argument)
    {
        var lower = argument.ToLowerInvariant().Replace('/', '.');
        var at = lower.IndexOf("drop_caches", StringComparison.Ordinal);
        return at < 0 ? null : lower[(at + "drop_caches".Length)..].TrimStart().TrimStart('=').Trim();
    }

    private static bool IsSysctlFromFile(IReadOnlyList<string> argv) =>
        Exe(argv) == "sysctl" && argv.Skip(1).Any(a => a is "-p" or "-f" or "--load" or "--system" || a.StartsWith("--load=", StringComparison.Ordinal) || (a.StartsWith('-') && !a.StartsWith("--", StringComparison.Ordinal) && a.Contains('p')));

    private static bool IsWslShutdown(IReadOnlyList<string> argv) =>
        Exe(argv) == "wsl" && argv.Skip(1).Any(a => a.ToLowerInvariant() is "--shutdown" or "--terminate" or "-t" or "--unregister");

    private static bool IsDeleteByCommand(IReadOnlyList<string> argv)
    {
        var exe = Exe(argv);
        var rest = argv.Skip(1).Select(a => a.ToLowerInvariant()).ToList();
        return Deleters.Contains(exe)
            || (exe is "find" && rest.Any(a => a is "-delete" or "-exec" or "-execdir" or "-ok" or "-okdir"))
            || (exe is "rsync" && rest.Any(a => a.StartsWith("--delete", StringComparison.Ordinal) || a == "--remove-source-files"))
            || (exe is "git" && rest.Contains("clean"));
    }

    private static bool IsWrapper(IReadOnlyList<string> argv)
    {
        var exe = Exe(argv);
        return Wrappers.Contains(exe) || (exe == "runuser" && !TargetUserArgv.HasTheOneShape(argv));
    }

    /// <summary>
    /// A path — absolute, <c>~</c>-relative, or after an <c>=</c> — at or under a protected place, recognised by its
    /// SEGMENTS so it holds for any account's home: <c>&lt;home&gt;/git</c>, an AI agent's folder, Claude's temp folder.
    /// </summary>
    internal static bool IsProtectedPath(string argument)
    {
        var path = argument[(argument.LastIndexOf('=') + 1)..].Replace('\\', '/');
        if (!(path.StartsWith('/') || path.StartsWith('~') || (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')))
        {
            return false;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(s => s.ToLowerInvariant()).ToList();
        return segments.Any(s => AgentFolders.Contains(s))
            || Adjacent(segments, "tmp", "claude") || Adjacent(segments, "temp", "claude") || Adjacent(segments, "roaming", "claude")
            || IsHomeGit(segments);
    }

    /// <summary><c>~/git</c>, <c>/root/git</c>, <c>/home/&lt;user&gt;/git</c>, <c>C:/Users/&lt;user&gt;/git</c>.</summary>
    private static bool IsHomeGit(IReadOnlyList<string> segments) =>
        Enumerable.Range(0, segments.Count).Any(i => segments[i] == "git" && IsHomeEnd(segments, i));

    private static bool IsHomeEnd(IReadOnlyList<string> segments, int gitAt) =>
        (gitAt >= 1 && segments[gitAt - 1] is "~" or "root")
        || (gitAt >= 2 && segments[gitAt - 2] is "home" or "users");

    private static bool Adjacent(IReadOnlyList<string> segments, string first, string second) =>
        Enumerable.Range(0, Math.Max(segments.Count - 1, 0)).Any(i => segments[i] == first && segments[i + 1] == second);

    private static bool Has(string argument, string fragment) => argument.Contains(fragment, StringComparison.OrdinalIgnoreCase);
}
