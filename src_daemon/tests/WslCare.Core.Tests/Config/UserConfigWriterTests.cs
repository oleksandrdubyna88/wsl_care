using FluentAssertions;

using WslCare.Core.Actions.Engine;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Config;

/// <summary>
/// <c>config set</c> / <c>config reset</c> write ONLY the user layer, atomically, and repair a broken
/// one on the way (plan §15a #1).
/// </summary>
public sealed class UserConfigWriterTests
{
    private static UserConfigWriter Writer(SandboxHost host) => new(host.Paths, host.Files, new FixedTimeProvider());

    [Fact]
    public void Set_creates_the_user_file_with_the_key_nested_and_the_loader_reads_it_back_from_the_user_layer()
    {
        using var host = new SandboxHost("writer-set");

        var result = Writer(host).Set(ConfigKeys.Volumes.AnonymousMaxGb, new ConfigValue.Int(25));

        var written = result.Should().BeOfType<UserConfigWriteResult.Written>().Subject;
        written.File.Should().Be(host.Paths.UserConfigFile);
        written.KeyWasPresent.Should().BeFalse();
        host.ReadUserConfig().Should().Contain("\"volumes\"").And.Contain("\"anonymousMaxGb\": 25");
        var loaded = ConfigLoader.Load(host.Paths, host.Files);
        loaded.Should().BeOfType<ConfigLoadResult.Valid>();
        loaded.Config.Entry(ConfigKeys.Volumes.AnonymousMaxGb).Should().Be(new ConfigEntry(ConfigKeys.Volumes.AnonymousMaxGb, new ConfigValue.Int(25), ConfigLayer.User));
    }

    [Fact]
    public void Set_keeps_the_other_valid_keys_and_overwrites_the_same_key()
    {
        using var host = new SandboxHost("writer-keep");
        host.WriteUserConfig("""{ "dryRun": false, "volumes": { "anonymousMaxGb": 25 } }""");

        var result = Writer(host).Set(ConfigKeys.Volumes.AnonymousMaxGb, new ConfigValue.Int(30));

        result.Should().BeOfType<UserConfigWriteResult.Written>().Which.KeyWasPresent.Should().BeTrue();
        var config = ConfigLoader.Load(host.Paths, host.Files).Config;
        config.Bool(ConfigKeys.DryRun).Should().BeFalse();
        config.Int(ConfigKeys.Volumes.AnonymousMaxGb).Should().Be(30);
    }

    [Fact]
    public void Set_on_a_file_with_invalid_keys_drops_them_names_them_and_leaves_a_valid_file()
    {
        using var host = new SandboxHost("writer-repair");
        host.WriteUserConfig("""{ "dryRun": false, "volumes": { "anonymousMaxGB": 1 }, "npm": { "maxCacheGb": -5 } }""");
        ConfigLoader.Load(host.Paths, host.Files).IsObserveOnly.Should().BeTrue("the fixture must be invalid, or this test proves nothing");

        var result = Writer(host).Set(ConfigKeys.Journal.KeepDays, new ConfigValue.Int(10));

        var written = result.Should().BeOfType<UserConfigWriteResult.Written>().Subject;
        written.DroppedKeys.Should().BeEquivalentTo("volumes.anonymousMaxGB", "npm.maxCacheGb");
        written.MovedAsideTo.Should().BeEmpty();
        written.PinnedDryRun.Should().BeTrue("two entries were lost, so the repair turns the timer dry (retro gate over PR #4)");
        var loaded = ConfigLoader.Load(host.Paths, host.Files);
        loaded.Should().BeOfType<ConfigLoadResult.Valid>("set must leave a valid user layer behind");
        loaded.Config.Bool(ConfigKeys.DryRun).Should().BeTrue("a lossy repair pins dryRun, overriding even the kept dryRun = false");
        loaded.Config.Int(ConfigKeys.Journal.KeepDays).Should().Be(10);
    }

    [Fact]
    public void Set_on_a_file_that_is_not_json_moves_it_aside_with_a_utc_stamp_and_writes_a_fresh_one()
    {
        using var host = new SandboxHost("writer-broken");
        host.WriteUserConfig("{ this is not json");

        var result = Writer(host).Set(ConfigKeys.DryRun, new ConfigValue.Bool(false));

        var written = result.Should().BeOfType<UserConfigWriteResult.Written>().Subject;
        written.MovedAsideTo.Should().Be(host.Paths.UserConfigFile + ".broken-20261002T120000Z");
        File.ReadAllText(written.MovedAsideTo).Should().Be("{ this is not json", "the user's text is never discarded");
        var loaded = ConfigLoader.Load(host.Paths, host.Files);
        loaded.Should().BeOfType<ConfigLoadResult.Valid>();
        loaded.Config.Bool(ConfigKeys.DryRun).Should().BeFalse();
    }

    [Fact]
    public void Two_repairs_in_the_same_second_keep_both_broken_files_and_both_succeed()
    {
        // The stamp has whole-second precision and the clock is frozen, so both repairs ask for the
        // same aside name; the second must find a free one rather than fail or overwrite the first.
        using var host = new SandboxHost("writer-broken-twice");
        var writer = Writer(host);
        host.WriteUserConfig("{ first broken text");
        var first = writer.Set(ConfigKeys.DryRun, new ConfigValue.Bool(false));
        host.WriteUserConfig("{ second broken text");

        var second = writer.Set(ConfigKeys.DryRun, new ConfigValue.Bool(true));

        var firstAside = first.Should().BeOfType<UserConfigWriteResult.Written>().Subject.MovedAsideTo;
        var secondAside = second.Should().BeOfType<UserConfigWriteResult.Written>().Subject.MovedAsideTo;
        firstAside.Should().Be(host.Paths.UserConfigFile + ".broken-20261002T120000Z", "the first aside keeps the plain UTC stamp");
        secondAside.Should().NotBe(firstAside).And.StartWith(host.Paths.UserConfigFile + ".broken-20261002T120000Z", "the stamp stays readable for a person");
        File.ReadAllText(firstAside).Should().Be("{ first broken text", "an existing aside file is never overwritten");
        File.ReadAllText(secondAside).Should().Be("{ second broken text");
        ConfigLoader.Load(host.Paths, host.Files).Config.Bool(ConfigKeys.DryRun).Should().BeTrue("the second set still wrote its value");
    }

    [Fact]
    public void Reset_removes_the_key_and_says_whether_it_was_there()
    {
        using var host = new SandboxHost("writer-reset");
        host.WriteUserConfig("""{ "dryRun": false, "volumes": { "anonymousMaxGb": 25 } }""");

        var removed = Writer(host).Reset(ConfigKeys.Volumes.AnonymousMaxGb);
        var absent = Writer(host).Reset(ConfigKeys.Npm.MaxCacheGb);

        removed.Should().BeOfType<UserConfigWriteResult.Written>().Which.KeyWasPresent.Should().BeTrue();
        absent.Should().BeOfType<UserConfigWriteResult.Written>().Which.KeyWasPresent.Should().BeFalse();
        var config = ConfigLoader.Load(host.Paths, host.Files).Config;
        config.Entry(ConfigKeys.Volumes.AnonymousMaxGb).Layer.Should().Be(ConfigLayer.Default);
        config.Entry(ConfigKeys.DryRun).Layer.Should().Be(ConfigLayer.User);
        host.ReadUserConfig().Should().NotContain("volumes");
    }

    [Fact]
    public void Reset_on_a_missing_file_writes_an_empty_object_rather_than_failing()
    {
        using var host = new SandboxHost("writer-reset-missing");

        var result = Writer(host).Reset(ConfigKeys.DryRun);

        result.Should().BeOfType<UserConfigWriteResult.Written>().Which.KeyWasPresent.Should().BeFalse();
        host.ReadUserConfig().Trim().Should().Be("{}");
    }

    [Fact]
    public void The_write_leaves_no_temporary_file_in_the_config_directory()
    {
        using var host = new SandboxHost("writer-tmp");

        Writer(host).Set(ConfigKeys.DryRun, new ConfigValue.Bool(false));

        Directory.GetFiles(Path.GetDirectoryName(host.Paths.UserConfigFile)!).Should().ContainSingle()
            .Which.Should().Be(host.Paths.UserConfigFile);
    }

    // Retro gate over PR #4 (plan round, accepted): a repair that LOSES something the person wrote must not hand the timer the
    // defaults it hid — auto.A4 = off lost with an unparseable file is A4 = on (its default) at the next timer run.
    [Fact]
    public void A_repair_that_discards_an_unparseable_layer_turns_the_timer_dry_rather_than_back_on_by_default()
    {
        using var host = new SandboxHost("writer-repair-pins-dry");
        host.WriteMachineConfig("""{ "dryRun": false }""");
        host.WriteUserConfig("""{ "auto": { "A4": false }, oops }""");
        ConfigLoader.Load(host.Paths, host.Files).IsObserveOnly.Should().BeTrue("the fixture must be broken, or this test proves nothing");

        Writer(host).Set(ConfigKeys.RefreshSeconds, new ConfigValue.Int(120)).Should().BeOfType<UserConfigWriteResult.Written>();

        var config = ConfigLoader.Load(host.Paths, host.Files).Config;
        config.Entry(ConfigKeys.DryRun).Should().Be(
            new ConfigEntry(ConfigKeys.DryRun, new ConfigValue.Bool(true), ConfigLayer.User),
            "the A4 = off the person wrote is lost with the file, and its default (on) must not reach the timer unseen");
        config.Int(ConfigKeys.RefreshSeconds).Should().Be(120);
    }

    [Fact]
    public void A_repair_that_drops_another_invalid_entry_turns_the_timer_dry_too()
    {
        using var host = new SandboxHost("writer-repair-drop-pins-dry");
        host.WriteMachineConfig("""{ "dryRun": false }""");
        host.WriteUserConfig("""{ "auto": { "A4": "off" } }""");

        Writer(host).Set(ConfigKeys.RefreshSeconds, new ConfigValue.Int(120));

        ConfigLoader.Load(host.Paths, host.Files).Config.Bool(ConfigKeys.DryRun).Should().BeTrue("auto.A4 = \"off\" was dropped, so A4 is back at its default");
    }

    [Fact]
    public void Correcting_the_one_invalid_key_loses_nothing_else_and_pins_nothing()
    {
        using var host = new SandboxHost("writer-repair-lossless");
        host.WriteMachineConfig("""{ "dryRun": false }""");
        host.WriteUserConfig("""{ "refreshSeconds": "soon" }""");

        Writer(host).Set(ConfigKeys.RefreshSeconds, new ConfigValue.Int(120));

        var config = ConfigLoader.Load(host.Paths, host.Files).Config;
        config.Entry(ConfigKeys.DryRun).Layer.Should().Be(ConfigLayer.Machine, "the only entry dropped was the one being written");
        config.Int(ConfigKeys.RefreshSeconds).Should().Be(120);
    }

    // Retro gate over PR #4 (consultant): the broken layer was moved aside BEFORE the write was checked, so a set the loader
    // would refuse wrote nothing — and still took the person's file away.
    [Fact]
    public void A_set_the_loader_would_refuse_leaves_a_broken_layer_where_it_is()
    {
        using var host = new SandboxHost("writer-refused-no-move");
        host.WriteMachineConfig("""{ "logs": { "maxRangeDays": 100 } }""");
        host.WriteUserConfig("{ this is not json");

        var result = Writer(host).Set(ConfigKeys.Runs.HistoryRetentionDays, new ConfigValue.Int(300));

        result.Should().BeOfType<UserConfigWriteResult.BreaksRule>();
        File.Exists(host.Paths.UserConfigFile).Should().BeTrue("a refused write moves nothing");
        File.ReadAllText(host.Paths.UserConfigFile).Should().Be("{ this is not json");
        Directory.GetFiles(Path.GetDirectoryName(host.Paths.UserConfigFile)!, "config.json.broken-*").Should().BeEmpty();
    }

    // Fix-PR plan round (retro gate over PR #4): the move aside and the write are two steps, so a write that fails after the
    // move left NO user layer — the next timer run loaded the defaults the repair exists to keep away.
    [Fact]
    public void A_write_that_fails_after_the_move_aside_puts_the_broken_layer_back()
    {
        using var host = new SandboxHost("writer-write-fails");
        host.WriteUserConfig("""{ "auto": { "A4": false }, oops }""");
        var writer = new UserConfigWriter(host.Paths, new FailingWrites(host.Files), new FixedTimeProvider());

        var set = () => writer.Set(ConfigKeys.RefreshSeconds, new ConfigValue.Int(120));

        set.Should().Throw<IOException>();
        File.ReadAllText(host.Paths.UserConfigFile).Should().Be("""{ "auto": { "A4": false }, oops }""", "the person's layer is back where it was");
        Directory.GetFiles(Path.GetDirectoryName(host.Paths.UserConfigFile)!, "config.json.broken-*").Should().BeEmpty();
    }

    [Fact]
    public void After_a_lossy_repair_the_timer_decides_dry_even_past_its_first_week()
    {
        using var host = new SandboxHost("writer-repair-timer-dry");
        host.WriteMachineConfig("""{ "dryRun": false }""");
        host.WriteUserConfig("""{ "auto": { "A4": false }, oops }""");
        Directory.CreateDirectory(host.Paths.StateDirectory);
        File.WriteAllText(DryRunWindow.File(host.Paths), $$"""{ "schemaVersion": 1, "at": "{{FixedTimeProvider.DefaultNow.AddDays(-30):O}}" }""");

        Writer(host).Set(ConfigKeys.RefreshSeconds, new ConfigValue.Int(120));

        var config = ConfigLoader.Load(host.Paths, host.Files).Config;
        DryRunWindow.Decide(RunTrigger.Timer, config, host.Paths, host.Files, FixedTimeProvider.DefaultNow)
            .Should().Be(new DryRunDecision(true, "the setting dryRun is on"), "the week is long over; only the pinned setting keeps the timer previewing");
    }

    // Fix-PR code round (session 3991eba5): the dryRun exemption was meant for a person WRITING dryRun; a reset of it writes
    // nothing, so a lossy repair by `config reset dryRun` must pin too.
    [Fact]
    public void A_lossy_repair_by_a_reset_of_dryRun_still_pins_it()
    {
        using var host = new SandboxHost("writer-reset-dry-pins");
        host.WriteMachineConfig("""{ "dryRun": false }""");
        host.WriteUserConfig("""{ "auto": { "A4": false }, oops }""");

        Writer(host).Reset(ConfigKeys.DryRun).Should().BeOfType<UserConfigWriteResult.Written>().Which.PinnedDryRun.Should().BeTrue();

        ConfigLoader.Load(host.Paths, host.Files).Config.Bool(ConfigKeys.DryRun).Should().BeTrue();
    }

    // Fix-PR code round: a process killed between the move aside and the write left NO user layer. A layer whose bytes were
    // read is COPIED aside, so it is still in place while its replacement is written.
    [Fact]
    public void While_the_repaired_layer_is_written_the_broken_one_is_still_in_place()
    {
        using var host = new SandboxHost("writer-crash-window");
        host.WriteUserConfig("""{ "auto": { "A4": false }, oops }""");
        var probe = new LayerAtWriteTime(host.Files, host.Paths.UserConfigFile);

        new UserConfigWriter(host.Paths, probe, new FixedTimeProvider()).Set(ConfigKeys.RefreshSeconds, new ConfigValue.Int(120));

        probe.Seen.Should().Be("""{ "auto": { "A4": false }, oops }""", "a crash at that moment must leave the person's layer, not none");
    }

    [Fact]
    public void A_put_back_that_is_refused_is_said_never_swallowed()
    {
        using var host = new SandboxHost("writer-putback-refused");
        Directory.CreateDirectory(host.Paths.UserConfigFile); // unreadable as a file: it can only be MOVED aside
        var writer = new UserConfigWriter(host.Paths, new FailingWritesRefusedPutBack(host.Files), new FixedTimeProvider());

        var set = () => writer.Set(ConfigKeys.RefreshSeconds, new ConfigValue.Int(120));

        set.Should().Throw<InvalidOperationException>().WithMessage("*could not put the broken user layer back*");
    }

    // Fix-PR consultation f4a0e9b4: an EMPTY layer is read too (zero bytes), and took the move path with its absent-layer window.
    [Fact]
    public void An_empty_broken_layer_is_also_still_in_place_while_its_replacement_is_written()
    {
        using var host = new SandboxHost("writer-crash-window-empty");
        host.WriteUserConfig(string.Empty);
        var probe = new LayerAtWriteTime(host.Files, host.Paths.UserConfigFile);

        new UserConfigWriter(host.Paths, probe, new FixedTimeProvider()).Set(ConfigKeys.RefreshSeconds, new ConfigValue.Int(120));

        probe.Seen.Should().Be(string.Empty, "an empty layer was read, so it is copied aside like any other");
    }

    // Fix-PR consultation f4a0e9b4: the copy aside probed a free name, then WROTE it with a replacing rename — a second repair
    // that took the same name in between had its kept file overwritten. The kept file is created exclusively.
    [Fact]
    public void A_kept_broken_file_that_appears_under_the_chosen_name_is_never_overwritten()
    {
        using var host = new SandboxHost("writer-aside-race");
        host.WriteUserConfig("{ this is not json");
        var racer = new PlantsTheAsideName(host.Files);

        var written = new UserConfigWriter(host.Paths, racer, new FixedTimeProvider()).Set(ConfigKeys.RefreshSeconds, new ConfigValue.Int(120));

        racer.Planted.Should().NotBeEmpty("the race must have been staged, or this test proves nothing");
        File.ReadAllText(racer.Planted).Should().Be("another repair's file", "nothing a concurrent repair kept is ever overwritten");
        written.Should().BeOfType<UserConfigWriteResult.Written>().Which.MovedAsideTo.Should().NotBe(racer.Planted);
    }

    private sealed class PlantsTheAsideName(IFileSystem inner) : DelegatingFileSystem(inner)
    {
        public string Planted { get; private set; } = string.Empty;

        public override DeletionVerdict WriteFileAtomically(string path, ReadOnlySpan<byte> content, DeletionScope scope)
        {
            Plant(path);
            return base.WriteFileAtomically(path, content, scope);
        }

        public override ExclusiveCreate CreateFileExclusively(string path, ReadOnlySpan<byte> content, DeletionScope scope)
        {
            Plant(path);
            return base.CreateFileExclusively(path, content, scope);
        }

        private void Plant(string path)
        {
            if (Planted.Length == 0 && path.Contains(".broken-", StringComparison.Ordinal))
            {
                File.WriteAllText(path, "another repair's file");
                Planted = path;
            }
        }
    }

    // Fix-PR final round: the undo ran only for an I/O failure, so any other exception out of the write left an unreadable
    // layer moved aside and the user layer absent.
    [Fact]
    public void Any_exception_out_of_the_write_puts_a_moved_layer_back()
    {
        using var host = new SandboxHost("writer-any-exception");
        Directory.CreateDirectory(host.Paths.UserConfigFile); // unreadable as a file: it can only be MOVED aside
        var writer = new UserConfigWriter(host.Paths, new WritesThrowNotSupported(host.Files), new FixedTimeProvider());

        var set = () => writer.Set(ConfigKeys.RefreshSeconds, new ConfigValue.Int(120));

        set.Should().Throw<NotSupportedException>("the original failure is what the person needs to see");
        Directory.Exists(host.Paths.UserConfigFile).Should().BeTrue("the moved layer is back where it was");
    }

    private sealed class WritesThrowNotSupported(IFileSystem inner) : DelegatingFileSystem(inner)
    {
        public override DeletionVerdict WriteFileAtomically(string path, ReadOnlySpan<byte> content, DeletionScope scope) =>
            throw new NotSupportedException("the file system does not support this write");
    }

    private sealed class LayerAtWriteTime(IFileSystem inner, string layer) : DelegatingFileSystem(inner)
    {
        public string Seen { get; private set; } = "(missing)";

        public override DeletionVerdict WriteFileAtomically(string path, ReadOnlySpan<byte> content, DeletionScope scope)
        {
            if (path == layer)
            {
                Seen = File.Exists(layer) ? File.ReadAllText(layer) : "(missing)";
            }

            return base.WriteFileAtomically(path, content, scope);
        }
    }

    private sealed class FailingWritesRefusedPutBack(IFileSystem inner) : DelegatingFileSystem(inner)
    {
        private int _directoryMoves;

        public override DeletionVerdict WriteFileAtomically(string path, ReadOnlySpan<byte> content, DeletionScope scope) =>
            throw new IOException("No space left on device");

        public override DeletionVerdict MoveDirectory(string from, string to, DeletionScope scope) =>
            ++_directoryMoves == 1 ? base.MoveDirectory(from, to, scope) : DeletionVerdict.Refuse(DeletionRule.PathChanged, "test: the put-back is refused");
    }

    private sealed class FailingWrites(IFileSystem inner) : DelegatingFileSystem(inner)
    {
        public override DeletionVerdict WriteFileAtomically(string path, ReadOnlySpan<byte> content, DeletionScope scope) =>
            throw new IOException("No space left on device");
    }
}
