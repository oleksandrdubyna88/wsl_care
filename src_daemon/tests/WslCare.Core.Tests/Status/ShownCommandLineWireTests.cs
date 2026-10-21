using System.Text;
using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Json;
using WslCare.Core.Records;
using WslCare.Core.Status;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Status;

/// <summary>E15.S7 §5: the wire change is ADDITIVE. Every process row gains <c>program</c>, <c>argumentsShown</c> (at most
/// <c>processes.shownSafeArgs</c> safe arguments, never the constant) and <c>argumentsTotal</c>; an MCP owner gains the first
/// two; <c>commandLine</c> stays, so no client breaks. Read as JSON — the shape a client sees.</summary>
public sealed class ShownCommandLineWireTests
{
    private static JsonDocument StatusOver(ConfigLoadResult config, int safeArgs = -1)
    {
        var paths = ProcfsFixture.PathsAt(ProcfsFixture.Root);
        var files = ProcfsFixture.LinkOverlay(new PhysicalFileSystem(paths), ProcfsFixture.Root);
        using var scope = safeArgs < 0 ? null : Tuning.Use(Machine($$"""{ "processes": { "shownSafeArgs": {{safeArgs}} } }""").Config);
        var sample = new LinuxProbe(files, paths, new FixedTimeProvider(ProcfsFixture.CapturedAt)).Sample(CancellationToken.None);
        var slow = new LastSlowParts(Reading.Missing<AgedPart<ContainerStatsSample>>(LastFullRun.NoFullRunYet), Reading.Missing<AgedPart<WindowsClockSample>>(LastFullRun.NoFullRunYet));
        return JsonDocument.Parse(JsonSerializer.Serialize(StatusReports.From(sample, slow, config), WslCareJsonContext.Default.StatusReport));
    }

    private static ConfigLoadResult Machine(string json) =>
        ConfigLoader.Load([(ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults())),
            (new ConfigLayerFile(ConfigLayer.Machine, "/etc/wsl-care/config.json"), new FileReadResult.Content(Encoding.UTF8.GetBytes(json)))]);

    private static ConfigLoadResult Defaults() => Machine("{}");

    private static IReadOnlyList<JsonElement> Rows(JsonDocument status) =>
        [.. status.RootElement.GetProperty("vm").GetProperty("processes").GetProperty("top").EnumerateArray()
            .Concat(status.RootElement.GetProperty("vm").GetProperty("processes").GetProperty("mntWalkers").EnumerateArray())];

    [Fact]
    public void Every_process_row_carries_the_program_the_safe_arguments_and_their_count_and_still_the_command_line()
    {
        using var status = StatusOver(Defaults());

        var rows = Rows(status);
        rows.Should().NotBeEmpty();
        foreach (var row in rows)
        {
            row.TryGetProperty("commandLine", out _).Should().BeTrue("no wire removal");
            row.TryGetProperty("program", out var program).Should().BeTrue($"pid {row.GetProperty("pid")} carries its program");
            program.ValueKind.Should().Be(JsonValueKind.String);
            row.TryGetProperty("argumentsShown", out var shown).Should().BeTrue();
            shown.ValueKind.Should().Be(JsonValueKind.Array);
            row.TryGetProperty("argumentsTotal", out var total).Should().BeTrue();
            total.GetInt32().Should().BeGreaterThanOrEqualTo(shown.GetArrayLength());
        }
    }

    [Fact]
    public void The_shown_arguments_are_at_most_the_key_and_never_hold_the_redaction_constant()
    {
        using var status = StatusOver(Defaults());

        foreach (var row in Rows(status))
        {
            var shown = Field(row, "argumentsShown").EnumerateArray().Select(e => e.GetString()).ToList();
            shown.Should().HaveCountLessThanOrEqualTo(2, $"processes.shownSafeArgs is 2 (pid {row.GetProperty("pid")})");
            shown.Should().NotContain(a => a!.Contains("<redacted>", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void A_known_row_shows_its_program_and_leading_arguments_as_the_design_does()
    {
        using var status = StatusOver(Defaults());

        var claude = Rows(status).First(r => r.GetProperty("commandLine").GetString()!.Contains("native-binary/claude", StringComparison.Ordinal));
        Field(claude, "program").GetString().Should().Be("claude");
        Field(claude, "argumentsShown").EnumerateArray().Select(e => e.GetString()).Should().Equal("--output-format", "stream-json");
    }

    [Fact]
    public void A_safe_argument_count_of_zero_shows_no_arguments_at_all_but_still_counts_them()
    {
        using var status = StatusOver(Defaults(), safeArgs: 0);

        foreach (var row in Rows(status))
        {
            Field(row, "argumentsShown").GetArrayLength().Should().Be(0);
        }

        Rows(status).Should().Contain(r => r.GetProperty("argumentsTotal").GetInt32() > 0);
    }

    private static JsonElement Field(JsonElement element, string name)
    {
        element.TryGetProperty(name, out var value).Should().BeTrue($"the object carries \"{name}\" (E15.S7 §5, the additive wire change)");
        return value;
    }
}
