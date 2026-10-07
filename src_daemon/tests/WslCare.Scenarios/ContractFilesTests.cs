using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Actions;
using WslCare.Core.Agents;
using WslCare.Core.Config;
using WslCare.Core.Files;

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
        ["description"] = "Every action id the daemon knows (its auto.* switch names; A5Testcontainers and A6Unused are ids; A18 is a button only, with no auto switch) and the order a run takes them in. Generated from ActionId.All / ActionId.ExecutionOrder by ContractFilesTests.",
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

    /// <summary>The text <c>contracts/config-keys.json</c> must hold (plan §15q D5, R1): every configuration key with its shape,
    /// range or allowed values, its default from the embedded <c>default.json</c>, and what it means to a root run — the safe
    /// direction the loader applies and the extension's loosening modal keys on — from <see cref="ConfigKeys.All"/>.</summary>
    internal static string ConfigKeysText()
    {
        var defaults = ConfigLoader.Load([(ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults()))]).Config;
        return Indented(new JsonObject
        {
            ["schemaVersion"] = 1,
            ["description"] = "Every configuration key the daemon knows: its shape, its bounds or allowed values, its default, and what it means to a root run (safeDirection: the direction of change a root run trusts from another account's layer; rootEffective; tightenOnlyForRoot; machineOnly; daemonUnused; zeroIsUnbounded). Generated from ConfigKeys and the embedded default.json by ContractFilesTests.",
            ["keys"] = new JsonArray([.. ConfigKeys.All.Select(k => KeyNode(k, defaults.Entry(k).Value))]),
        });
    }

    /// <summary>The text <c>contracts/status-limits.json</c> must hold (E7.S2c): the fields of <c>status --json</c>'s <c>limits</c>
    /// object — the daemon values the extension mirrors instead of copying — from <see cref="Core.Status.StatusLimits.Fields"/>,
    /// each with the key it publishes, its unit, its range and its default. The extension's reader is tested against this file.</summary>
    internal static string StatusLimitsText()
    {
        var defaults = ConfigLoader.Load([(ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults()))]).Config;
        return Indented(new JsonObject
        {
            ["schemaVersion"] = 1,
            ["description"] = "The fields of status --json's limits object (additive, E7.S2c): daemon values the extension MIRRORS instead of copying, each the value in force, a whole number. An absent limits object or field means a daemon older than E7.S2c: the reader takes its fallback (the default here). Generated from StatusLimits.Fields and the embedded default.json by ContractFilesTests.",
            ["object"] = "limits",
            ["fields"] = new JsonArray([.. Core.Status.StatusLimits.Fields.Select(f => (JsonNode)new JsonObject
            {
                ["name"] = f.Name,
                ["key"] = f.Key.Name,
                ["unit"] = f.Unit,
                ["min"] = f.Key.Min,
                ["max"] = f.Key.Max,
                ["default"] = defaults.Int(f.Key),
            })]),
        });
    }

    private static JsonNode KeyNode(ConfigKey key, ConfigValue fallback)
    {
        var node = new JsonObject { ["name"] = key.Name };
        foreach (var (name, value) in Shape(key))
        {
            node[name] = value;
        }

        node["default"] = JsonNode.Parse(fallback.ToJsonElement().GetRawText());
        node["safeDirection"] = Camel(key.Trust.Safe.ToString());
        node["rootEffective"] = key.Trust.RootEffective;
        node["tightenOnlyForRoot"] = key.Trust.TightenOnlyForRoot;
        node["machineOnly"] = key.Trust.MachineOnly;
        node["daemonUnused"] = key.Trust.DaemonUnused;
        node["zeroIsUnbounded"] = key.Trust.ZeroIsUnbounded;
        return node;
    }

    private static IReadOnlyList<(string Name, JsonNode? Value)> Shape(ConfigKey key) => key switch
    {
        ConfigKey.BoolKey => [("shape", "bool")],
        ConfigKey.IntKey number => [("shape", "int"), ("min", number.Min), ("max", number.Max)],
        ConfigKey.TextKey { Rule: TextRule.OneOf one } => [("shape", "text"), ("oneOf", new JsonArray([.. one.Values.Select(v => (JsonNode)v)]))],
        ConfigKey.TextKey { Rule: TextRule.Matching matching } => [("shape", "text"), ("pattern", matching.Expression)],
        ConfigKey.TextKey { Rule: TextRule.AbsolutePathOrEmpty } => [("shape", "path"), ("maxLength", TextRule.AbsolutePathOrEmpty.MaxLength)],
        ConfigKey.TextListKey list => [("shape", "textList"), ("allowed", new JsonArray([.. list.Allowed.Select(v => (JsonNode)v)]))],
        ConfigKey.AgentListKey => [("shape", "agentList"), ("maxEntries", ExtraAgentShape.MaxEntries), ("maxFolders", ExtraAgentShape.MaxFolders), ("maxPathLength", ExtraAgentShape.MaxPathLength), ("maxGlobLength", ExtraAgentShape.MaxGlobLength), ("maxNameLength", ExtraAgentShape.MaxNameLength), ("sides", new JsonArray(ExtraAgentShape.Wsl, ExtraAgentShape.Windows))],
        _ => throw new InvalidOperationException($"{key.Name}: a key shape the contract does not describe"),
    };

    public static IReadOnlyList<(string File, string Text)> Expected =>
        [("actions.json", ActionsText()), ("exit-codes.json", ExitCodesText()), ("history-reasons.json", HistoryReasonsText()), ("config-keys.json", ConfigKeysText()), ("status-limits.json", StatusLimitsText())];

    /// <summary>The companion of the config-keys contract: it carries every key, the closed families list and the trust of the
    /// keys R1 is about — a contract derived from nothing would pass the equality test as well.</summary>
    [Fact]
    public void The_config_keys_contract_holds_every_key_with_its_trust()
    {
        var keys = JsonNode.Parse(ConfigKeysText())!["keys"]!.AsArray().ToDictionary(k => (string)k!["name"]!, k => k!);

        keys.Should().HaveCount(ConfigKeys.All.Count);
        keys["dryRun"]["safeDirection"]!.GetValue<string>().Should().Be("on");
        keys["auto.A5"]["safeDirection"]!.GetValue<string>().Should().Be("off");
        keys["containers.stoppedOlderThanDays"]["min"]!.GetValue<int>().Should().Be(0);
        keys["processes.families"]["allowed"]!.AsArray().Select(n => (string)n!).Should().NotContain(["other", "ai-agents"]).And.Contain("testhost");
        keys["archive.baseFolder"]["machineOnly"]!.GetValue<bool>().Should().BeTrue();
        keys["logging.retentionDays"]["zeroIsUnbounded"]!.GetValue<bool>().Should().BeTrue();
        keys["distro"]["daemonUnused"]!.GetValue<bool>().Should().BeTrue();
        keys["aiAgents.warnGb"]["rootEffective"]!.GetValue<bool>().Should().BeFalse();
    }

    /// <summary>E7.S2c: the two field names the extension's <c>shared/daemonLimits.ts</c> reads (PR #12) are in the contract, and
    /// the daemon's JSON writer spells every contract field exactly as the contract does — a reader and a writer held equal.</summary>
    [Fact]
    public void The_status_limits_contract_carries_the_names_the_extension_reads_and_the_writer_spells_them()
    {
        var fields = JsonNode.Parse(StatusLimitsText())!["fields"]!.AsArray().Select(f => (string)f!["name"]!).ToList();
        var written = JsonNode.Parse(JsonSerializer.Serialize(
            Core.Status.StatusLimits.From(ConfigLoader.Load([(ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults()))]).Config),
            Core.Json.WslCareJsonContext.Default.StatusLimits))!.AsObject();

        fields.Should().Contain(["historyRetentionDays", "requestFutureSkewSeconds"], "the names PR #12's daemonLimits.ts reads");
        written.Select(p => p.Key).Should().Equal(fields, "the writer spells each field as the contract does, in its order");
        written["historyRetentionDays"]!.GetValue<int>().Should().Be(90);
        written["requestFutureSkewSeconds"]!.GetValue<int>().Should().Be(300);
    }

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

    /// <summary>§15o review O4: the reason texts the contract hands out are ALREADY ON DISK — every history line written with them
    /// stays for 90 days — so they are frozen here as literals, never read back from the constants they guard. Changing one is
    /// a contract break (old lines keep the old words): add a contract entry for the new text, never edit these.</summary>
    [Fact]
    public void The_reasons_already_on_disk_are_frozen()
    {
        const string Frozen = "these strings are on disk; a change is a contract break";

        Core.Records.HistoryReasons.UnusableRequestPrefix.Should().Be("refused: its request could not be used", Frozen);
        Core.Records.RunReconcile.InterruptedReason.Should().Be("the run wrote its detail and ended before its history line (found by the next run's reconcile)", Frozen);
        Core.Records.RunReconcile.UnreadableDetailReason.Should().Be("the run left a detail that cannot be read; its start is the second its id names", Frozen);
        Core.Records.HistoryReasons.KindNotKnownPrefix.Should().Be("its kind is not known", Frozen);
        Core.Records.HistoryReasons.NotAFullCheckWithoutKind.Select(p => p.Prefix).Should().Equal(
            ["refused: its request could not be used", "the run wrote its detail and ended before its history line (found by the next run's reconcile)", "the run left a detail that cannot be read; its start is the second its id names", "its kind is not known"],
            Frozen);
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
