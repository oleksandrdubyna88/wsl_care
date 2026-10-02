using FluentAssertions;

using WslCare.Cli;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// The BUILT CLI never puts a raw control character on stderr: not one the user typed into a key,
/// and not one read from the user's own config file — which reaches stderr twice, as a note and as a
/// log line from the console sink.
/// </summary>
public sealed class ControlCharacterFlows
{
    [Fact]
    public async Task Control_characters_typed_into_a_key_or_read_from_the_user_layer_never_reach_stderr_raw()
    {
        using var home = new ScenarioHome("ctl");
        var typed = new[]
        {
            await home.RunAsync("config", "get", "bad\nkey"),
            await home.RunAsync("config", "set", "\u001b[2J", "1"),
            await home.RunAsync("config", "reset", "bad\rkey"),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(home.Paths.UserConfigFile)!);
        // JSON escapes: the parser turns these into a real ESC and a real newline inside the key.
        File.WriteAllText(home.Paths.UserConfigFile, "{ \"\\u001b[2J\\nforged\": 1 }");

        var read = await home.RunAsync("config", "get");

        foreach (var refused in typed)
        {
            refused.Exit.Should().Be((int)ExitCode.Usage);
            var stderr = CliStderr.Of(refused);
            stderr.Messages.Should().ContainSingle("a refusal is ONE message line whatever was typed").Which.Should().StartWith("wsl-care: unknown key");
            stderr.Unexplained.Should().BeEmpty("a typed newline cannot split a line into something unexplained");
            TerminalText.ForeignControlCharacters(refused.Stderr).Should().BeEmpty("no control character the user typed reaches the terminal");
        }

        read.Exit.Should().Be((int)ExitCode.Ok, read.Stderr);
        CliStderr.Of(read).Unexplained.Should().BeEmpty("a key read from the file cannot split the note or the log line");
        TerminalText.ForeignControlCharacters(read.Stderr).Should().BeEmpty("only the console sink's own colour escapes reach the terminal");
    }
}
