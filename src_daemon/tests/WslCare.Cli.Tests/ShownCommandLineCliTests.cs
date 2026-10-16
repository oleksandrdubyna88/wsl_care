using System.Reflection;
using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary>E15.S7 through the CLI: the MCP owner on the wire carries the shown program and its safe arguments (§5), and ONE
/// definition of "printable" serves the CLI's refusals and the shown command lines (§3: it MOVED to Core, it was not copied).</summary>
public sealed class ShownCommandLineCliTests
{
    private static CliHost FixtureHost()
    {
        var paths = ProcfsFixture.PathsAt(ProcfsFixture.Root);
        var files = ProcfsFixture.LinkOverlay(new Core.Files.PhysicalFileSystem(paths), ProcfsFixture.Root);
        return new CliHost(paths, files, new FixedTimeProvider(ProcfsFixture.CapturedAt), new RecordingCommandRunner()) { Wait = (_, _) => Task.CompletedTask };
    }

    [Fact]
    public void An_mcp_owner_on_the_wire_carries_its_program_and_at_most_the_safe_arguments_and_keeps_the_command_line()
    {
        var (exit, stdout, stderr) = CliRun.Over(FixtureHost(), "status", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        using var status = JsonDocument.Parse(stdout);
        var owners = status.RootElement.GetProperty("mcpServers").GetProperty("instances").EnumerateArray()
            .Select(i => i.GetProperty("owner")).Where(o => !o.GetProperty("orphaned").GetBoolean()).ToList();
        owners.Should().HaveCount(2);
        foreach (var owner in owners)
        {
            owner.TryGetProperty("commandLine", out _).Should().BeTrue("no wire removal");
            Field(owner, "program").GetString().Should().Be("claude");
            Field(owner, "argumentsShown").EnumerateArray().Select(e => e.GetString()).Should().Equal("--output-format", "stream-json");
        }
    }

    [Fact]
    public void A_refusal_echoes_a_bidi_override_in_the_users_text_as_a_question_mark()
    {
        var (exit, _, stderr) = CliRun.Over(FixtureHost(), "status", "a" + (char)0x202e + "b");

        exit.Should().NotBe((int)ExitCode.Ok);
        stderr.Should().Contain("got \"a?b\"", "a format character can make a refusal read otherwise than it is; the CLI uses the one definition");
    }

    [Fact]
    public void The_clis_printable_was_moved_to_core_not_copied()
    {
        const BindingFlags any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        typeof(Program).Assembly.GetType("WslCare.Cli.CommandLine")!.GetMethod("Printable", any).Should().BeNull("one definition: ShownText.Printable in Core");
        typeof(Core.Processes.Redaction.ShownText).GetMethod("Printable", any).Should().NotBeNull();
    }

    private static JsonElement Field(JsonElement element, string name)
    {
        element.TryGetProperty(name, out var value).Should().BeTrue($"the object carries \"{name}\" (E15.S7 §5, the additive wire change)");
        return value;
    }
}
