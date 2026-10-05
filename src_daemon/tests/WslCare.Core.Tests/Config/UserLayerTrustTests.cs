using System.Text;

using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Config;

/// <summary>
/// Plan §15q R1.2, R1.3, R1.6: how far a run trusts the user layer it reads. Its own layer: fully. Another account's layer read
/// by ROOT: fully while WSL interop gives that account root anyway — except root's audit-log keys, which only tighten — and,
/// without interop, only in each root-effective key's safe direction. A value not taken is a notice, never an error.
/// </summary>
public sealed class UserLayerTrustTests
{
    private static readonly ConfigLayerFile Machine = new(ConfigLayer.Machine, "/etc/wsl-care/config.json");
    private static readonly ConfigLayerFile User = new(ConfigLayer.User, "/home/me/.config/wsl-care/config.json");

    private static readonly UserLayerTrust RootWithInterop = new(1000, ForRoot: true, LoosenRefused: string.Empty, Skipped: string.Empty);

    private static readonly UserLayerTrust RootWithoutInterop = new(1000, ForRoot: true, LoosenRefused: "WSL interop is not registered", Skipped: string.Empty);

    private static (ConfigLayerFile, FileReadResult) Defaults() => (ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults()));

    private static (ConfigLayerFile, FileReadResult) Layer(ConfigLayerFile file, string json) => (file, new FileReadResult.Content(Encoding.UTF8.GetBytes(json)));

    private const string Loosening = """
        { "dryRun": false, "auto": { "A5": true }, "containers": { "stoppedOlderThanDays": 0 }, "processes": { "families": ["node"] },
          "thresholds": { "memAvailableActPercent": 50 } }
        """;

    private const string Tightening = """
        { "auto": { "A1": false }, "containers": { "stoppedOlderThanDays": 30 }, "processes": { "families": ["testhost"] },
          "thresholds": { "memAvailableActPercent": 5 }, "aiAgents": { "warnGb": 1 } }
        """;

    [Fact]
    public void With_interop_disabled_a_user_value_can_only_tighten_root()
    {
        var result = ConfigLoader.Load([Defaults(), (Machine, new FileReadResult.Missing()), Layer(User, Loosening)], RootWithoutInterop);

        result.IsObserveOnly.Should().BeFalse("a value root does not take is a notice, not an error that stops every action");
        result.Config.Bool(ConfigKeys.DryRun).Should().BeTrue();
        result.Config.Bool(ConfigKeys.Auto.A5).Should().BeFalse();
        result.Config.Int(ConfigKeys.Containers.StoppedOlderThanDays).Should().Be(7);
        result.Config.TextList(ConfigKeys.Processes.Families).Should().Equal("dotnet-build-servers", "testhost");
        result.Config.Int(ConfigKeys.Thresholds.MemAvailableActPercent).Should().Be(15);
        result.Notices.Select(n => n.Key).Should().BeEquivalentTo(
            ["dryRun", "auto.A5", "containers.stoppedOlderThanDays", "processes.families", "thresholds.memAvailableActPercent"]);
        result.Notices.Should().OnlyContain(n => n.Message.Contains("WSL interop is not registered") && n.Message.Contains("is ignored"));
    }

    [Fact]
    public void With_interop_disabled_a_tightening_user_value_is_taken_and_a_display_value_moves_freely()
    {
        var result = ConfigLoader.Load([Defaults(), (Machine, new FileReadResult.Missing()), Layer(User, Tightening)], RootWithoutInterop);

        result.Notices.Should().BeEmpty();
        result.Config.Bool(ConfigKeys.Auto.A1).Should().BeFalse();
        result.Config.Int(ConfigKeys.Containers.StoppedOlderThanDays).Should().Be(30);
        result.Config.TextList(ConfigKeys.Processes.Families).Should().Equal("testhost");
        result.Config.Int(ConfigKeys.Thresholds.MemAvailableActPercent).Should().Be(5);
        result.Config.Int(ConfigKeys.AiAgents.WarnGb).Should().Be(1);
        result.Config.Entry(ConfigKeys.Containers.StoppedOlderThanDays).Layer.Should().Be(ConfigLayer.User);
    }

    [Fact]
    public void The_safe_direction_is_judged_against_the_machine_layer_not_only_the_default()
    {
        var result = ConfigLoader.Load(
            [Defaults(), Layer(Machine, """{ "containers": { "stoppedOlderThanDays": 30 } }"""), Layer(User, """{ "containers": { "stoppedOlderThanDays": 14 } }""")],
            RootWithoutInterop);

        result.Config.Int(ConfigKeys.Containers.StoppedOlderThanDays).Should().Be(30, "14 is tighter than the default but looser than the machine's 30");
        result.Notices.Should().ContainSingle().Which.Key.Should().Be("containers.stoppedOlderThanDays");
    }

    [Fact]
    public void With_interop_the_target_users_layer_steers_root_but_not_roots_own_audit_log()
    {
        var result = ConfigLoader.Load(
            [Defaults(), (Machine, new FileReadResult.Missing()), Layer(User, """{ "dryRun": false, "logging": { "minimumLevel": "Error", "retentionDays": 1 } }""")],
            RootWithInterop);

        result.Config.Bool(ConfigKeys.DryRun).Should().BeFalse("with interop the target user can be root anyway: the layer is their intent");
        result.Config.Text(ConfigKeys.Logging.MinimumLevel).Should().Be("Information");
        result.Config.Int(ConfigKeys.Logging.RetentionDays).Should().Be(14);
        result.Notices.Select(n => n.Key).Should().BeEquivalentTo(["logging.minimumLevel", "logging.retentionDays"]);
    }

    [Fact]
    public void Roots_audit_log_may_be_made_more_verbose_or_kept_longer_and_zero_retention_is_the_longest()
    {
        var result = ConfigLoader.Load(
            [Defaults(), (Machine, new FileReadResult.Missing()), Layer(User, """{ "logging": { "minimumLevel": "Debug", "retentionDays": 0 } }""")],
            RootWithInterop);

        result.Notices.Should().BeEmpty();
        result.Config.Text(ConfigKeys.Logging.MinimumLevel).Should().Be("Debug");
        result.Config.Int(ConfigKeys.Logging.RetentionDays).Should().Be(0, "0 disables the sweep: kept for ever");
    }

    [Fact]
    public void A_users_own_run_takes_its_whole_layer_logging_included()
    {
        var result = ConfigLoader.Load([Defaults(), (Machine, new FileReadResult.Missing()), Layer(User, """{ "dryRun": false, "logging": { "minimumLevel": "Error" } }""")]);

        result.Notices.Should().BeEmpty();
        result.Config.Text(ConfigKeys.Logging.MinimumLevel).Should().Be("Error");
        result.Config.Bool(ConfigKeys.DryRun).Should().BeFalse();
    }

    [Fact]
    public void A_machine_only_key_is_taken_from_the_machine_layer_and_ignored_with_a_notice_from_the_user_layer()
    {
        var fromUser = ConfigLoader.Load([Defaults(), (Machine, new FileReadResult.Missing()), Layer(User, """{ "archive": { "baseFolder": "/srv/archive" } }""")]);
        var fromMachine = ConfigLoader.Load([Defaults(), Layer(Machine, """{ "archive": { "baseFolder": "/srv/archive" } }"""), (User, new FileReadResult.Missing())]);

        fromUser.Config.Text(ConfigKeys.Archive.BaseFolder).Should().BeEmpty();
        fromUser.IsObserveOnly.Should().BeFalse();
        fromUser.Notices.Should().ContainSingle().Which.Message.Should().Contain("only in the machine layer");
        fromMachine.Config.Text(ConfigKeys.Archive.BaseFolder).Should().Be("/srv/archive");
        fromMachine.Notices.Should().BeEmpty();
    }

    [Fact]
    public void Root_reading_the_target_users_layer_requires_that_user_to_own_it()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "file owners are Linux's: run in WSL or on the Linux legs");
        using var root = new TempRoot("user-layer-owner");
        var paths = ProcfsFixture.PathsAt(root.Path);
        var files = new PhysicalFileSystem(paths); // owners checked as named, as on a machine
        var layer = paths.UserConfigFile;
        Directory.CreateDirectory(Path.GetDirectoryName(layer)!);
        File.WriteAllText(layer, """{ "dryRun": false }""");
        var someoneElse = RegularFiles.EffectiveUid() + 4242;

        var result = ConfigLoader.Load(paths, files, new UserLayerTrust(someoneElse, ForRoot: true, string.Empty, string.Empty));

        result.Should().BeOfType<ConfigLoadResult.ObserveOnly>();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain($"not uid {someoneElse}");
        result.Config.Bool(ConfigKeys.DryRun).Should().BeTrue();
    }

    [Fact]
    public void The_trust_follows_whose_home_the_paths_follow()
    {
        var target = new HomeOwner.Target(new TargetUser("me", 1000, "/home/me"), "test");

        UserLayerTrusts.For(target, static () => string.Empty).Should().Be(RootWithInterop);
        UserLayerTrusts.For(target, static () => "WSL interop is not registered").LoosenRefused.Should().Contain("WSL interop is not registered").And.Contain("only tighten");
        UserLayerTrusts.For(new HomeOwner.Unknown("two accounts"), static () => string.Empty).Skipped.Should().Contain("two accounts");
        UserLayerTrusts.For(new HomeOwner.ThisProcess("not root"), () => throw new InvalidOperationException("an unprivileged run never asks")).Should().Be(UserLayerTrust.OwnLayer());
    }

    [Fact]
    public void The_digest_names_the_user_layer_as_read_and_is_empty_without_one()
    {
        using var sandbox = new LinuxSandbox("user-layer-digest");
        ConfigLoader.Load(sandbox.Paths, sandbox.Files).UserLayerDigest.Should().BeEmpty();

        sandbox.Write("/home/me/.config/wsl-care/config.json", "{}");

        ConfigLoader.Load(sandbox.Paths, sandbox.Files).UserLayerDigest.Should().Be("44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a", "the SHA-256 of {}");
    }
}
