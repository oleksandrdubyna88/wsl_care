using System.Globalization;
using System.Text;

using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;

namespace WslCare.TestSupport;

/// <summary>
/// A DETERMINISTIC, seeded generator of hostile command inputs for the command-policy property tests (E3.S1) — hand-rolled
/// rather than a property-testing package (the family NuGet rule: prefer nothing; a seed makes every failure replayable).
/// </summary>
/// <remarks>
/// It produces argvs of four kinds: every command of the never-list in many spellings (paths, case, <c>.exe</c>, extra
/// words, behind a wrapper); instances of the declared templates with valid slot values; the same with HOSTILE slot values
/// (<c>--all</c>, <c>;</c>, <c>$(…)</c>, <c>../</c>, paths under <c>~/git</c> and agent folders, unicode, newlines, NUL);
/// and token soup. Any of them may be wrapped in <c>runuser</c>'s shape, with or without a clean environment.
/// </remarks>
public sealed class HostileInputs(int seed)
{
    /// <summary>Values no slot of a destructive command may ever take.</summary>
    public static readonly IReadOnlyList<string> HostileValues =
    [
        "--all", "-a", "-af", "-f", "--force", "--filter=until=0s", "-", "--", ";", "; rm -rf ~", "&& reboot", "|| true", "| sh", "$(reboot)", "`id`", "${IFS}",
        "../", "../../git", "~/git", "~/git/wsl_care", "/home/me/git/repo", "/root/git", "/home/user/.claude/projects/x/memory",
        "/home/me/.codex/sessions", "/home/me/.gemini", "/tmp/claude/x", "C:\\Users\\me\\git", "C:\\Users\\me\\AppData\\Local\\Temp\\claude",
        "C:\\Users\\me\\.claude", "*", "?", ">", "<", "&", "\n", "a\nb", "\r", "x\0y", "\u001b[31m", "名前", "ünïcödé", "\u202Eexe.txt", " ", "",
        "/proc/sys/vm/drop_caches", "vm.drop_caches=3", "vm.drop_caches=2", "vm/drop_caches=0", "sparseVhd=true", "autoMemoryReclaim=gradual",
        "--shutdown", "--set-sparse", "prune", "worktree", "-c", "-ic", "/c", "-Command", "-EncodedCommand", "@0", "@-1", "0d", "3651d", "-1d", "30", "30 d",
    ];

    /// <summary>One spelling per command of the never-list (plan §5 <i>Never</i>, §15c #3) — the generator varies them.</summary>
    public static readonly IReadOnlyList<IReadOnlyList<string>> NeverCommands =
    [
        ["git", "worktree", "prune"], ["git", "-C", "/home/me/git/repo", "worktree", "prune", "--verbose"],
        ["docker", "system", "prune", "-a"], ["docker", "system", "prune", "--all", "--force"], ["docker", "system", "prune", "-af", "--volumes"],
        ["docker", "volume", "prune"], ["docker", "volume", "prune", "--all"], ["docker", "volume", "prune", "-a", "-f"],
        ["sysctl", "-w", "vm.drop_caches=3"], ["sysctl", "-w", "vm.drop_caches=2"], ["sysctl", "vm.drop_caches=0"], ["tee", "/proc/sys/vm/drop_caches"],
        ["sh", "-c", "echo 3 > /proc/sys/vm/drop_caches"], ["bash", "-c", "true"], ["bash", "-ic", "npm cache clean --force"], ["bash", "-lc", "x"],
        ["dash", "-c", "x"], ["zsh", "-c", "x"], ["cmd.exe", "/c", "del", "x"], ["cmd", "/C", "rd", "/s", "/q", "C:\\x"],
        ["powershell.exe", "-Command", "Remove-Item -Recurse $HOME"], ["pwsh", "-c", "x"], ["powershell", "-EncodedCommand", "ZQBjAGgAbwA="],
        ["wsl.exe", "--shutdown"], ["wsl", "--shutdown"], ["wsl.exe", "--manage", "Ubuntu", "--set-sparse", "true"],
        ["rm", "-rf", "/home/me/git"], ["rm", "-rf", "~/git/repo"], ["rm", "-rf", "/home/me/.claude/projects"], ["rmdir", "/home/me/.codex/sessions"],
        ["find", "/home/me/git", "-name", "bin", "-delete"], ["del", "/s", "C:\\Users\\me\\git\\x"], ["unlink", "/home/me/.gemini/tmp/a"],
        ["shred", "-u", "/root/.claude/x"], ["mv", "/home/me/.claude/projects", "/tmp/x"],
        ["python3", "-c", "import os; os.system('rm -rf ~')"], ["node", "-e", "require('child_process').execSync('x')"], ["perl", "-e", "unlink"],
        ["sudo", "rm", "-rf", "/"], ["env", "sh", "-c", "x"], ["xargs", "rm"], ["nohup", "docker", "system", "prune", "-a"],
        ["runuser", "-u", "me", "-c", "npm cache clean --force"], ["runuser", "-l", "me", "-c", "x"],
    ];

    private static readonly IReadOnlyList<string> WrapperPrefixes = ["sudo", "env", "nohup", "timeout 5", "nice -n 19", "ionice -c3", "setsid", "xargs"];

    private readonly Random _random = new(seed);

    public int Next(int maxExclusive) => _random.Next(maxExclusive);

    public T Pick<T>(IReadOnlyList<T> items) => items[_random.Next(items.Count)];

    /// <summary>A hostile value: one from the list, or two glued together.</summary>
    public string HostileValue() => _random.Next(4) == 0 ? Pick(HostileValues) + Pick(HostileValues) : Pick(HostileValues);

    /// <summary>A never-command in a random spelling: a path or <c>.exe</c> on the executable, a case change, extra words, a wrapper.</summary>
    public IReadOnlyList<string> NeverCommand()
    {
        var argv = Pick(NeverCommands).ToList();
        argv[0] = Spell(argv[0]);
        if (_random.Next(3) == 0)
        {
            argv.Insert(_random.Next(1, argv.Count + 1), HostileValue());
        }

        return _random.Next(4) == 0 ? [.. Pick(WrapperPrefixes).Split(' '), .. argv] : argv;
    }

    /// <summary>An instance of <paramref name="template"/>: every slot filled with a value its kind accepts, or — with
    /// <paramref name="hostile"/> — some slots with hostile values (which the template should then not match).</summary>
    public IReadOnlyList<string> Instance(CommandTemplate template, bool hostile) =>
        [template.Executable, .. template.Parts.SelectMany(part => Fill(part, hostile && _random.Next(2) == 0))];

    /// <summary>Values a slot kind ACCEPTS — the whole accepted space the generator can reach, hostile-looking ones included.</summary>
    public string Valid(SlotKind kind) => kind switch
    {
        SlotKind.Number n => Number(n),
        SlotKind.UnixSeconds => "@" + _random.NextInt64(0, 99_999_999_999).ToString(CultureInfo.InvariantCulture),
        SlotKind.Rfc3339Utc => new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(_random.NextInt64(0, 2_000_000_000)).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        SlotKind.Hex h => new string([.. Enumerable.Range(0, h.Length).Select(_ => "0123456789abcdef"[_random.Next(16)])]),
        SlotKind.UnitName u => Pick<string>(u.TypeRequired ? ["wsl-pro.service", "fstrim.timer", "a@b.service", "systemd-oomd.service", "x-y_z.socket"] : ["systemd-resolved", "wsl-pro.service", "kernel-x"]),
        SlotKind.UserName => Pick<string>(["me", "user", "_svc", "a-b", "u1000"]),
        SlotKind.Text t => TextValue(t),
        SlotKind.OneOf o => Pick(o.Values),
        SlotKind.Prefixed p => p.Prefix + Valid(p.Inner),
        SlotKind.AnyOf a => Valid(Pick(a.Kinds)),
        _ => throw new InvalidOperationException($"the generator does not know the slot kind {kind.GetType().Name}; teach it before declaring one"),
    };

    /// <summary>Random words from the hostile list and the templates' own literals.</summary>
    public IReadOnlyList<string> Soup(CommandCatalogue catalogue)
    {
        var literals = catalogue.Templates.SelectMany(t => t.Parts.OfType<ArgPart.Literal>().Select(l => l.Text)).ToList();
        var executables = catalogue.Templates.Select(t => t.Executable).Concat(["rm", "git", "docker", "sh", "sysctl", "journalctl", "npm", "runuser"]).ToList();
        return [Pick(executables), .. Enumerable.Range(0, _random.Next(0, 6)).Select(_ => _random.Next(2) == 0 && literals.Count > 0 ? Pick(literals) : HostileValue())];
    }

    /// <summary>One request of any kind, possibly wrapped in <c>runuser</c>'s shape with a clean or an inherited environment.</summary>
    public CommandRequest Request(CommandCatalogue catalogue)
    {
        IReadOnlyList<string> argv = _random.Next(5) switch
        {
            0 => NeverCommand(),
            1 or 2 when catalogue.Templates.Count > 0 => Instance(Pick(catalogue.Templates), hostile: false),
            3 when catalogue.Templates.Count > 0 => Instance(Pick(catalogue.Templates), hostile: true),
            _ => Soup(catalogue),
        };
        return _random.Next(6) == 0 ? AsTargetUser(argv) : new CommandRequest(argv, TimeSpan.FromSeconds(1));
    }

    private CommandRequest AsTargetUser(IReadOnlyList<string> argv)
    {
        var folder = Pick<string>(["/home/me/.local/bin", "/usr/bin", "/home/me/.nvm/versions/node/v22.11.0/bin", "/tmp/evil", "/home/me/git/bin", "/home/me/.local/bin/../../git"]);
        var wrapped = new CommandRequest(TargetUserArgv.Build(Pick<string>(["me", "root", "Bad User", "-me"]), $"{folder}/{Path.GetFileName(argv[0])}", [.. argv.Skip(1)]), TimeSpan.FromSeconds(1));
        return _random.Next(2) == 0
            ? wrapped with { Environment = new CommandEnvironment.Clean(new Dictionary<string, string> { ["HOME"] = "/home/me", ["PATH"] = folder }) }
            : wrapped;
    }

    private IEnumerable<string> Fill(ArgPart part, bool hostile) => part switch
    {
        ArgPart.Literal literal => [hostile && _random.Next(4) == 0 ? HostileValue() : literal.Text],
        ArgPart.Slot slot => [hostile ? HostileValue() : Valid(slot.Kind)],
        ArgPart.Repeat repeat => Enumerable.Range(0, _random.Next(repeat.Min, Math.Min(repeat.Max, repeat.Min + 4) + 1)).Select(_ => hostile && _random.Next(3) == 0 ? HostileValue() : Valid(repeat.Kind)),
        _ => throw new InvalidOperationException("ArgPart is a closed set"),
    };

    private string Number(SlotKind.Number n)
    {
        var edges = new[] { n.Min, n.Max, n.Min + 1, n.Max - 1 }.Where(v => v >= n.Min && v <= n.Max).ToArray();
        var value = _random.Next(3) == 0 ? edges[_random.Next(edges.Length)] : _random.NextInt64(n.Min, n.Max + 1);
        return value.ToString(CultureInfo.InvariantCulture) + n.Suffix;
    }

    private string TextValue(SlotKind.Text kind)
    {
        var candidates = new[] { "Clock change detected", "page allocation failure|invoked oom-killer", "a;b $(x) `y`", "vm.drop_caches=3", "~/git", "名前" }
            .Where(kind.Accepts).ToList();
        return Pick(candidates);
    }

    private string Spell(string executable)
    {
        var spelled = _random.Next(4) switch
        {
            0 => "/usr/bin/" + executable,
            1 => executable.ToUpperInvariant(),
            2 when !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) => executable + ".exe",
            _ => executable,
        };
        return _random.Next(8) == 0 ? new StringBuilder("C:\\Windows\\System32\\").Append(spelled).ToString() : spelled;
    }
}
