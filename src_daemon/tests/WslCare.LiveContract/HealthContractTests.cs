using System.Diagnostics;

using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Docker;
using WslCare.Core.Health;
using WslCare.Core.Processes;
using WslCare.Core.Systemd;

namespace WslCare.LiveContract;

/// <summary>
/// E2.S3's commands against the real tools of this machine, parsed by the product's parsers (plan §15b #6): the
/// health collectors' <c>systemctl</c> / <c>journalctl</c> / <c>timedatectl</c> / <c>snap</c> answers, the Windows clock
/// probe through WSL interop, and Docker's event stream — the follower's assumptions, observed rather than read
/// about: a past window answers and ends, and a FUTURE <c>--until</c> streams and then closes by itself.
/// </summary>
public sealed class HealthContractTests
{
    [Fact]
    public async Task Systemctl_lists_the_failed_units_as_json_the_parser_reads()
    {
        var units = Available(HealthParsers.FailedUnits(await Live.SystemdAsync(SystemdCommands.FailedUnits)));

        units.Should().NotContain(u => u.Unit.Length == 0);
    }

    [Fact]
    public async Task Journalctl_lists_the_boots_as_json_and_the_oldest_entry_is_in_the_past()
    {
        var oldest = Available(HealthParsers.OldestJournalEntry(await Live.SystemdAsync(SystemdCommands.ListBoots)));

        oldest.Should().BeBefore(DateTimeOffset.UtcNow).And.BeAfter(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("systemd-resolved", "Clock change detected")]
    [InlineData("", "page allocation failure|invoked oom-killer")]
    [InlineData("systemd-resolved", "wsl-care-live-contract-matches-nothing")]
    public async Task A_journal_search_answers_a_count_even_when_nothing_matched(string unit, string pattern)
    {
        await Live.SystemdAsync(SystemdCommands.JournalDiskUsage); // a host without systemd skips here
        JournalScope scope = unit.Length == 0 ? new JournalScope.Kernel() : new JournalScope.Unit(unit);
        var command = SystemdCommands.Search(DateTimeOffset.UtcNow.AddHours(-4), scope, pattern);

        var matches = Available(HealthParsers.SearchMatches(command, await Live.RunAsync(command)));

        matches.Should().NotBeNull();
        if (pattern.StartsWith("wsl-care", StringComparison.Ordinal))
        {
            matches.Should().BeEmpty("journalctl exits 1 with nothing printed, and that is a count of 0");
        }
    }

    [Fact]
    public async Task Timedatectl_says_whether_ntp_is_on_and_the_clock_synchronised()
    {
        await Live.SystemdAsync(SystemdCommands.JournalDiskUsage);
        var answer = ToolAnswers.Read(SystemdCommands.TimeSync, await Live.RunAsync(SystemdCommands.TimeSync));

        Available(answer.Bind(HealthParsers.TimeSync)).Should().NotBeNull();
    }

    [Fact]
    public async Task Systemctl_version_names_systemd_and_a_unit_carries_its_unit_file_state()
    {
        Available(HealthParsers.SystemdVersion(await Live.SystemdAsync(SystemdCommands.Version))).Should().StartWith("systemd ");
        var timer = Available(SystemdUnit.Parse(await Live.SystemdAsync(SystemdCommands.ShowUnit("fstrim.timer"))));
        Available(SystemdUnit.Parse(await Live.SystemdAsync(SystemdCommands.ShowUnit("wsl-pro.service")))).Id.Should().Be("wsl-pro.service");

        if (timer.Exists)
        {
            timer.UnitFileState.Should().NotBeEmpty("UnitFileState is one of the properties asked for");
        }
    }

    [Fact]
    public async Task Snap_list_all_reads_with_the_header_the_parser_skips()
    {
        var outcome = await Live.RunAsync(HealthCommands.SnapList);
        if (outcome is CommandOutcome.FailedToStart failed)
        {
            Live.Unavailable($"snap is not installed here: {failed.Reason}");
        }

        var stdout = Available(ToolAnswers.Read(HealthCommands.SnapList, outcome));

        stdout.Split('\n')[0].Should().StartWith("Name").And.Contain("Rev").And.Contain("Notes");
        HealthParsers.DisabledSnapRevisions(stdout).Should().NotContain(r => !r.Revision.All(char.IsAsciiDigit), "an empty list is the ordinary answer: no disabled revision");
    }

    [Fact]
    public async Task The_windows_clock_probe_prints_two_instants_and_the_profile_from_inside_the_distro()
    {
        if (OperatingSystem.IsWindows())
        {
            Live.Unavailable("the clock probe is the distro's (WSL interop to powershell.exe); this binary runs on Windows");
        }

        var launched = DateTimeOffset.UtcNow;
        var outcome = await Live.RunAsync(HealthCommands.WindowsClock);
        if (outcome is CommandOutcome.FailedToStart failed)
        {
            Live.Unavailable($"powershell.exe is not reachable (no WSL interop here): {failed.Reason}");
        }

        var probe = Available(ToolAnswers.Read(HealthCommands.WindowsClock, outcome).Bind(HealthParsers.WindowsClock));

        probe.Profile.Should().MatchRegex(@"^[A-Za-z]:\\");
        (probe.PrintedAt - probe.ProcessStartedAt).Should().BePositive("the launch latency is measured on one clock");
        Math.Abs((probe.ProcessStartedAt - launched).TotalSeconds).Should().BeLessThan(60, "a skew of a minute would be a broken clock, not this probe");
    }

    [Fact]
    public async Task Docker_events_of_a_past_window_answer_every_kind_of_event_with_its_time()
    {
        var now = DateTimeOffset.UtcNow;
        var events = Available(DockerEvents.Parse(await Live.DockerAsync(DockerCommands.Backfill(now.AddHours(-24), now))));

        events.Should().NotContain(e => e.At < now.AddHours(-24).AddSeconds(-1) || e.At > now.AddSeconds(1));
    }

    [Fact]
    public async Task Docker_events_with_a_future_until_stream_and_then_close_by_themselves_at_it()
    {
        await Live.DockerAsync(DockerCommands.Version); // no daemon skips here
        var since = DateTimeOffset.UtcNow.AddSeconds(-5);
        var until = DateTimeOffset.UtcNow.AddSeconds(4);
        var watch = Stopwatch.StartNew();

        var (outcome, lines) = await Live.StreamAsync(DockerCommands.EventStream(since, until, TimeSpan.FromSeconds(30)));

        outcome.Should().BeOfType<CommandOutcome.Exited>().Which.ExitCode.Should().Be(0, "Docker ends the stream at --until on its own");
        watch.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(2), "the stream stayed open until the future --until").And.BeLessThan(TimeSpan.FromSeconds(25));
        lines.Should().NotContain(l => !DockerEvents.Parse(l).IsAvailable, "no start may happen in the window, and that is fine");
    }

    private static T Available<T>(Reading<T> reading)
    {
        reading.Should().BeOfType<Reading<T>.Available>("the product's parser reads the real answer ({0})", reading.ReasonOrEmpty);
        return ((Reading<T>.Available)reading).Value;
    }
}
