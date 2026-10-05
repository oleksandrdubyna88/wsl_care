using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Actions;

namespace WslCare.Scenarios;

/// <summary>
/// <c>contracts/actions.json</c>, <c>contracts/exit-codes.json</c> and (plan §15o) <c>contracts/history-reasons.json</c> (plan §15f #10, §15j m3): the action ids (incl.
/// <c>A5Testcontainers</c> / <c>A6Unused</c>) with the execution order, and every exit code — the lists the extension reads
/// in E6.S2 instead of keeping its own. The emitting side ENUMERATES its types (<see cref="ActionId.All"/>,
/// <see cref="ActionId.ExecutionOrder"/>, every value of <see cref="ExitCode"/>), never a hand-typed list, so a code or an id
/// added without the contract fails here, where it is added (family testing rule: enumerate, never retype). Regenerate with
/// <c>WSL_CARE_WRITE_GOLDENS=1</c> and review the diff as a contract change.
/// </summary>
public sealed class ContractFilesTests
{
    private static string ContractsDirectory => Path.Combine(ReleaseFiles.Root, "contracts");

    /// <summary>The text <c>contracts/actions.json</c> must hold, from the types.</summary>
    internal static string ActionsText() => Indented(new JsonObject
    {
        ["schemaVersion"] = 1,
        ["description"] = "Every action id the daemon knows (its auto.* switch names; A5Testcontainers and A6Unused are ids) and the order a run takes them in. Generated from ActionId.All / ActionId.ExecutionOrder by ContractFilesTests.",
        ["ids"] = new JsonArray([.. ActionId.All.Select(id => (JsonNode)id.Text)]),
        ["executionOrder"] = new JsonArray([.. ActionId.ExecutionOrder.Select(id => (JsonNode)id.Text)]),
    });

    /// <summary>The text <c>contracts/exit-codes.json</c> must hold, from the enum — every value, in numeric order.</summary>
    internal static string ExitCodesText() => Indented(new JsonObject
    {
        ["schemaVersion"] = 1,
        ["description"] = "Every exit code wsl-care returns. Generated from the ExitCode enum by ContractFilesTests.",
        ["codes"] = new JsonArray([.. Enum.GetValues<ExitCode>().OrderBy(c => (int)c).Select(c => (JsonNode)new JsonObject { ["name"] = Camel(c.ToString()), ["code"] = (int)c })]),
    });

    /// <summary>The text <c>contracts/history-reasons.json</c> must hold (plan §15o, coai plan round #1): the reason prefixes of the
    /// history lines a reader must not take for a full check when they carry no <c>kind</c> — from the daemon's own constants
    /// (<see cref="Core.Records.HistoryReasons"/>), so the extension's follower reads ONE list instead of keeping a copy.</summary>
    internal static string HistoryReasonsText() => Indented(new JsonObject
    {
        ["schemaVersion"] = 1,
        ["description"] = "Reason prefixes of history lines that are NOT a full check, for a line that carries no kind (written by a daemon older than kind, or one whose kind cannot be known). A line WITH kind is told by kind alone: kind == \"collect\" is a full check. Generated from the daemon's constants (HistoryReasons) by ContractFilesTests.",
        ["notAFullCheckWithoutKind"] = new JsonArray([.. Core.Records.HistoryReasons.NotAFullCheckWithoutKind.Select(p => (JsonNode)new JsonObject { ["writer"] = p.Writer, ["prefix"] = p.Prefix })]),
    });

    public static IReadOnlyList<(string File, string Text)> Expected =>
        [("actions.json", ActionsText()), ("exit-codes.json", ExitCodesText()), ("history-reasons.json", HistoryReasonsText())];

    /// <summary>Plan §15o, coai plan round #2: <c>collect</c> is the full check's reserved meta name — a request's and
    /// <c>running.json</c>'s marker — so no action id may carry it, in the registry or in the contract the extension reads.</summary>
    [Fact]
    public async Task No_action_id_in_the_registry_or_the_checked_in_contract_is_the_full_check_s_reserved_name()
    {
        var checkedIn = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(ContractsDirectory, "actions.json"), TestContext.Current.CancellationToken))!["ids"]!
            .AsArray().Select(n => (string)n!).ToList();

        checkedIn.Should().NotBeEmpty("the check reads the real contract");
        checkedIn.Concat(ActionId.All.Select(id => id.Text))
            .Should().NotContain(id => string.Equals(id, Core.Records.RunKinds.FullCheckName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The prefixes the contract carries cover the ones the extension's follower was written against on the E6.S3
    /// branch (<c>src_vs_code/src/cleanup/runMatching.ts</c>, <c>NOT_A_FULL_CHECK</c>, origin/feat/wc-e6-cleanup-logs, copied
    /// here as of 2026-10-05) — each begins one of ours, so a follower moving to the contract loses no line it excluded.</summary>
    [Fact]
    public void The_contract_covers_every_prefix_the_follower_was_written_against()
    {
        string[] followerPrefixes =
        [
            "refused: its request could not be used",
            "the run wrote its detail and ended before its history line",
            "the run left a detail that cannot be read",
        ];

        followerPrefixes.Should().OnlyContain(f => Core.Records.HistoryReasons.NotAFullCheckWithoutKind.Any(p => p.Prefix.StartsWith(f, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_checked_in_contracts_equal_what_the_types_enumerate()
    {
        if (Environment.GetEnvironmentVariable(GoldenContracts.WriteVariable) == "1")
        {
            foreach (var (file, text) in Expected)
            {
                await File.WriteAllTextAsync(Path.Combine(ContractsDirectory, file), text, new UTF8Encoding(false), TestContext.Current.CancellationToken);
            }
        }

        foreach (var (file, text) in Expected)
        {
            var path = Path.Combine(ContractsDirectory, file);
            File.Exists(path).Should().BeTrue($"{path} is checked in (regenerate with {GoldenContracts.WriteVariable}=1)");
            var checkedIn = (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).Replace("\r\n", "\n", StringComparison.Ordinal);
            (checkedIn == text).Should().BeTrue($"contracts/{file} must be what the types enumerate — {GoldenContracts.FirstDifference(checkedIn, text)}");
        }
    }

    /// <summary>The companion: the enumeration reaches every value — a contract derived from nothing would pass as well.</summary>
    [Fact]
    public void The_contracts_hold_every_id_and_every_code_including_the_second_switches_and_the_edges()
    {
        var actions = JsonNode.Parse(ActionsText())!;
        var codes = JsonNode.Parse(ExitCodesText())!["codes"]!.AsArray().Select(c => ((string)c!["name"]!, (int)c["code"]!)).ToList();

        actions["ids"]!.AsArray().Select(n => (string)n!).Should().Contain(["A1", "A4", "A5Testcontainers", "A6Unused", "A17"]).And.HaveCount(ActionId.All.Count);
        actions["executionOrder"]!.AsArray().Select(n => (string)n!).Should().StartWith("A5Testcontainers").And.EndWith("A2");
        codes.Should().Contain([("ok", 0), ("usage", 2), ("busy", 75), ("stateUnreadable", 79), ("interrupted", 130)]).And.HaveCount(Enum.GetValues<ExitCode>().Length);
    }

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    private static string Indented(JsonNode node) =>
        node.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
}
