using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Mcp;
using WslCare.Core.Tests.Collectors;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Mcp;

/// <summary>
/// E14 S2d (Q13): an MCP server an INTERPRETER runs — <c>npx @playwright/mcp</c>, measured twice in the captured 2026-10-02 tree as
/// <c>claude</c> → <c>npm exec @playwright/mcp@latest</c> → (a shell) → <c>node …/node_modules/.bin/playwright-mcp</c> — is
/// recognised by its SCRIPT: the interpreter's first word that is no option, a file name the catalogue names, under a
/// <c>node_modules</c> folder. Never by a later argument, never by a script of that name outside an installed package.
/// </summary>
public sealed class McpInterpreterServersTests : IDisposable
{
    private const string Claude = "/home/me/.vscode-server/extensions/anthropic.claude-code-2.1.0-linux-x64/resources/native-binary/claude";
    private const string NpxBin = "/home/me/.npm/_npx/9833c18b2d85bc59/node_modules/.bin/playwright-mcp";

    private readonly SyntheticProcTree _tree = new SyntheticProcTree().MemInfo(1000, 0);

    public void Dispose() => _tree.Dispose();

    private static EffectiveConfig Defaults() =>
        ConfigLoader.Load([(ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults()))]).Config;

    /// <summary>The VS Code server's node → claude, the agent every server below hangs from.</summary>
    private McpInterpreterServersTests Agent()
    {
        _tree.Process(100, 1, "/a", 10, words: ["/home/me/.vscode-server/bin/abc/node", "--dns-result-order=ipv4first"])
            .Process(200, 100, "/a", 10, words: [Claude, "--output-format", "stream-json"]);
        return this;
    }

    private McpFinding Find() =>
        McpInstances.Find(
            new ProcessCollector(_tree.Files, _tree.Paths, new FixedTimeProvider())
                .Read(Reading.Of(new KernelFacts(100, 4096)), new ContainerSet([]), CancellationToken.None)
                .Should().BeOfType<Reading<ProcessSnapshot>.Available>().Subject.Value.All,
            McpSettings.From(Defaults()).Watched);

    [Fact]
    public void An_npx_started_playwright_mcp_is_an_instance_under_its_agent_through_npm_exec()
    {
        Agent();
        _tree.Process(300, 200, "/a", 10, words: ["npm exec @playwright/mcp@latest"])
            .Process(310, 300, "/a", 10, words: ["sh", "-c", "playwright-mcp"])
            .Process(320, 310, "/a", 40_000, words: ["node", NpxBin]);

        var found = Find();

        var instance = found.Instances.Should().ContainSingle(i => i.Server.Name == "playwright-mcp").Subject;
        instance.Process.Pid.Should().Be(320, "the node process running the script is the server; npm exec and the shell are its launchers");
        instance.Owner.Should().BeOfType<McpOwner.Agent>().Which.Pid.Should().Be(200);
    }

    [Fact]
    public void Node_running_another_script_is_not_playwright_mcp_and_a_mention_after_the_script_is_not_either()
    {
        Agent();
        _tree.Process(320, 200, "/a", 10, words: ["node", "/home/me/.npm/_npx/abc/node_modules/.bin/other-mcp"])
            .Process(330, 200, "/a", 10, words: ["node", "/home/me/git/p/server.js", "--name", NpxBin])
            .Process(340, 200, "/a", 10, words: ["node", "/tmp/playwright-mcp"])
            .Process(350, 200, "/a", 10, words: ["/usr/local/bin/playwright-mcp"])
            .Process(360, 200, "/a", 10, words: ["python3", NpxBin]);

        var found = Find();

        found.Instances.Should().NotContain(i => i.Server.Name == "playwright-mcp",
            "another script, a later argument, a script of that name outside an installed package, a binary and another interpreter are none of them the catalogued server");
    }

    [Fact]
    public void The_shebang_form_with_interpreter_options_before_the_script_is_the_server()
    {
        // coai plan round 2026-10-08 (session bf45d6ae), findings 1 and 2: a script started through its shebang runs as the
        // interpreter (argv[0] node, never playwright-mcp), and an option may stand before the script.
        Agent();
        _tree.Process(320, 200, "/a", 10, words: ["/usr/bin/node", "--no-warnings", NpxBin, "--headless"])
            .Process(330, 200, "/a", 10, words: ["nodejs", NpxBin]);

        var found = Find();

        found.Instances.Where(i => i.Server.Name == "playwright-mcp").Select(i => i.Process.Pid).Should().Equal(320, 330);
    }

    [Fact]
    public void Playwright_mcp_is_watched_by_default_and_refused_as_a_user_program()
    {
        McpSettings.From(Defaults()).Watched.Select(s => s.Name).Should().Contain(["coai-mcp", "playwright-mcp"], "every catalogued server is watched by default");
        McpServerCatalogue.Names.Should().Contain("playwright-mcp");
        ConfigValidation.Parse(ConfigKeys.McpServers.Programs, "playwright-mcp").Should().BeOfType<ValueCheck.Invalid>()
            .Which.Message.Should().Contain(McpUserPrograms.ACatalogueServer);
    }
}
