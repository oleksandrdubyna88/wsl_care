using FluentAssertions;

using WslCare.FakeTool;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// The harness's own guarantees, proved positively — without these, "no config verb started a tool"
/// would pass just as well with fakes that were never on PATH or a log that is never written.
/// </summary>
/// <remarks>The lookups go through a real shell (<c>/bin/sh</c>, <c>cmd.exe</c>) given the scenario's
/// environment, because a shell resolves a bare <c>docker</c> through <c>PATH</c> exactly as the CLI's
/// own process launcher will once a verb shells out (E2).</remarks>
public sealed class FakeToolFlows
{
    [Fact]
    public async Task A_bare_docker_looked_up_on_the_scenario_path_reaches_the_fake_which_records_its_argv_and_prints_the_scripted_fixture()
    {
        using var home = new ScenarioHome("fake-docker");
        const string fixture = "synthetic/harness-self-test.txt";
        home.Script("docker", ["ps", "-a"], exitCode: 3, stdoutFixture: fixture, stderr: "scripted stderr");

        var result = await ThroughAShellAsync(home, "docker", "ps", "-a");

        result.Exit.Should().Be(3, "the fake exits with the scripted code");
        result.Stdout.Should().Be(File.ReadAllText(ScenarioHome.Fixture(fixture)), "the fixture's bytes, unchanged");
        result.Stderr.Should().Contain("scripted stderr");
        home.Calls.Should().ContainSingle().Which.Matches("docker", ["ps", "-a"]).Should().BeTrue("the fake records the argv it was given");
    }

    [Fact]
    public async Task Every_fake_is_installed_under_its_tool_name_answers_as_that_tool_and_refuses_an_unscripted_call()
    {
        using var home = new ScenarioHome("fake-names");

        foreach (var tool in FakeToolProtocol.Tools)
        {
            // Started directly, so the argv reaches it through .NET's own quoting, spaces included.
            var result = await ChildProcess.RunAsync(InstalledFake(home, tool), ["--unscripted", "argument with spaces"], home.Environment, home.WorkingDirectory);

            result.Exit.Should().Be(FakeToolProtocol.Unscripted, $"{tool} was not scripted");
            result.Stderr.Should().Contain($"fake {tool}: no scripted answer for: {tool} --unscripted argument with spaces");
        }

        home.Calls.Select(c => c.Tool).Should().Equal(FakeToolProtocol.Tools);
        home.Calls.Should().OnlyContain(c => c.Argv.SequenceEqual(new[] { "--unscripted", "argument with spaces" }), "argv is recorded exactly, one argument per element");
    }

    [Fact]
    public async Task An_answer_with_an_output_flag_writes_the_fixture_to_the_file_the_call_names_and_nothing_to_stdout()
    {
        using var home = new ScenarioHome("fake-output", ["docker"]);
        const string fixture = "synthetic/harness-self-test.txt";
        home.Answer(new FakeAnswer("docker", ["pull"], 0, ScenarioHome.Fixture(fixture), string.Empty) { Prefix = true, OutputFlag = "--output" });
        var target = Path.Combine(home.WorkingDirectory, "downloaded.bin");

        var written = await ChildProcess.RunAsync(InstalledFake(home, "docker"), ["pull", "--output", target], home.Environment, home.WorkingDirectory);
        var flagLast = await ChildProcess.RunAsync(InstalledFake(home, "docker"), ["pull", "--output"], home.Environment, home.WorkingDirectory);

        written.Exit.Should().Be(0);
        written.Stdout.Should().BeEmpty("the bytes go to the file, not to stdout");
        File.ReadAllBytes(target).Should().Equal(File.ReadAllBytes(ScenarioHome.Fixture(fixture)), "the fixture's bytes, unchanged");
        flagLast.Exit.Should().Be(FakeToolProtocol.Unscripted, "a call that names no file after the flag is refused, loudly");
        flagLast.Stderr.Should().Contain("the call has none");
    }

    [Fact]
    public async Task A_tool_the_scenario_did_not_fake_is_not_reachable_on_its_path()
    {
        // git is on every runner and on the owner's machine; inside a scenario it must not be.
        using var home = new ScenarioHome("fake-isolated");

        var result = await ThroughAShellAsync(home, "git", "--version");

        result.Exit.Should().NotBe(0, "the scenario PATH holds the fakes and nothing else");
        result.Stdout.Should().NotContain("git version");
        home.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_fake_started_outside_a_scenario_refuses_to_stand_in_for_the_real_tool()
    {
        using var home = new ScenarioHome("fake-outside");

        var result = await ChildProcess.RunAsync(InstalledFake(home, "docker"), ["ps"], new Dictionary<string, string?> { [FakeToolProtocol.CallsVariable] = null });

        result.Exit.Should().Be(FakeToolProtocol.NotInAScenario);
        result.Stderr.Should().Contain("not inside a scenario");
        home.Calls.Should().BeEmpty();
    }

    private static string InstalledFake(ScenarioHome home, string tool) =>
        Path.Combine(home.FakeBin, FakeToolProtocol.FileName(tool, OperatingSystem.IsWindows()));

    /// <summary>A real shell resolving <paramref name="tool"/> on the scenario PATH; arguments without
    /// spaces, because cmd.exe re-parses its command line by rules of its own.</summary>
    private static Task<ChildResult> ThroughAShellAsync(ScenarioHome home, string tool, params string[] args)
    {
        args.Should().OnlyContain(a => !a.Contains(' '), "a shell lookup here passes plain words only");
        return OperatingSystem.IsWindows()
            ? ChildProcess.RunAsync(Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/d", "/c", tool, .. args], home.Environment, home.WorkingDirectory)
            : ChildProcess.RunAsync("/bin/sh", ["-c", "exec \"$0\" \"$@\"", tool, .. args], home.Environment, home.WorkingDirectory);
    }
}
