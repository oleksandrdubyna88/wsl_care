using System.Text;
using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Cli.Commands;
using WslCare.Core.Agents;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Processes;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary>
/// E7.S2 in-process over the distro's layout (plan §15q D4, R2): <c>config set aiAgents.extra -</c> reads the list from stdin and
/// judges every entry against the disk before writing; <c>agents probe</c> refuses root naming uid 0 and the fix; the two-phase
/// host (review M1) rebuilds the deletion policy over the manual agents' folders.
/// </summary>
public sealed class AgentsCommandTests : IDisposable
{
    private static readonly ProcessPrivilege Root = new(true, "a test says so");

    private readonly LinuxSandbox _sandbox = new("agents-cmd");

    public void Dispose() => _sandbox.Dispose();

    private CliHost Host(string stdin = "") =>
        new CliHost(_sandbox.Paths, _sandbox.Files, new FixedTimeProvider(), new RecordingCommandRunner())
        {
            StandardInput = () => new MemoryStream(Encoding.UTF8.GetBytes(stdin)),
            Rewire = static (paths, host) => (new PhysicalFileSystem(paths) { TrustedStateOwner = RegularFiles.EffectiveUid(), OwnersAreThisProcess = true }, host.Signals),
        };

    private static string Extra(string folder) =>
        $$"""[{ "cli": "/home/me/.local/bin/mycli", "side": "wsl", "name": "mycli", "dataFolders": ["{{folder}}"], "sessionGlob": "" }]""";

    [Fact]
    public void Config_set_of_the_manual_agents_reads_stdin_judges_the_folders_and_writes_the_list()
    {
        Directory.CreateDirectory(_sandbox.Paths.DistroPath("/home/me/.mycli"));

        var set = CliRun.Over(Host(Extra("/home/me/.mycli")), "config", "set", "aiAgents.extra", "-");
        var get = CliRun.Over(Host(), "config", "get", "aiAgents.extra", "--json");

        set.Exit.Should().Be(0, set.Stderr);
        get.Stdout.Should().Contain("/home/me/.mycli").And.Contain("\"side\": \"wsl\"").And.Contain("\"layer\": \"user\"");
    }

    [Theory]
    [InlineData("/home/me/.npm", "A8's cleanup folder ~/.npm")]
    [InlineData("/home/me/.config/wsl-care", "wsl-care's own folder")]
    [InlineData("/home/me/.nothing", "does not exist")]
    public void Config_set_refuses_a_folder_the_rules_refuse_naming_the_rule_and_writes_nothing(string folder, string rule)
    {
        Directory.CreateDirectory(_sandbox.Paths.DistroPath(folder == "/home/me/.nothing" ? "/home/me" : folder));

        var set = CliRun.Over(Host(Extra(folder)), "config", "set", "aiAgents.extra", "-");

        set.Exit.Should().Be((int)ExitCode.Usage);
        set.Stderr.Should().Contain("aiAgents.extra").And.Contain("mycli").And.Contain(rule).And.Contain("Nothing was written");
        File.Exists(_sandbox.Paths.UserConfigFile).Should().BeFalse();
    }

    [Fact]
    public void Config_set_takes_the_manual_agents_from_stdin_only_and_refuses_what_is_not_json()
    {
        var onArgv = CliRun.Over(Host(), "config", "set", "aiAgents.extra", Extra("/home/me/.mycli"));
        var notJson = CliRun.Over(Host("not json"), "config", "set", "aiAgents.extra", "-");

        onArgv.Exit.Should().Be((int)ExitCode.Usage);
        onArgv.Stderr.Should().Contain("is read from stdin");
        notJson.Exit.Should().Be((int)ExitCode.Usage);
        notJson.Stderr.Should().Contain("not JSON");
        File.Exists(_sandbox.Paths.UserConfigFile).Should().BeFalse();
    }

    /// <summary>Plan §15q D4, review C3: the probe never runs as root — refused whole with its own code, naming uid 0 and the fix.</summary>
    [Fact]
    public void Agents_probe_as_root_is_refused_naming_uid_0_and_the_default_user_fix()
    {
        _sandbox.Executable("/home/me/.local/bin", "mycli");

        var probe = CliRun.Over(Host() with { Privilege = Root }, "agents", "probe", "/home/me/.local/bin/mycli", "--json");

        probe.Exit.Should().Be((int)ExitCode.NotAsRoot);
        probe.Stdout.Should().BeEmpty();
        probe.Stderr.Should().Contain("not as uid 0").And.Contain("--set-default-user").And.Contain("Nothing was looked at");
    }

    [Theory]
    [InlineData("relative/mycli")]
    [InlineData("/home/me/../../etc/shadow")]
    public void Agents_probe_refuses_a_path_of_the_wrong_shape_before_anything_is_looked_at(string path)
    {
        var probe = CliRun.Over(Host(), "agents", "probe", path);

        probe.Exit.Should().Be((int)ExitCode.Usage);
        probe.Stderr.Should().Contain("agents probe");
    }

    [Fact]
    public void Agents_probe_answers_the_json_contract()
    {
        var cli = _sandbox.Write("/home/me/.local/bin/mycli", "#!/bin/false");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        _sandbox.Sized("/home/me/.mycli/a.db", 10, FixedTimeProvider.DefaultNow);

        var probe = CliRun.Over(Host(), "agents", "probe", "/home/me/.local/bin/mycli", "--json");

        probe.Exit.Should().Be(0, probe.Stderr);
        var report = JsonSerializer.Deserialize(probe.Stdout, WslCareJsonContext.Default.AgentProbeReport)!;
        report.SchemaVersion.Should().Be(1);
        report.Suggested!.DataFolders.Should().Equal("/home/me/.mycli");
    }

    /// <summary>Review M1: phase one built the deletion policy before the configuration was read; phase two rebuilds it over the
    /// manual agents' folders — a delete under one is refused by the run's file system, and allowed by phase one's.</summary>
    [Fact]
    public void The_second_phase_of_the_host_protects_the_manual_agents_folders()
    {
        var inside = _sandbox.Write("/home/me/.mycli/data/x.bin", "x");
        _sandbox.Write("/home/me/.config/wsl-care/config.json", $$"""{ "aiAgents": { "extra": {{Extra("/home/me/.mycli")}} } }""");
        var first = Host();
        var folder = Path.GetDirectoryName(inside)!;
        var scope = new DeletionScope(_sandbox.Paths.DistroPath("/home/me/.mycli"), "a test");

        var second = first.WithAgentExtras(first.LoadConfig().Config);

        second.Paths.AgentRoots.Should().Contain(_sandbox.Paths.DistroPath("/home/me/.mycli"));
        second.Files.DeleteDirectory(folder, scope).Should().BeOfType<DeletionVerdict.Refused>();
        Directory.Exists(folder).Should().BeTrue();
        first.Files.DeleteDirectory(folder, scope).Should().NotBeOfType<DeletionVerdict.Refused>("phase one's policy predates the extras — what phase two exists to fix");
    }

    [Fact]
    public void Without_manual_agents_the_second_phase_is_the_same_host()
    {
        var first = Host();

        first.WithAgentExtras(first.LoadConfig().Config).Should().BeSameAs(first);
    }
}
