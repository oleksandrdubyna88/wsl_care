using System.Reflection;
using System.Text;

using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Health;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Health;

/// <summary>
/// E15.S1: the second fixed interop script — its text (one line of ASCII, two clamped integers, every tag the parser reads)
/// and its parser over the answer CAPTURED on 2026-10-10 (<see cref="WindowsDiskFixture"/>): read by tag, a cut answer
/// unavailable as a whole, a malformed line counted and skipped, every figure a reading that is never a silent 0.
/// </summary>
public sealed class WindowsDiskTests
{
    private const int MaxRead = 5000;
    private const int SinceDays = 60;

    private static T Value<T>(Reading<T> reading)
    {
        reading.Should().BeOfType<Reading<T>.Available>(reading.ReasonOrEmpty);
        return ((Reading<T>.Available)reading).Value;
    }

    private static WindowsDiskFacts Parse(string stdout) => Value(WindowsDiskParsers.Parse(stdout, MaxRead, SinceDays));

    /// <summary>The captured answer with its last line, <c>end=1</c>, moved after <paramref name="lines"/>.</summary>
    private static string With(params string[] lines) =>
        WindowsDiskFixture.Answer.Replace(WindowsDiskCommands.EndLine + "\n", string.Empty, StringComparison.Ordinal) + string.Join('\n', lines) + "\n" + WindowsDiskCommands.EndLine + "\n";

    private static string Without(string tag) =>
        string.Join('\n', WindowsDiskFixture.Answer.Split('\n').Where(l => !l.StartsWith(tag, StringComparison.Ordinal)));

    [Fact]
    public void The_captured_answer_reads_as_the_owners_machine_was_that_evening()
    {
        var facts = Parse(WindowsDiskFixture.Answer);

        facts.MalformedLines.Should().Be(0);
        var distros = Value(facts.Distros);
        distros.Select(d => d.Name).Should().Equal("Ubuntu", "docker-desktop", "Ubuntu-26.04");
        var ubuntu = distros[0];
        ubuntu.Path.Should().EndWith(@"\CanonicalGroupLimited.Ubuntu_79rhkp1fndgsc\LocalState\ext4.vhdx");
        Value(ubuntu.SizeBytes).Should().Be(255_195_086_848, "237.7 GiB on the host (D2)");
        Value(ubuntu.Sparse).Should().BeFalse();
        Value(ubuntu.LastWriteUtc).Should().Be(new DateTimeOffset(2026, 10, 10, 18, 29, 9, TimeSpan.Zero).AddTicks(3902750));
        var docker = Value(facts.DockerDataDisk);
        docker.Name.Should().Be(WindowsDiskParsers.DockerDataDiskName);
        Value(docker.SizeBytes).Should().Be(132_338_679_808);
        var volumes = Value(facts.Volumes);
        volumes.Select(v => v.Letter).Should().Equal("E", "D", "C", "F");
        volumes.Single(v => v.Letter == "D").Should().Match<WindowsVolume>(v => v.FileSystem == "ReFS" && v.Label == "git");
        Value(volumes.Single(v => v.Letter == "D").DiskNumber).Should().Be(0);
        Value(volumes.Single(v => v.Letter == "D").SizeBytes).Should().Be(209_715_200_000);
        Value(volumes.Single(v => v.Letter == "D").FreeBytes).Should().Be(24_833_069_056);
        volumes.Single(v => v.Letter == "C").Label.Should().BeEmpty("C: has no label");
        Value(volumes.Single(v => v.Letter == "E").DiskNumber).Should().Be(1, "E: is the USB keyboard's storage function (D1)");
        Value(facts.PhysicalDisks).Should().Equal(
            new WindowsPhysicalDisk(1, "USB", "Healthy", "OK", "1.6", "ClawsKey ClawsKeyboard"),
            new WindowsPhysicalDisk(0, "NVMe", "Healthy", "OK", "G70YG252", "SOLIDIGM SB5PH27X038T"));
        facts.Reliability.ReasonOrEmpty.Should().Contain("refused").And.Contain("CimException").And.Contain("elevation");
        var events = Value(facts.Events);
        events.Should().Match<StorageEvents>(e => e.Read == 63 && e.MaxRead == MaxRead && e.SinceDays == SinceDays && !e.Capped);
        events.Groups.Should().HaveCount(4).And.OnlyContain(g => g.Provider == "disk" && g.Id == 11);
        events.Groups.Select(g => Value(g.Disk)).Should().OnlyContain(n => n == 1, "all 63 are on Harddisk1, the keyboard, not the NVMe (D4)");
        events.Groups.Sum(g => g.Count).Should().Be(63);
        events.Groups[0].Day.Should().Be(new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero), "a calendar day is stamped UTC, never converted");
        Value(facts.CommitBytes).Should().Be(110_976_245_760);
        Value(facts.CommitLimitBytes).Should().Be(156_356_161_536);
        Value(facts.CompressionBytes).Should().Be(10_582_515_712);
        Value(facts.Identity.Edition).Should().Be("Professional");
        Value(facts.Identity.DisplayVersion).Should().Be("26H2");
        Value(facts.Identity.Build).Should().Be("26300.9550");
        Value(facts.Identity.InstalledAt).Should().Be(DateTimeOffset.FromUnixTimeSeconds(1_784_190_275));
        Value(facts.Identity.MachineGuid).Should().Be("00000000-0000-4000-8000-000000000002");
        var adapters = Value(facts.Adapters);
        adapters.Select(a => Value(a.ProblemCode)).Should().Equal(0, 22);
        adapters[1].Name.Should().Be("AMD Radeon AI PRO R9700");
        adapters[1].PnpDeviceId.Should().StartWith(@"PCI\VEN_1002&DEV_7551");
    }

    [Theory]
    [InlineData("")]
    [InlineData("distro=1|0|2026-10-10T18:29:09Z|C:\\x\\ext4.vhdx|Ubuntu\n")]
    public void An_answer_without_its_end_line_was_cut_and_is_unavailable_as_a_whole(string stdout)
    {
        var reading = WindowsDiskParsers.Parse(stdout, MaxRead, SinceDays);

        reading.IsAvailable.Should().BeFalse();
        reading.ReasonOrEmpty.Should().Contain("cut").And.Contain(WindowsDiskCommands.EndLine);
    }

    [Fact]
    public void The_end_line_must_be_the_last_line_not_merely_present()
    {
        var reading = WindowsDiskParsers.Parse("end=1\nvolume=C|0|NTFS|10|5|\n", MaxRead, SinceDays);

        reading.ReasonOrEmpty.Should().Contain("cut");
    }

    [Fact]
    public void Crlf_line_ends_read_like_lf()
    {
        var facts = Parse(WindowsDiskFixture.Answer.Replace("\n", "\r\n", StringComparison.Ordinal));

        facts.MalformedLines.Should().Be(0);
        Value(facts.Volumes).Should().HaveCount(4);
    }

    [Theory]
    [InlineData("volume=Z|x|NTFS|10|5|label")]
    [InlineData("volume=Z|0|NTFS|ten|5|label")]
    [InlineData("volume=Z|0|NTFS")]
    [InlineData("disk=zero|NVMe|Healthy|OK|1|model")]
    [InlineData("event=2026-13-40|disk|11|Harddisk1|3")]
    [InlineData("event=2026-10-10|disk|eleven|Harddisk1|3")]
    [InlineData("distro=12|maybe|2026-10-10T18:29:09Z|C:\\x\\ext4.vhdx|Old")]
    [InlineData("a line with no tag")]
    [InlineData("unknown.tag=1")]
    public void A_malformed_line_is_counted_and_skipped_and_the_rest_still_reads(string line)
    {
        var facts = Parse(With(line));

        facts.MalformedLines.Should().Be(1);
        Value(facts.Volumes).Select(v => v.Letter).Should().Equal("E", "D", "C", "F");
        Value(facts.PhysicalDisks).Should().HaveCount(2);
        Value(facts.Distros).Should().HaveCount(3);
        Value(facts.Events).Groups.Should().HaveCount(4);
    }

    [Fact]
    public void A_file_that_is_not_there_is_unavailable_never_zero()
    {
        var facts = Parse(With(@"distro=|||C:\Users\user\old\ext4.vhdx|Old").Replace(
            @"docker=132338679808|0|2026-10-10T18:29:06.8072598Z|C:\Users\user\AppData\Local\Docker\wsl\disk\docker_data.vhdx",
            @"docker=|||C:\Users\user\AppData\Local\Docker\wsl\disk\docker_data.vhdx",
            StringComparison.Ordinal));

        var old = Value(facts.Distros).Single(d => d.Name == "Old");
        old.SizeBytes.ReasonOrEmpty.Should().Contain("not there").And.Contain(@"C:\Users\user\old\ext4.vhdx");
        old.Sparse.IsAvailable.Should().BeFalse();
        old.LastWriteUtc.IsAvailable.Should().BeFalse();
        facts.DockerDataDisk.ReasonOrEmpty.Should().Contain("not there").And.Contain("docker_data.vhdx");
        facts.MalformedLines.Should().Be(0, "an absent file is an answer, not a malformed line");
    }

    [Fact]
    public void A_volume_without_a_partition_has_no_disk_number_with_the_reason()
    {
        var facts = Parse(With("volume=Z||NTFS|10|5|Ram"));

        Value(facts.Volumes).Single(v => v.Letter == "Z").DiskNumber.ReasonOrEmpty.Should().Contain("Z:").And.Contain("partition");
    }

    [Fact]
    public void An_event_that_names_no_disk_keeps_its_count_and_says_why_the_disk_is_unknown()
    {
        var facts = Parse(With("event=2026-10-01|Ntfs|153||2"));

        var group = Value(facts.Events).Groups.Single(g => g.Provider == "Ntfs");
        group.Count.Should().Be(2);
        group.Disk.ReasonOrEmpty.Should().Contain("Harddisk");
    }

    [Fact]
    public void A_section_the_script_could_not_read_is_unavailable_naming_what_failed()
    {
        string[] failed = [WindowsDiskCommands.VolumeTag, WindowsDiskCommands.EventTag, WindowsDiskCommands.EventsReadTag, WindowsDiskCommands.DistroTag];
        var lines = WindowsDiskFixture.Answer.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l != WindowsDiskCommands.EndLine && !failed.Any(t => l.StartsWith(t, StringComparison.Ordinal)))
            .Concat(["volumes.error=CimException", "events.error=EventLogException", "distros.error=ItemNotFoundException", WindowsDiskCommands.EndLine]);

        var facts = Parse(string.Join('\n', lines) + "\n");

        facts.Volumes.ReasonOrEmpty.Should().Contain("volumes").And.Contain("CimException");
        facts.Events.ReasonOrEmpty.Should().Contain("EventLogException");
        facts.Distros.ReasonOrEmpty.Should().Contain("ItemNotFoundException");
        facts.MalformedLines.Should().Be(0);
        Value(facts.PhysicalDisks).Should().HaveCount(2, "one failed section leaves the others readable");
    }

    [Fact]
    public void A_figure_the_script_did_not_print_is_unavailable_with_the_reason()
    {
        var facts = Parse(Without(WindowsDiskCommands.CommitTag));

        facts.CommitBytes.ReasonOrEmpty.Should().Contain("commit");
        facts.CommitLimitBytes.IsAvailable.Should().BeFalse();
        Value(facts.CompressionBytes).Should().BePositive();
    }

    [Fact]
    public void No_storage_events_is_a_count_of_zero_and_reaching_the_cap_says_the_counts_are_a_lower_bound()
    {
        var none = Value(Parse(string.Join('\n', WindowsDiskFixture.Answer.Split('\n').Where(l => !l.StartsWith("event", StringComparison.Ordinal)))
            .Replace(WindowsDiskCommands.EndLine, "events.read=0\n" + WindowsDiskCommands.EndLine, StringComparison.Ordinal)).Events);
        none.Should().Match<StorageEvents>(e => e.Read == 0 && e.Groups.Count == 0 && !e.Capped);

        var capped = Value(WindowsDiskParsers.Parse(WindowsDiskFixture.Answer, maxRead: 63, sinceDays: SinceDays)).Events;
        Value(capped).Capped.Should().BeTrue("63 read of at most 63");
    }

    [Fact]
    public void The_reliability_counters_read_elevated_are_kept_per_disk()
    {
        var answer = WindowsDiskFixture.Answer.Replace("reliability.refused=CimException", "reliability=0|41|2|0|0|5321\nreliability.read=1", StringComparison.Ordinal);

        var counters = Value(Parse(answer).Reliability).Should().ContainSingle().Subject;

        counters.Number.Should().Be(0);
        Value(counters.TemperatureCelsius).Should().Be(41);
        Value(counters.PowerOnHours).Should().Be(5321);
    }

    [Theory]
    [InlineData("machine.guid=not-a-guid", "machine")]
    [InlineData("windows.installDate=yesterday", "install")]
    public void An_identity_value_that_does_not_parse_is_unavailable_and_counted(string line, string what)
    {
        var tag = line[..(line.IndexOf('=') + 1)];
        var answer = string.Join('\n', WindowsDiskFixture.Answer.Split('\n').Select(l => l.StartsWith(tag, StringComparison.Ordinal) ? line : l));

        var facts = Parse(answer);

        (tag == WindowsDiskCommands.MachineGuidTag ? facts.Identity.MachineGuid.ReasonOrEmpty : facts.Identity.InstalledAt.ReasonOrEmpty).Should().Contain(what);
        facts.MalformedLines.Should().Be(1);
    }

    [Fact]
    public void The_script_is_one_line_of_ascii_with_no_double_quote_and_no_control_character()
    {
        var script = WindowsDiskCommands.Script;

        script.Should().NotContain("\"", "WSL interop and PowerShell quote double quotes their own way; the script needs none");
        script.Should().Match(s => s.All(c => c >= ' ' && c <= '~'), "ASCII only, and the never-list refuses a control character");
        script.Should().EndWith("[Console]::Out.Write(($o -join [char]10)+[char]10)");
        script.IndexOf("$o.Add('" + WindowsDiskCommands.EndLine + "')", StringComparison.Ordinal).Should().BeGreaterThan(script.LastIndexOf("$o.Add('adapter", StringComparison.Ordinal), "end=1 is added last");
        script.Should().Contain("$ProgressPreference='SilentlyContinue'");
    }

    [Fact]
    public void Every_tag_the_parser_reads_is_printed_by_the_script()
    {
        // Enumerated from the type, never retyped: a tag added without a line in the script is red here.
        var tags = typeof(WindowsDiskCommands).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.Name.EndsWith("Tag", StringComparison.Ordinal)).Select(f => (string)f.GetRawConstantValue()!).ToList();

        tags.Should().HaveCountGreaterThan(20);
        tags.Should().OnlyContain(t => WindowsDiskCommands.Script.Contains("'" + t, StringComparison.Ordinal), "each tag is printed as a quoted prefix");
    }

    [Theory]
    [InlineData(7, 250, "7", "250")]
    [InlineData(0, 1, "1", "100")]
    [InlineData(1000, 10_000_000, "365", "100000")]
    public void The_two_numbers_are_baked_in_as_integers_clamped_to_their_keys(int days, int max, string expectedDays, string expectedMax)
    {
        var script = WindowsDiskCommands.ScriptFor(days, max);

        script.Should().Contain($"-MaxEvents {expectedMax} ").And.Contain($"AddDays(-{expectedDays})");
    }

    [Fact]
    public void The_script_and_its_ceiling_follow_the_configuration_in_force()
    {
        WindowsDiskCommands.Script.Should().Contain("-MaxEvents 5000 ").And.Contain("AddDays(-60)");
        WindowsDiskCommands.WindowsDisk.Ceiling.Should().Be(TimeSpan.FromSeconds(30));

        using (Tuning.Use(Config("""{ "disk": { "eventsSinceDays": 14, "eventsMaxRead": 1000 }, "health": { "windowsDiskTimeoutSeconds": 45 } }""")))
        {
            WindowsDiskCommands.Script.Should().Contain("-MaxEvents 1000 ").And.Contain("AddDays(-14)");
            WindowsDiskCommands.WindowsDisk.Ceiling.Should().Be(TimeSpan.FromSeconds(45));
        }
    }

    [Fact]
    public void The_keys_have_their_ranges_defaults_and_trust()
    {
        var defaults = Tuning.Default.Config;

        ConfigKeys.Health.WindowsDiskTimeoutSeconds.Should().Match<ConfigKey.IntKey>(k => k.Name == "health.windowsDiskTimeoutSeconds" && k.Min == 5 && k.Max == 120 && k.Trust.MachineOnly);
        defaults.Int(ConfigKeys.Health.WindowsDiskTimeoutSeconds).Should().Be(30);
        ConfigKeys.Disk.EventsSinceDays.Should().Match<ConfigKey.IntKey>(k => k.Name == "disk.eventsSinceDays" && k.Min == 1 && k.Max == 365 && !k.Trust.MachineOnly);
        defaults.Int(ConfigKeys.Disk.EventsSinceDays).Should().Be(60);
        ConfigKeys.Disk.EventsMaxRead.Should().Match<ConfigKey.IntKey>(k => k.Name == "disk.eventsMaxRead" && k.Min == 100 && k.Max == 100000 && k.Trust.MachineOnly);
        defaults.Int(ConfigKeys.Disk.EventsMaxRead).Should().Be(5000);
    }

    private static EffectiveConfig Config(string machine)
    {
        var loaded = ConfigLoader.Load(
        [
            (ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults())),
            (new ConfigLayerFile(ConfigLayer.Machine, "/etc/wsl-care/config.json"), new FileReadResult.Content(Encoding.UTF8.GetBytes(machine))),
        ]);
        loaded.Errors.Should().BeEmpty();
        return loaded.Config;
    }
}
