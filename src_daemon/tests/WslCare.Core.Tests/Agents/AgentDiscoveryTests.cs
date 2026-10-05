using FluentAssertions;

using WslCare.Core.Agents;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Agents;

/// <summary>
/// Discovery (plan §4.6, §15q D3): an agent is tracked by a binary on PATH, an npm global package, or a data folder — and
/// nothing is ever executed: a binary is looked up, a version read from disk or "not asked". As root only folders count.
/// </summary>
public sealed class AgentDiscoveryTests : IDisposable
{
    private readonly LinuxSandbox _sandbox = new("agent-discovery");

    public void Dispose() => _sandbox.Dispose();

    private AgentPresence Of(IReadOnlyList<AgentPresence> found, string id) => found.Single(p => p.Entry.Id == id);

    private string Bin(string name)
    {
        var bin = _sandbox.Paths.DistroPath("/home/me/.local/bin");
        Directory.CreateDirectory(bin);
        var file = Path.Combine(bin, OperatingSystem.IsWindows() ? name + ".exe" : name);
        File.WriteAllText(file, "#!/bin/sh\nexit 99\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return bin;
    }

    [Fact]
    public void An_agent_is_tracked_by_a_binary_on_path_by_an_npm_package_or_by_a_folder_alone()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the distro's PATH rule (an execute bit) is Linux's: run in WSL or on the Linux legs");
        var bin = Bin("claude");
        _sandbox.Write("/home/me/.npm-global/lib/node_modules/@openai/codex/package.json", """{ "name": "@openai/codex", "version": "0.44.0" }""");
        _sandbox.Write("/home/me/.gemini/settings.json", "{}");

        var found = AgentDiscovery.Discover(_sandbox.Paths, _sandbox.Files, bin, asRoot: false);

        Of(found, "claude-code").DetectedBy.Should().Equal(AgentDiscovery.Binary);
        Of(found, "claude-code").Binaries.Should().ContainSingle().Which.Name.Should().Be("claude");
        Of(found, "codex").DetectedBy.Should().Equal(AgentDiscovery.Npm);
        Of(found, "codex").Version.Should().Be("0.44.0", "read from the npm package's package.json, not by running codex");
        Of(found, "gemini-cli").DetectedBy.Should().Equal(AgentDiscovery.Folder);
        Of(found, "ollama").Tracked.Should().BeFalse();
    }

    [Fact]
    public void A_native_install_names_its_version_in_its_link_target_and_nothing_is_executed_to_ask()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "a file link needs no privilege on Linux; run in WSL or on the Linux legs");
        var version = _sandbox.Write("/home/me/.local/share/claude/versions/2.1.3", "#!/bin/sh\nexit 99\n");
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(version, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var bin = _sandbox.Paths.DistroPath("/home/me/.local/bin");
        Directory.CreateDirectory(bin);
        File.CreateSymbolicLink(Path.Combine(bin, "claude"), version);

        var claude = Of(AgentDiscovery.Discover(_sandbox.Paths, _sandbox.Files, bin, asRoot: false), "claude-code");

        claude.Version.Should().Be("2.1.3");
    }

    [Fact]
    public void An_agent_without_a_version_on_disk_says_not_asked()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the distro's PATH rule (an execute bit) is Linux's: run in WSL or on the Linux legs");
        var bin = Bin("qwen");

        var qwen = Of(AgentDiscovery.Discover(_sandbox.Paths, _sandbox.Files, bin, asRoot: false), "qwen-code");

        qwen.Version.Should().BeEmpty();
        qwen.VersionReason.Should().Contain("nothing is executed");
    }

    [Fact]
    public void As_root_only_folders_count_and_no_version_is_asked()
    {
        var bin = Bin("claude");
        _sandbox.Write("/home/me/.npm-global/lib/node_modules/@openai/codex/package.json", """{ "version": "0.44.0" }""");
        _sandbox.Write("/home/me/.gemini/settings.json", "{}");

        var found = AgentDiscovery.Discover(_sandbox.Paths, _sandbox.Files, bin, asRoot: true);

        Of(found, "claude-code").Tracked.Should().BeFalse("root never searches the user's PATH");
        Of(found, "codex").Tracked.Should().BeFalse("root never reads the user's packages");
        Of(found, "codex").VersionReason.Should().Be(AgentDiscovery.NotAskedAsRoot);
        Of(found, "gemini-cli").DetectedBy.Should().Equal(AgentDiscovery.Folder);
    }

    /// <summary>Discovery has no command runner to start anything with — the structure is the guarantee (plan §15q D3): its
    /// one entry point takes paths, a file system and a PATH string, nothing that can run a process.</summary>
    [Fact]
    public void Discovery_and_the_walk_have_no_way_to_start_a_process()
    {
        var parameters = typeof(AgentDiscovery).GetMethods().Cast<System.Reflection.MethodBase>()
            .Concat(typeof(AgentWalk).GetMethods())
            .Concat(typeof(AgentWalk).GetConstructors())
            .SelectMany(m => m.GetParameters()).Select(p => p.ParameterType);

        parameters.Should().NotContain(t => t == typeof(WslCare.Core.Processes.ICommandRunner));
    }
}
