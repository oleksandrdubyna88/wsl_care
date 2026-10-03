using FluentAssertions;

namespace WslCare.Scenarios;

/// <summary>
/// The golden JSON the extension's client tests replay (plan §15f #10, §15g m7) is the BUILT CLI's answer at THIS commit:
/// a renamed field, a changed figure, a new member — any of them makes the checked-in <c>contracts/golden/head/*.json</c>
/// stale, and this fails naming the file and the first differing line. Regenerate with
/// <c>WSL_CARE_WRITE_GOLDENS=1 ./WslCare.Scenarios --filter-class "*GoldenContractTests"</c> on Linux (WSL), then review
/// the diff like any other change of the contract.
/// </summary>
public sealed class GoldenContractTests
{
    private const string LinuxOnly = "the goldens are the Linux binary's answers over the captured procfs tree; the Windows binary answers for the host side";

    [Fact]
    public async Task The_checked_in_goldens_are_what_the_built_cli_answers_at_this_commit_and_every_normalisation_rule_still_matches()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);
        var (files, matched) = await GoldenContracts.ProduceAsync();

        if (Environment.GetEnvironmentVariable(GoldenContracts.WriteVariable) == "1")
        {
            Directory.CreateDirectory(GoldenContracts.HeadDirectory);
            foreach (var (file, text) in files)
            {
                await File.WriteAllTextAsync(Path.Combine(GoldenContracts.HeadDirectory, file), text, TestContext.Current.CancellationToken);
            }
        }

        foreach (var (file, text) in files)
        {
            var path = Path.Combine(GoldenContracts.HeadDirectory, file);
            File.Exists(path).Should().BeTrue($"{path} is checked in (regenerate with {GoldenContracts.WriteVariable}=1)");
            var checkedIn = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            (checkedIn == text).Should().BeTrue(
                $"contracts/golden/head/{file} must be what the CLI answers at this commit — {GoldenContracts.FirstDifference(checkedIn, text)}; regenerate with {GoldenContracts.WriteVariable}=1 and review the diff");
        }

        // The companion: a rule that matches nothing is a rule nobody can review against the answers any more.
        matched.Should().Contain(GoldenContracts.AllRuleNames, "every rule of the normalisation list still names a value the CLI answers");
    }

    [Fact]
    public void The_normaliser_replaces_what_its_rules_name_keeps_types_and_leaves_every_other_value_alone()
    {
        const string sandbox = "/tmp/scn-golden-abc/root";
        const string answer = """
            {"schemaVersion":1,"sampledAt":"2026-10-03T19:19:05.29+00:00","sampleMilliseconds":22,
             "vm":{"disk":{"available":true,"path":"/tmp/scn-golden-abc/root","totalBytes":5,"usedBytes":4,"availableBytes":1,"usedPercent":80.0},
                   "processes":{"top":[{"pid":5949,"ageSeconds":{"available":true,"value":81234}}]}},
             "slow":{"containerStats":{"available":true,"runId":"20261003T191900Z-4242","ageSeconds":17}},
             "verdicts":[{"id":"disk.root","level":"warn","value":"80.0 %","limit":"warn > 80 %","reason":"df /"},
                         {"id":"memory.available","level":"ok","value":"66.8 %","limit":"l","reason":"r","basis":{"source":"sample","evaluatedAt":"2026-10-03T19:19:05+00:00","ageSeconds":0}}],
             "folders":{"reason":"/tmp/scn-golden-abc/root/home/u/.npm is missing; measured by the full run 20261003T191900Z-4242"},
             "productVersion":"0.1.0+7aeb02dd39a9841915112456532d3803e4cb6026"}
            """;
        var matched = new HashSet<string>(StringComparer.Ordinal);

        var normalised = GoldenContracts.Normalise(answer, sandbox, matched);

        normalised.Should().Contain("\"sampledAt\": \"2000-01-01T00:00:00+00:00\"")
            .And.Contain("\"sampleMilliseconds\": 0")
            .And.Contain("\"path\": \"/golden-root\"")
            .And.Contain("\"usedPercent\": 13")
            .And.Contain("\"pid\": 5949", "a pid from the captured tree is a fixture value, not a volatile one")
            .And.Contain("\"value\": 0")
            .And.Contain("\"runId\": \"20000101T000000Z-1\"")
            .And.Contain("\"ageSeconds\": 0")
            .And.Contain("\"level\": \"ok\",\n      \"value\": \"13.0 %\"")
            .And.Contain("\"value\": \"66.8 %\"", "a verdict the runner does not decide is kept")
            .And.Contain("\"evaluatedAt\": \"2000-01-01T00:00:00+00:00\"")
            .And.Contain("\"reason\": \"/golden-root/home/u/.npm is missing; measured by the full run 20000101T000000Z-1\"")
            .And.Contain("\"productVersion\": \"unknown\"")
            .And.EndWith("}\n").And.NotContain("\r");
        normalised.Should().NotContain("2026-10-03").And.NotContain("scn-golden").And.NotContain("7aeb02d").And.NotContain("4242");
        matched.Should().Contain(["**.sampledAt", "**.runId", "**.ageSeconds", "**.ageSeconds.value", "productVersion", "vm.disk.path", "id:disk.root", "runIdInText"]);
    }

    [Fact]
    public void The_first_difference_names_the_line_and_both_texts()
    {
        GoldenContracts.FirstDifference("a\nb\nc\n", "a\nB\nc\n").Should().Be("line 2: checked in 'b', the CLI answers 'B'");
        GoldenContracts.FirstDifference("a\n", "a\n").Should().Be("no difference");
        GoldenContracts.FirstDifference("a", "a\nb").Should().Be("line 2: checked in '<end>', the CLI answers 'b'");
    }
}
