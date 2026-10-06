using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using FluentAssertions;

using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Processes;
using WslCare.Core.Systemd;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Config;

/// <summary>
/// Retro review of PR #8, O1 (2026-10-06): every <c>timer.periodHours</c> the configuration ACCEPTS renders a timer drop-in whose
/// calendar systemd accepts and that fires every that many hours. <c>periodHours = 24</c> passed validation (range 1..24, "divides
/// 24") and rendered <c>OnCalendar=*-*-* 00/24:00:00</c>, which systemd 255 refuses ("Invalid argument"; <c>verify</c>: "Timer unit
/// lacks value setting. Refusing") — so after <c>install.sh</c> wrote that drop-in the timer never fired.
/// </summary>
public sealed partial class TimerCalendarTests
{
    private static readonly ConfigLayerFile Machine = new(ConfigLayer.Machine, "/etc/wsl-care/config.json");

    private const int HoursPerDay = 24;

    /// <summary>Every period the configuration accepts — derived from the key's range and the loader's own rules, never retyped.</summary>
    public static TheoryData<int> AcceptedPeriods() =>
        [.. Enumerable.Range(ConfigKeys.Timer.PeriodHours.Min, ConfigKeys.Timer.PeriodHours.Max - ConfigKeys.Timer.PeriodHours.Min + 1).Where(h => !Load(h).IsObserveOnly)];

    [Fact]
    public void The_accepted_periods_are_the_divisors_of_a_day_and_include_the_whole_day()
    {
        AcceptedPeriods().Select(row => row.Data).Should().Equal([1, 2, 3, 4, 6, 8, 12, 24],
            "the rule keeps exactly the periods that divide 24 — and 24, the case that rendered a calendar systemd refuses, is one of them");
    }

    [Theory]
    [MemberData(nameof(AcceptedPeriods))]
    public void Every_accepted_period_renders_a_calendar_that_restarts_at_midnight_in_the_form_systemd_accepts(int hours)
    {
        Calendar(hours).Should().Be(hours == HoursPerDay ? "*-*-* 00:00:00" : $"*-*-* 00/{hours}:00:00",
            "a repetition of 24 hours is not a repetition systemd accepts: once a day is midnight itself");
        Render(hours).Should().Contain("OnCalendar=\nOnCalendar=", "the empty assignment still clears the unit's own calendar first");
    }

    /// <summary>systemd's own parser over each rendered calendar: it must parse, and its elapses must be exactly the period apart
    /// across midnight. <c>systemd-analyze calendar</c> EXITS 0 on a calendar it refuses (systemd 255, measured 2026-10-06: "Failed
    /// to parse calendar specification '*-*-* 00/24:00:00': Invalid argument", exit 0), so the output is read, never the exit code.</summary>
    [Theory]
    [MemberData(nameof(AcceptedPeriods))]
    public async Task Systemd_parses_every_rendered_calendar_and_fires_it_every_period(int hours)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "systemd-analyze exists on the Linux legs only");
        var analyze = SystemdAnalyze();
        Assert.SkipWhen(analyze.Length == 0, "systemd-analyze is not installed here");
        var iterations = (HoursPerDay / hours) + 2;

        var result = await ChildProcess.RunAsync(
            analyze,
            ["calendar", $"--iterations={iterations}", Calendar(hours)],
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["TZ"] = "UTC", ["LC_ALL"] = "C", ["SYSTEMD_COLORS"] = "0" });

        var output = result.Stdout + result.Stderr;
        output.Should().Contain("Normalized form:", $"systemd must parse {Calendar(hours)}");
        var elapses = Elapse().Matches(output).Select(m => DateTime.ParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).ToList();
        elapses.Should().HaveCount(iterations, output);
        elapses.Zip(elapses.Skip(1), (a, b) => b - a).Should().OnlyContain(gap => gap == TimeSpan.FromHours(hours), $"the timer fires every {hours} h, across midnight too:\n{output}");
    }

    private static ConfigLoadResult Load(int hours) =>
        ConfigLoader.Load(
        [
            (ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults())),
            (Machine, new FileReadResult.Content(Encoding.UTF8.GetBytes($$"""{ "timer": { "periodHours": {{hours}} } }"""))),
        ]);

    private static string Render(int hours)
    {
        using (Tuning.Use(Load(hours).Config))
        {
            return UnitDropIns.Render(UnitDropIns.Timer);
        }
    }

    /// <summary>The calendar the timer's drop-in sets: its one non-empty <c>OnCalendar=</c>.</summary>
    private static string Calendar(int hours) =>
        Render(hours).Split('\n').Single(l => l.StartsWith("OnCalendar=", StringComparison.Ordinal) && l.Length > "OnCalendar=".Length)["OnCalendar=".Length..];

    private static string SystemdAnalyze() =>
        ExecutableResolver.Resolve("systemd-analyze", Environment.GetEnvironmentVariable("PATH"), windows: false) is ResolvedExecutable.Found found ? found.Path : string.Empty;

    [GeneratedRegex(@"(?:Next elapse|Iteration #\d+): \w+ (\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}) UTC")]
    private static partial Regex Elapse();
}
