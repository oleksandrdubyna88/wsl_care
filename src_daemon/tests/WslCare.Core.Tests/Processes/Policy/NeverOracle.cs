using System.Text.RegularExpressions;

namespace WslCare.Core.Tests.Processes.Policy;

/// <summary>
/// The plan's never-list (§5 <i>Never</i>, §15c #3) written a SECOND time, independently of <c>NeverList</c> and on purpose
/// in a different shape — the argv joined into one lower-cased line and matched by patterns, with every wrapper peeled off
/// — so the property test is not the product's rules agreeing with themselves. It states only what the PLAN forbids;
/// the product may refuse more (it does: every shell, every delete-by-command), never less.
/// </summary>
internal static partial class NeverOracle
{
    private static readonly string[] Wrappers = ["sudo", "su", "doas", "env", "nohup", "setsid", "xargs", "timeout", "nice", "ionice", "stdbuf", "runuser", "chroot", "nsenter", "flock", "watch"];
    private static readonly string[] Shells = ["sh", "bash", "dash", "zsh", "ksh", "mksh", "ash", "fish", "busybox", "csh", "tcsh", "rbash"];

    /// <summary>Whether <paramref name="argv"/> — or any command it wraps — is something the plan says is never run.</summary>
    public static bool IsNever(IReadOnlyList<string> argv) =>
        argv.Count > 0 && (argv.Any(a => a.Any(char.IsControl)) || Commands(argv).Any(IsNeverCommand));

    /// <summary>The argv, and every argv a wrapper in it runs (each word after a wrapper may start the payload).</summary>
    private static IEnumerable<IReadOnlyList<string>> Commands(IReadOnlyList<string> argv)
    {
        yield return argv;
        for (var i = 0; i < argv.Count; i++)
        {
            if (Wrappers.Contains(Base(argv[i])))
            {
                for (var j = i + 1; j < argv.Count; j++)
                {
                    yield return [.. argv.Skip(j)];
                }
            }
        }
    }

    private static bool IsNeverCommand(IReadOnlyList<string> argv)
    {
        var exe = Base(argv[0]);
        var line = " " + string.Join(' ', argv.Skip(1).Select(a => a.ToLowerInvariant())) + " ";
        return (Shells.Contains(exe) && Regex.IsMatch(line, @" -[a-z]*c[a-z]* ")) // sh -c, bash -ic, …
            || (exe == "cmd" && Regex.IsMatch(line, @" /[ck] "))
            || (exe is "powershell" or "pwsh" && Regex.IsMatch(line, @" -(c|command|encodedcommand|ec|e|file|f) ") && !IsTheClockProbe(argv))
            || (exe == "git" && Regex.IsMatch(line, @" worktree( \S+)* prune "))
            || (exe == "docker" && Regex.IsMatch(line, @" system( \S+)* prune( \S+)* (-a|--all|-[a-z]*a[a-z]*)(=\S*)? "))
            || (exe == "docker" && Regex.IsMatch(line, @" volume( \S+)* prune "))
            || DropCachesNotOne(line)
            || (exe == "wsl" && line.Contains(" --shutdown ", StringComparison.Ordinal))
            || (IsDeleter(exe, line) && ProtectedPathArgument(argv))
            || line.Contains("sparsevhd", StringComparison.Ordinal) && line.Contains("true", StringComparison.Ordinal)
            || line.Contains("--set-sparse", StringComparison.Ordinal)
            || (line.Contains("automemoryreclaim", StringComparison.Ordinal) && line.Contains("gradual", StringComparison.Ordinal));
    }

    private static bool IsTheClockProbe(IReadOnlyList<string> argv) =>
        argv.Skip(1).SequenceEqual(Core.Health.HealthCommands.WindowsClock.Arguments, StringComparer.Ordinal);

    private static bool DropCachesNotOne(string line) =>
        line.Contains("/proc/sys/vm/drop_caches", StringComparison.Ordinal)
        || DropCaches().Matches(line).Any(m => m.Groups[1].Value != "1");

    [GeneratedRegex(@"vm[./]drop_caches\s*=?\s*(\S*)", RegexOptions.CultureInvariant)]
    private static partial Regex DropCaches();

    private static bool IsDeleter(string exe, string line) =>
        exe is "rm" or "rmdir" or "unlink" or "shred" or "del" or "erase" or "rd" or "mv"
        || (exe == "find" && line.Contains(" -delete ", StringComparison.Ordinal));

    /// <summary>An argument under any home's <c>git</c>, an AI agent's folder, or Claude's temp folder.</summary>
    private static bool ProtectedPathArgument(IReadOnlyList<string> argv) =>
        argv.Skip(1).Select(a => a.Replace('\\', '/').ToLowerInvariant()).Any(a =>
            Regex.IsMatch(a, @"(^~|/home/[^/]+|^/root|/users/[^/]+)/git(/|$)")
            || Regex.IsMatch(a, @"/\.(claude|codex|gemini|copilot|rovodev|ollama)(/|$)")
            || Regex.IsMatch(a, @"/(tmp|temp)/claude(/|$)"));

    private static string Base(string executable)
    {
        var name = executable.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..].ToLowerInvariant();
        return name.EndsWith(".exe", StringComparison.Ordinal) ? name[..^4] : name;
    }
}
