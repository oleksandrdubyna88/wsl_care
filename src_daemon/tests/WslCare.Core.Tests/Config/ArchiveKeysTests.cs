using System.Text;

using FluentAssertions;

using WslCare.Core.Agents;
using WslCare.Core.Config;
using WslCare.Core.Files;

namespace WslCare.Core.Tests.Config;

/// <summary>
/// Plan §15r E9.S0 — the archive's keys: every number a key (the owner's rule, 2026-10-05), the coupled rules that keep the
/// archive ahead of the agents' own deletion, <c>archive.agents</c> a closed list, and <c>archive.baseFolder</c> an ordinary key
/// once root never writes it (§15r D1, D7).
/// </summary>
public sealed class ArchiveKeysTests
{
    private static readonly ConfigLayerFile Machine = new(ConfigLayer.Machine, "/etc/wsl-care/config.json");
    private static readonly ConfigLayerFile User = new(ConfigLayer.User, "/home/me/.config/wsl-care/config.json");

    private static (ConfigLayerFile, FileReadResult) Defaults() => (ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults()));

    private static (ConfigLayerFile, FileReadResult) Layer(ConfigLayerFile file, string json) => (file, new FileReadResult.Content(Encoding.UTF8.GetBytes(json)));

    private static ConfigLoadResult Load(string machine, string user = "{}") => ConfigLoader.Load([Defaults(), Layer(Machine, machine), Layer(User, user)]);

    private static ConfigKey.IntKey Int(string name) => (ConfigKey.IntKey)ConfigKeys.Find(name)!;

    [Fact]
    public void The_defaults_copy_from_day_14_remove_from_day_15_and_keep_15_days_before_claudes_own_sweep()
    {
        var config = Load("{}").Config;

        config.Int(Int("archive.olderThanDays")).Should().Be(14);
        config.Int(Int("archive.removeAfterHours")).Should().Be(24);
        config.Int(Int("archive.marginDays")).Should().Be(7);
        config.Int(Int("archive.agentRetentionDays")).Should().Be(30);
        config.Int(Int("archive.urgentWithinDays")).Should().Be(7);
    }

    /// <summary>§15r D10: ⌈removeAfterHours / 24⌉ + olderThanDays + marginDays ≤ agentRetentionDays — 1 + 14 + 7 ≤ 30 by default; a
    /// machine layer archiving at day 25 would let Claude's 30-day sweep win.</summary>
    [Theory]
    [InlineData("""{ "archive": { "olderThanDays": 25 } }""", "archive.olderThanDays")]
    [InlineData("""{ "archive": { "removeAfterHours": 240 } }""", "archive.olderThanDays")]
    [InlineData("""{ "archive": { "agentRetentionDays": 20 } }""", "archive.olderThanDays")]
    [InlineData("""{ "archive": { "urgentWithinDays": 8 } }""", "archive.urgentWithinDays")]
    [InlineData("""{ "running": { "noProgressMinutes": 5 }, "archive": { "progressSilenceSeconds": 600 } }""", "archive.progressSilenceSeconds")]
    [InlineData("""{ "commands": { "maxTimeoutHours": 1 }, "archive": { "runBudgetMinutes": 55, "finishGraceMinutes": 10 } }""", "archive.runBudgetMinutes")]
    [InlineData("""{ "archive": { "maxSessionsPerRun": 100000 } }""", "archive.maxStateFileBytes")]
    public void An_archive_rule_a_machine_layer_breaks_refuses_the_layer_naming_it(string machine, string named)
    {
        var result = Load(machine);

        result.IsObserveOnly.Should().BeTrue("a contradiction between two limits is a configuration error");
        result.Errors.Should().Contain(e => e.Message.Contains(named, StringComparison.Ordinal));
    }

    [Fact]
    public void A_user_layer_that_would_archive_after_claudes_sweep_is_a_notice_and_the_default_stays()
    {
        var result = Load("{}", """{ "archive": { "olderThanDays": 25 } }""");

        result.IsObserveOnly.Should().BeFalse("a user value in its range never puts root observe-only");
        result.Notices.Should().Contain(n => n.Key == "archive.olderThanDays");
        result.Config.Int(Int("archive.olderThanDays")).Should().Be(14);
    }

    [Fact]
    public void A_user_who_raised_the_agents_retention_may_archive_later()
    {
        var result = Load("{}", """{ "archive": { "agentRetentionDays": 3650, "olderThanDays": 60 } }""");

        result.Notices.Should().BeEmpty();
        result.Config.Int(Int("archive.olderThanDays")).Should().Be(60);
    }

    [Fact]
    public void The_archived_agents_are_a_closed_list_of_the_agents_with_an_archive_block()
    {
        var key = (ConfigKey.TextListKey)ConfigKeys.Find("archive.agents")!;

        key.Allowed.Should().Equal(AgentCatalogue.ArchivableIds);
        Load("{}").Config.TextList(key).Should().Equal("claude-code", "codex", "gemini-cli", "antigravity");
        ConfigValidation.Parse(key, "codex,claude-code").Should().BeOfType<ValueCheck.Ok>();
        ConfigValidation.Parse(key, "copilot-cli").Should().BeOfType<ValueCheck.Invalid>()
            .Which.Message.Should().Contain("copilot-cli", "an agent whose layout nobody confirmed is never archived");
        key.Trust.Safe.Should().Be(SafeDirection.Subset, "fewer archived agents is the safe direction");
    }

    /// <summary>§15r D1: root never writes the base — the user's own process moves — so the user layer may name it; the base rules
    /// (D7) are the user process's, at config set and at every run.</summary>
    [Fact]
    public void A_base_folder_set_in_the_user_layer_is_accepted_once_root_never_writes_it()
    {
        var result = Load("{}", """{ "archive": { "baseFolder": "/mnt/v/ai-archive" } }""");

        result.Notices.Should().BeEmpty();
        result.Config.Text(ConfigKeys.Archive.BaseFolder).Should().Be("/mnt/v/ai-archive");
        ConfigKeys.Archive.BaseFolder.Trust.MachineOnly.Should().BeFalse();
    }

    [Theory]
    [InlineData(@"\\nas\share\ai-archive")]
    [InlineData(@"V:\ai-archive")]
    [InlineData("/mnt/v/ai-archive")]
    public void A_base_folder_may_be_a_linux_path_a_drive_path_or_a_unc_share(string path)
    {
        ConfigValidation.Parse(ConfigKeys.Archive.BaseFolder, path).Should().BeOfType<ValueCheck.Ok>();
    }

    [Theory]
    [InlineData(@"\\?\C:\x")]
    [InlineData(@"\\.\pipe\x")]
    [InlineData(@"\\nas")]
    [InlineData(@"\\nas\share\..\x")]
    public void A_device_path_or_a_share_without_a_folder_is_not_a_base(string path)
    {
        ConfigValidation.Parse(ConfigKeys.Archive.BaseFolder, path).Should().BeOfType<ValueCheck.Invalid>();
    }
}
