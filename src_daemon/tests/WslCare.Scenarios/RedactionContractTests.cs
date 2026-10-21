using System.Text.Json;

using FluentAssertions;

namespace WslCare.Scenarios;

/// <summary>E15.S7 §4 and §5 as the checked-in contract files say them: the head golden's 52 process rows carry the three new
/// fields and nothing shown in them holds the constant, and the three
/// <c>processes.*</c> keys are in <c>contracts/config-keys.json</c>. Platform-neutral: it reads files, it does not run the CLI
/// (<see cref="GoldenContractTests"/> holds the files equal to what the CLI answers, on Linux).</summary>
public sealed class RedactionContractTests
{
    private static JsonDocument Golden() => JsonDocument.Parse(File.ReadAllText(Path.Combine(ReleaseFiles.Root, "contracts", "golden", "head", "status.json")));

    private static IReadOnlyList<JsonElement> Rows(JsonDocument status)
    {
        var processes = status.RootElement.GetProperty("vm").GetProperty("processes");
        return [.. processes.GetProperty("top").EnumerateArray().Concat(processes.GetProperty("mntWalkers").EnumerateArray())];
    }

    [Fact]
    public void Every_process_row_of_the_head_golden_carries_program_arguments_shown_and_arguments_total()
    {
        using var golden = Golden();

        var rows = Rows(golden);
        rows.Should().HaveCount(50, "the captured tree's top 30 and its 20 /mnt walkers (the other two commandLines of the file are the MCP owners')");
        foreach (var row in rows)
        {
            row.TryGetProperty("program", out var program).Should().BeTrue($"pid {row.GetProperty("pid")}");
            program.GetString().Should().NotBeNullOrEmpty();
            row.TryGetProperty("argumentsShown", out var shown).Should().BeTrue();
            shown.GetArrayLength().Should().BeLessThanOrEqualTo(2);
            shown.EnumerateArray().Select(e => e.GetString()).Should().NotContain(a => a!.Contains("<redacted>", StringComparison.Ordinal));
            row.TryGetProperty("argumentsTotal", out _).Should().BeTrue();
        }
    }

    [Fact]
    public void The_mcp_owners_of_the_head_golden_carry_the_program_and_the_safe_arguments_and_keep_the_command_line()
    {
        using var golden = Golden();

        var owners = golden.RootElement.GetProperty("mcpServers").GetProperty("instances").EnumerateArray().Select(i => i.GetProperty("owner")).Where(o => !o.GetProperty("orphaned").GetBoolean()).ToList();

        owners.Should().HaveCount(2);
        foreach (var owner in owners)
        {
            Field(owner, "program").GetString().Should().Be("claude");
            Field(owner, "argumentsShown").EnumerateArray().Select(e => e.GetString()).Should().Equal("--output-format", "stream-json");
            owner.TryGetProperty("commandLine", out _).Should().BeTrue("no wire removal");
        }
    }

    [Fact]
    public void The_three_redaction_keys_are_in_the_config_keys_contract()
    {
        using var keys = JsonDocument.Parse(File.ReadAllText(Path.Combine(ReleaseFiles.Root, "contracts", "config-keys.json")));

        var text = keys.RootElement.GetRawText();
        foreach (var name in new[] { "processes.secretMinChars", "processes.shownSafeArgs", "processes.maxJudgedArgChars" })
        {
            text.Should().Contain($"\"{name}\"");
        }
    }

    private static JsonElement Field(JsonElement element, string name)
    {
        element.TryGetProperty(name, out var value).Should().BeTrue($"the object carries \"{name}\" (E15.S7 §5, the additive wire change)");
        return value;
    }
}
