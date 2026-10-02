using System.Reflection;
using System.Text.RegularExpressions;

using FluentAssertions;

using WslCare.Core.Docker;
using WslCare.Core.Health;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Systemd;

namespace WslCare.Core.Tests.Processes.Policy;

/// <summary>
/// The <see cref="CommandPolicy"/> rule by rule: every never-rule refuses a known instance (the list of rules is read from
/// <see cref="NeverList.Rules"/>, so a rule without an instance here is a red test), deny by default, the never-list asked
/// before any template, the one <c>runuser</c> shape, and every read command the collectors build allowed.
/// </summary>
public sealed partial class CommandPolicyTests
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(5);

    /// <summary>One instance per rule id — the test below fails for a rule that has none.</summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Instances = new Dictionary<string, IReadOnlyList<string>>
    {
        ["control-characters"] = ["docker", "volume", "rm", "abc\ndef"],
        ["shell"] = ["bash", "-ic", "npm cache clean --force"],
        ["powershell"] = ["powershell.exe", "-NoProfile", "-Command", "Remove-Item -Recurse C:\\x"],
        ["inline-code"] = ["python3", "-c", "import shutil"],
        ["git-worktree-prune"] = ["git", "worktree", "prune"],
        ["docker-system-prune"] = ["docker", "system", "prune", "-a"],
        ["docker-volume-prune"] = ["docker", "volume", "prune", "--all"],
        ["drop-caches-not-1"] = ["sysctl", "-w", "vm.drop_caches=3"],
        ["sysctl-from-file"] = ["sysctl", "-p", "/etc/sysctl.d/x.conf"],
        ["wsl-shutdown"] = ["wsl.exe", "--shutdown"],
        ["delete-by-command"] = ["rm", "-rf", "/var/tmp/x"],
        ["protected-path"] = ["du", "-sh", "/home/me/git"],
        ["sparse-vhd"] = ["wslconfig-writer", "sparseVhd=true"],
        ["auto-memory-reclaim-gradual"] = ["wslconfig-writer", "autoMemoryReclaim=gradual"],
        ["command-wrapper"] = ["sudo", "journalctl", "--disk-usage"],
        ["kill-by-name"] = ["pkill", "dotnet"],
    };

    public static TheoryData<string> RuleIds => [.. NeverList.Rules.Select(r => r.Id)];

    private static CommandRequest Request(params string[] argv) => new(argv, Ceiling);

    [Theory]
    [MemberData(nameof(RuleIds))]
    public void Every_rule_of_the_never_list_refuses_its_known_instance_and_says_which_rule(string id)
    {
        Instances.Should().ContainKey(id, "every never-rule needs a known instance here");

        var verdict = CommandPolicy.Product.Review(new CommandRequest(Instances[id], Ceiling));

        verdict.Should().BeOfType<CommandVerdict.Refused>().Which.Reason.Should().Contain($"never-list ({id}:");
    }

    [Fact]
    public void Every_instance_here_names_a_rule_that_exists()
    {
        Instances.Keys.Should().BeSubsetOf(NeverList.Rules.Select(r => r.Id));
    }

    [Theory]
    [InlineData("sysctl", "-w", "vm.drop_caches=1")]
    [InlineData("journalctl", "--vacuum-time=30d")]
    [InlineData("docker", "volume", "rm", "0997307242ffbe630f0d1b184b88272a86e2a32302d0a52e303e1fd10c18d493")]
    [InlineData("du", "-sh", "/var/cache/apt")]
    public void A_harmless_argv_breaks_no_never_rule(params string[] argv)
    {
        NeverList.FirstBroken(argv).Should().BeNull();
    }

    [Theory]
    [InlineData("rm", "-rf", "~/git/repo")]
    [InlineData("RM.EXE", "x")]
    [InlineData("/usr/bin/rm", "x")]
    [InlineData("find", "/tmp", "-delete")]
    [InlineData("rsync", "-a", "--delete", "a", "b")]
    [InlineData("git", "clean", "-fdx")]
    [InlineData("docker", "system", "prune")]
    [InlineData("docker", "--host", "x", "volume", "prune", "-f")]
    [InlineData("sysctl", "vm.drop_caches=2")]
    [InlineData("sysctl", "-w", "vm/drop_caches=0")]
    [InlineData("tee", "/proc/sys/vm/drop_caches")]
    [InlineData("sh", "script.sh")]
    [InlineData("cmd", "/c", "del", "x")]
    [InlineData("pwsh", "-File", "x.ps1")]
    [InlineData("wsl", "--unregister", "Ubuntu")]
    [InlineData("wsl.exe", "--manage", "Ubuntu", "--set-sparse", "true")]
    [InlineData("journalctl", "--grep=/home/me/.claude/projects")]
    [InlineData("docker", "cp", "x:/a", "C:\\Users\\me\\AppData\\Local\\Temp\\claude\\b")]
    [InlineData("runuser", "-l", "me", "-c", "npm")]
    [InlineData("runuser", "-u", "me", "npm")]
    [InlineData("/usr/sbin/runuser", "-u", "me", "--", "/usr/bin/npm")]
    [InlineData("taskkill", "/IM", "node.exe")]
    public void Every_spelling_of_a_never_command_is_refused(params string[] argv)
    {
        CommandPolicy.Product.Review(Request(argv)).Should().BeOfType<CommandVerdict.Refused>().Which.Reason.Should().Contain("never-list");
    }

    [Fact]
    public void An_argv_no_template_declares_is_refused_deny_by_default()
    {
        var verdict = CommandPolicy.Product.Review(Request("docker", "image", "ls"));

        verdict.Should().BeOfType<CommandVerdict.Refused>().Which.Reason.Should().Contain("no declared command template matches");
    }

    [Fact]
    public void A_never_command_behind_runuser_is_refused_even_when_a_planted_user_template_declares_it()
    {
        var planted = new CommandTemplate("planted-rm", CommandScope.User, "rm", [new ArgPart.Slot("what", new SlotKind.OneOf(["x"]))], Ceiling, 1024);
        var request = new CommandRequest(TargetUserArgv.Build("me", "/usr/bin/rm", ["x"]), Ceiling) { Environment = new CommandEnvironment.Clean(new Dictionary<string, string>()) };

        var verdict = CommandPolicy.Over(new CommandCatalogue([planted])).Review(request);

        verdict.Should().BeOfType<CommandVerdict.Refused>().Which.Reason.Should().Contain("delete-by-command", "the wrapped command is judged by the never-list a second time");
    }

    [Fact]
    public void A_template_cannot_admit_a_never_command_because_the_never_list_is_asked_first()
    {
        var planted = new CommandTemplate("planted", CommandScope.Machine, "docker", [new ArgPart.Literal("system"), new ArgPart.Literal("prune"), new ArgPart.Literal("-a")], Ceiling, 1024);

        var verdict = CommandPolicy.Over(new CommandCatalogue([planted])).Review(Request("docker", "system", "prune", "-a"));

        verdict.Should().BeOfType<CommandVerdict.Refused>().Which.Reason.Should().Contain("docker-system-prune");
    }

    [Fact]
    public void The_windows_clock_probe_is_the_one_powershell_argv_allowed()
    {
        CommandPolicy.Product.Review(HealthCommands.WindowsClock.ToRequest()).IsAllowed.Should().BeTrue();
        CommandPolicy.Product.Review(Request([HealthCommands.PowerShell, .. HealthCommands.WindowsClock.Arguments.Take(3), "Get-Date"])).IsAllowed.Should().BeFalse();
    }

    /// <summary>Every read command the collectors build, from their own factories.</summary>
    private static IReadOnlyList<ToolCommand> AllCollectorCommands()
    {
        var since = new DateTimeOffset(2026, 10, 2, 8, 0, 0, TimeSpan.Zero);
        var id = new string('a', 64);
        IEnumerable<ToolCommand> all =
        [
            DockerCommands.Version, DockerCommands.SystemDf, DockerCommands.SystemDfVerbose, DockerCommands.DanglingVolumes, DockerCommands.ContainerList,
            DockerCommands.Stats, DockerCommands.EngineStart, DockerCommands.ContainerInspect([id]), DockerCommands.ContainerInspect([.. Enumerable.Repeat(id, DockerCommands.InspectBatch)]),
            DockerCommands.Events(since, since.AddDays(1)), DockerCommands.Backfill(since, since.AddDays(1)), DockerCommands.EventStream(since, since.AddMinutes(10), TimeSpan.FromSeconds(30)),
            SystemdCommands.JournalDiskUsage, SystemdCommands.ListBoots, SystemdCommands.FailedUnits, SystemdCommands.Version, SystemdCommands.TimeSync,
            .. new List<string> { "wsl-pro.service", "fstrim.timer", "earlyoom.service", "systemd-oomd.service" }.Concat(Core.Doctor.DoctorRun.Units).Select(SystemdCommands.ShowUnit),
            SystemdCommands.Search(since, new JournalScope.Unit("systemd-resolved"), "Clock change detected"),
            SystemdCommands.Search(since, new JournalScope.Kernel(), "page allocation failure|invoked oom-killer"),
            SystemdCommands.Search(since, new JournalScope.Unit("systemd-resolved"), "wsl-care-live-contract-matches-nothing"),
            HealthCommands.WindowsClock, HealthCommands.SnapList,
        ];
        return [.. all];
    }

    public static TheoryData<string> CollectorCommands() => [.. AllCollectorCommands().Select(c => c.Display)];

    [Theory]
    [MemberData(nameof(CollectorCommands))]
    public void Every_read_command_the_collectors_build_is_allowed_by_the_product_policy(string display)
    {
        var command = Rebuild(display);

        var verdict = CommandPolicy.Product.Review(command);

        verdict.Should().Be(CommandVerdict.Allowed, (verdict as CommandVerdict.Refused)?.Reason ?? display);
    }

    [Fact]
    public void A_container_inspect_with_a_short_or_hostile_id_is_not_a_declared_instance()
    {
        var template = DockerCommands.ContainerInspect([new string('a', 64)]).Argv.ToList();
        template[^1] = "--all";

        CommandPolicy.Product.Review(new CommandRequest(template, Ceiling)).IsAllowed.Should().BeFalse();
        CommandPolicy.Product.Review(new CommandRequest([.. template.SkipLast(1), "abc123"], Ceiling)).IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void A_runner_cannot_be_built_without_a_policy()
    {
        var act = () => new ProcessCommandRunner(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void No_product_file_but_the_runner_itself_reaches_the_unguarded_test_seam()
    {
        var offenders = SourceFiles()
            .Where(f => !f.EndsWith("ProcessCommandRunner.cs", StringComparison.Ordinal))
            .Where(f => UnguardedSeam().IsMatch(File.ReadAllText(f)))
            .ToList();

        offenders.Should().BeEmpty("only the runner's own tests may start a process without the product policy");
    }

    [Fact]
    public void The_unguarded_seam_scan_finds_the_seam_where_it_is_defined()
    {
        // The companion: a scan that matches nothing passes forever.
        SourceFiles().Single(f => f.EndsWith("ProcessCommandRunner.cs", StringComparison.Ordinal))
            .Should().Match(f => UnguardedSeam().IsMatch(File.ReadAllText(f)));
    }

    [GeneratedRegex(@"UnguardedForItsOwnTests", RegexOptions.CultureInvariant)]
    private static partial Regex UnguardedSeam();

    private static IReadOnlyList<string> SourceFiles()
    {
        var root = typeof(CommandPolicyTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "WslCare.SourceRoot").Value!;
        return [.. Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))];
    }

    /// <summary>The TheoryData carries the display (a ToolCommand is not serialisable data); rebuild it from the same list.</summary>
    private static CommandRequest Rebuild(string display) => AllCollectorCommands().First(c => c.Display == display).ToRequest();
}
