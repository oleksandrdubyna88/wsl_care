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
            .And.Contain("\"reason\": \"/golden-root/home/user/.npm is missing; measured by the full run 20000101T000000Z-1\"")
            .And.Contain("\"productVersion\": \"unknown\"")
            .And.EndWith("}\n").And.NotContain("\r");
        normalised.Should().NotContain("2026-10-03").And.NotContain("scn-golden").And.NotContain("7aeb02d").And.NotContain("4242");
        matched.Should().Contain(["**.sampledAt", "**.runId", "**.ageSeconds", "**.ageSeconds.value", "productVersion", "vm.disk.path", "id:disk.root", "runIdInText"]);
    }

    /// <summary>E5 code round #1: a carried verdict whose figure moves with the clock is fixed to a CONCRETE value — the
    /// one it had at the capture — never to placeholder prose a client would render as a figure.</summary>
    [Fact]
    public void The_journal_and_clock_verdicts_are_fixed_to_their_values_at_the_capture_never_to_placeholder_prose()
    {
        const string answer = """
            {"verdicts":[{"id":"journal.history","level":"ok","value":"9.4 days (oldest entry 2026-10-01 22:10Z)","limit":"l","reason":"r"},
                         {"id":"clock.drift","level":"ok","value":"-170612.34 s (launch latency 0.75 s subtracted)","limit":"l","reason":"one observation above the threshold"}]}
            """;

        var normalised = GoldenContracts.Normalise(answer, "/tmp/none", new HashSet<string>(StringComparer.Ordinal));

        normalised.Should().Contain("\"value\": \"0.8 days (oldest entry 2026-10-01 22:10Z)\"", "the health capture (2026-10-02T17:34:47Z) minus the oldest entry")
            .And.Contain("\"value\": \"+0.19 s (launch latency 0.75 s subtracted)\"", "the clock probe's process start minus the capture's instant")
            .And.Contain("\"reason\": \"the distro's clock agrees with Windows'\"")
            .And.NotContain("<").And.NotContain("170612");
    }

    /// <summary>Part 4 of the list — identity (E5 code round, privacy): the SAME rules the captured fixtures were anonymised
    /// with reach every string of an answer, after the sandbox root is rewritten.</summary>
    [Fact]
    public void Every_string_of_an_answer_passes_through_the_identity_list_the_fixtures_were_anonymised_with()
    {
        const string answer = """
            {"top":[{"user":"user","commandLine":"/home/alice/.vscode-server/bin/x/node /mnt/c/Users/Alice/AppData/Local/Temp/claude/p/0f/scratchpad/run.sh mail a.b@example.org",
                     "cwd":{"available":true,"value":"/tmp/scn/root/home/me/git"}}]}
            """;

        var normalised = GoldenContracts.Normalise(answer, "/tmp/scn/root", new HashSet<string>(StringComparer.Ordinal));

        normalised.Should().Contain("\"commandLine\": \"/home/user/.vscode-server/bin/x/node /tmp/x/run.sh mail user@example.invalid\"")
            .And.Contain("\"value\": \"/golden-root/home/user/git\"", "the sandbox's own home is a /home/<name> like any other");
        GoldenContracts.IdentityRules.Should().BeSameAs(WslCare.TestSupport.FixtureIdentity.Rules, "one list, not a copy of it");
    }

    [Fact]
    public void The_first_difference_names_the_line_and_both_texts()
    {
        GoldenContracts.FirstDifference("a\nb\nc\n", "a\nB\nc\n").Should().Be("line 2: checked in 'b', the CLI answers 'B'");
        GoldenContracts.FirstDifference("a\n", "a\n").Should().Be("no difference");
        GoldenContracts.FirstDifference("a", "a\nb").Should().Be("line 2: checked in '<end>', the CLI answers 'b'");
    }

    /// <summary>E6.S2 (plan §15k #11): the checked-in A4 previews hold the shown-list rule — <c>shown.length == min(count,
    /// 10 000)</c>, and <c>shownTruncated: true</c> exactly when the cap cut the list (absent otherwise). Read from the files
    /// the extension's host reads, on every OS.</summary>
    [Theory]
    [InlineData("act-a4-preview.json", 387, false)]
    [InlineData("act-a4-preview-capped.json", 10_001, true)]
    public void The_A4_previews_hold_the_shown_list_rule_below_and_past_the_cap(string file, int count, bool truncated)
    {
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(GoldenContracts.HeadDirectory, file)));
        var a4 = json.RootElement.GetProperty("actions").EnumerateArray().Single(a => a.GetProperty("id").GetString() == "A4");

        a4.GetProperty("preview").GetProperty("count").GetInt32().Should().Be(count);
        a4.GetProperty("shown").GetArrayLength().Should().Be(Math.Min(count, Core.Actions.ShownList.MaxNames));
        a4.TryGetProperty("shownTruncated", out var flag).Should().Be(truncated, "the flag is present only when the cap cut the list");
        if (truncated)
        {
            flag.GetBoolean().Should().BeTrue();
        }
    }
}
