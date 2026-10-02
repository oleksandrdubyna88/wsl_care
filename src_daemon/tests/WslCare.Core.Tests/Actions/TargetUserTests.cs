using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Actions;

/// <summary>
/// Plan §15c #2: whose home and tools a user-scoped action means — the discovery table — and the ONE way a tool runs as that
/// user: <c>runuser -u &lt;user&gt; -- &lt;full path&gt;</c>, resolved in the fixed bin folders before the start, a clean
/// environment, and a policy that refuses every other shape.
/// </summary>
public sealed class TargetUserTests : IDisposable
{
    private const string Root = "root:x:0:0:root:/root:/bin/bash\n";
    private const string Me = "me:x:1000:1000:Me:/home/me:/bin/bash\n";
    private const string Other = "ann:x:1001:1001:Ann:/home/ann:/usr/bin/zsh\n";
    private const string Daemon = "svc:x:1002:1002::/var/lib/svc:/usr/sbin/nologin\n";
    private const string Nobody = "nobody:x:65534:65534::/nonexistent:/bin/sh\n";

    private readonly LinuxSandbox _sandbox = new("target-user");

    public void Dispose() => _sandbox.Dispose();

    public static TheoryData<string, string, string, string> Table => new()
    {
        // passwd, wsl.conf (or <none>), expected kind, expected name or reason fragment
        { Root + Me + Daemon + Nobody, "<none>", "found", "me" },
        { Root + Me + Other, "[user]\ndefault=ann\n", "found", "ann" },
        { Root + Me + Other, "[boot]\nsystemd=true\n[user]\n  default = \"me\"  \n", "found", "me" },
        { Root + Me + Other, "<none>", "ambiguous", "2 login accounts (me, ann)" },
        { Root + Me + Other, "[automount]\nroot=/mnt/\n", "ambiguous", "no [user] default=" },
        { Root + Daemon + Nobody, "<none>", "none", "no account with uid >= 1000" },
        { Root + Me, "[user]\ndefault=ghost\n", "ambiguous", "which" },
        { Root + Me, "[user]\ndefault=Bad User\n", "ambiguous", "not a valid account name" },
        { Root + Me, "[user]\ndefault=root\n", "found", "root" },
        { Root + "me:x:1000:1000::relative/home:/bin/bash\n", "<none>", "ambiguous", "no usable name or home" },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void The_target_user_is_the_wsl_conf_default_else_the_single_login_account_and_anything_else_refuses(string passwd, string wslConf, string kind, string expected)
    {
        _sandbox.Write("/etc/passwd", passwd);
        if (wslConf != "<none>")
        {
            _sandbox.Write("/etc/wsl.conf", wslConf);
        }

        var result = TargetUserDiscovery.Discover(_sandbox.Files, _sandbox.Paths);

        switch (kind)
        {
            case "found":
                result.Should().BeOfType<TargetUserResult.Found>().Which.User.Name.Should().Be(expected);
                result.Refusal.Should().BeEmpty();
                break;
            case "ambiguous":
                result.Should().BeOfType<TargetUserResult.Ambiguous>().Which.Reason.Should().Contain(expected);
                result.Refusal.Should().StartWith("no target user:");
                break;
            default:
                result.Should().BeOfType<TargetUserResult.None>().Which.Reason.Should().Contain(expected);
                break;
        }
    }

    [Fact]
    public void An_unreadable_passwd_refuses_rather_than_guessing()
    {
        TargetUserDiscovery.Discover(_sandbox.Files, _sandbox.Paths).Should().BeOfType<TargetUserResult.Ambiguous>().Which.Reason.Should().Contain("passwd");
    }

    [Fact]
    public void A_tool_runs_as_the_target_user_through_runuser_by_its_full_path_with_a_clean_environment()
    {
        var npm = _sandbox.Executable("/home/me/.local/bin", "npm");
        _sandbox.Executable("/usr/bin", "npm");
        var user = new TargetUser("me", 1000, "/home/me");
        var template = UserTemplate();

        var built = TargetUserCommands.Build(template, ["cache", "clean", "--force"], user, TargetUserCommands.BinFolders(user, _sandbox.Paths, _sandbox.Files));

        var request = built.Should().BeOfType<UserCommand.Ready>().Subject.Request;
        request.Argv.Should().HaveCount(8);
        request.Argv.Take(4).Should().Equal("runuser", "-u", "me", "--");
        Path.GetFullPath(request.Argv[4]).Should().Be(Path.GetFullPath(npm), "the FIRST bin folder that holds it, by its full path");
        request.Argv.Skip(5).Should().Equal("cache", "clean", "--force");
        var environment = request.Environment.Should().BeOfType<CommandEnvironment.Clean>().Subject.Variables;
        environment.Keys.Should().BeEquivalentTo(["HOME", "USER", "LOGNAME", "PATH"], "nothing of this process's environment reaches the child");
        environment["HOME"].Should().Be("/home/me");
        environment["PATH"].Should().Be("/home/me/.local/bin:/home/me/.cargo/bin:/usr/local/bin:/usr/bin");
        CommandPolicy.Over(new CommandCatalogue([template])).Review(request).Should().Be(CommandVerdict.Allowed);
    }

    [Fact]
    public void Nvm_s_default_version_comes_first_and_an_alias_it_cannot_resolve_is_left_out()
    {
        _sandbox.Write("/home/me/.nvm/alias/default", "22\n");
        foreach (var version in new[] { "v20.18.0", "v22.9.0", "v22.11.0" })
        {
            Directory.CreateDirectory(_sandbox.Paths.DistroPath($"/home/me/.nvm/versions/node/{version}/bin"));
        }

        var npm = _sandbox.Executable("/home/me/.nvm/versions/node/v22.11.0/bin", "npm");
        _sandbox.Executable("/usr/bin", "npm");
        var user = new TargetUser("me", 1000, "/home/me");

        var folders = TargetUserCommands.BinFolders(user, _sandbox.Paths, _sandbox.Files);
        var built = TargetUserCommands.Build(UserTemplate(), [], user, folders);

        folders[0].DistroPath.Should().Be("/home/me/.nvm/versions/node/v22.11.0/bin");
        Path.GetFullPath(((UserCommand.Ready)built).Request.Argv[4]).Should().Be(Path.GetFullPath(npm));
        _sandbox.Write("/home/me/.nvm/alias/default", "lts/*\n");
        TargetUserCommands.BinFolders(user, _sandbox.Paths, _sandbox.Files).Select(f => f.DistroPath).Should().NotContain(p => p.Contains(".nvm", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("22", "v22.11.0")]
    [InlineData("v22.9", "v22.9.0")]
    [InlineData("v20.18.0", "v20.18.0")]
    [InlineData("node", "v22.11.0")]
    [InlineData("stable", "v22.11.0")]
    [InlineData("18", null)]
    [InlineData("lts/*", null)]
    [InlineData("default", null)]
    public void Nvm_aliases_resolve_to_the_highest_installed_match_or_to_nothing(string alias, string? expected)
    {
        NvmVersion.Choose(alias, ["v20.18.0", "v22.9.0", "v22.11.0", "not-a-version"]).Should().Be(expected);
    }

    [Fact]
    public void A_tool_that_is_in_none_of_the_bin_folders_is_refused_before_any_start()
    {
        var built = TargetUserCommands.Build(UserTemplate(), [], new TargetUser("me", 1000, "/home/me"), TargetUserCommands.BinFolders(new TargetUser("me", 1000, "/home/me"), _sandbox.Paths, _sandbox.Files));

        built.Should().BeOfType<UserCommand.Refused>().Which.Reason.Should().Contain("npm is not in me's bin folders");
    }

    [Theory]
    [InlineData("/tmp/evil/npm")]
    [InlineData("/home/me/.local/bin/../../git/npm")]
    [InlineData("/home/me/git/bin/npm")]
    [InlineData("relative/.local/bin/npm")]
    public void The_policy_refuses_a_wrapped_tool_outside_the_bin_folders_even_when_its_name_matches(string path)
    {
        var request = new CommandRequest(TargetUserArgv.Build("me", path, []), TimeSpan.FromSeconds(5)) { Environment = new CommandEnvironment.Clean(new Dictionary<string, string>()) };

        CommandPolicy.Over(new CommandCatalogue([UserTemplate()])).Review(request).IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void The_policy_refuses_a_wrapped_command_that_inherits_this_process_s_environment()
    {
        var request = new CommandRequest(TargetUserArgv.Build("me", "/usr/bin/npm", []), TimeSpan.FromSeconds(5));

        CommandPolicy.Over(new CommandCatalogue([UserTemplate()])).Review(request).Should().BeOfType<CommandVerdict.Refused>().Which.Reason.Should().Contain("clean environment");
    }

    [Fact]
    public void The_policy_refuses_a_machine_template_run_through_runuser_and_a_user_template_run_without_it()
    {
        var machine = CommandTemplate.Fixed(Systemd.SystemdCommands.JournalDiskUsage);
        var clean = new CommandEnvironment.Clean(new Dictionary<string, string>());
        var policy = CommandPolicy.Over(new CommandCatalogue([machine, UserTemplate()]));

        policy.Review(new CommandRequest(TargetUserArgv.Build("me", "/usr/bin/journalctl", ["--disk-usage"]), TimeSpan.FromSeconds(5)) { Environment = clean }).IsAllowed.Should().BeFalse();
        policy.Review(new CommandRequest(["npm"], TimeSpan.FromSeconds(5))).IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void Every_login_account_s_git_and_agent_folders_are_protected_not_only_those_of_this_process_s_home()
    {
        _sandbox.Write("/etc/passwd", Root + Me + Other + Daemon);
        var paths = _sandbox.Paths.WithProtectedHomes(TargetUserDiscovery.ProtectedHomes(_sandbox.Files, _sandbox.Paths));
        var files = new Core.Files.PhysicalFileSystem(paths);
        var victim = _sandbox.Write("/home/ann/git/repo/file.txt", "x");
        var agent = _sandbox.Write("/root/.claude/projects/p/s.jsonl", "x");

        files.DeleteFile(victim, new DeletionScope(_sandbox.Paths.DistroPath("/home/ann"), "test")).Should().BeOfType<DeletionVerdict.Refused>().Which.Rule.Should().Be(DeletionRule.GitFolder);
        files.DeleteFile(agent, new DeletionScope(_sandbox.Paths.DistroPath("/root"), "test")).Should().BeOfType<DeletionVerdict.Refused>().Which.Rule.Should().Be(DeletionRule.AgentFolder);
        File.Exists(victim).Should().BeTrue();
    }

    private static CommandTemplate UserTemplate() =>
        new("npm-cache-clean", CommandScope.User, "npm", [new ArgPart.Repeat("args", new SlotKind.OneOf(["cache", "clean", "--force"]), 0, 3)], TimeSpan.FromMinutes(1), 1024);
}
