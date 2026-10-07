using System.Runtime.Versioning;

using FluentAssertions;

using WslCare.TestSupport;

using static WslCare.Scenarios.InstallChecks;

namespace WslCare.Scenarios;

/// <summary>
/// The scripted clock of <see cref="InstallWorld.UseScriptedClock"/> is harness code under test (generated-code-tests rule
/// §3): it answers the two forms <c>install.sh</c> uses exactly as the real tools would, moves only when slept on, and is
/// STRICTER than the real tools — every other form is refused, so a script that starts reading the clock another way fails
/// instead of reading a stopped one.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ScriptedClockTests
{
    private static Task<ChildResult> Shell(InstallWorld world, string command) =>
        ChildProcess.RunAsync("/bin/sh", ["-c", command], world.Environment, world.Root);

    [Fact]
    public async Task Date_answers_the_scripted_second_and_sleep_moves_it_at_once()
    {
        Linux();
        using var world = new InstallWorld("scripted-clock");
        world.UseScriptedClock();

        var started = DateTime.UtcNow;
        var result = await Shell(world, "date +%s; sleep 5; date +%s; sleep 0; sleep 25; date +%s");

        result.Exit.Should().Be(0, result.Stderr);
        string[] read = [$"{InstallWorld.ScriptedClockStart}", $"{InstallWorld.ScriptedClockStart + 5}", $"{InstallWorld.ScriptedClockStart + 30}"];
        result.StdoutLines.Should().Equal(read, "date +%s reads the clock and every sleep adds its seconds");
        world.ClockSeconds.Should().Be(InstallWorld.ScriptedClockStart + 30);
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(20), "30 scripted seconds are slept without waiting for them");
    }

    [Theory]
    [InlineData("date", "date answers only +%s, not: ")]
    [InlineData("date -u +%s", "date answers only +%s, not: -u +%s")]
    [InlineData("date +%Y", "date answers only +%s, not: +%Y")]
    [InlineData("sleep", "sleep takes one whole number of seconds, not: ")]
    [InlineData("sleep 1.5", "sleep takes one whole number of seconds, not: 1.5")]
    [InlineData("sleep 5s", "sleep takes one whole number of seconds, not: 5s")]
    [InlineData("sleep ''", "sleep takes one whole number of seconds, not: ")]
    [InlineData("sleep 1 2", "sleep takes one whole number of seconds, not: 1 2")]
    public async Task Every_other_form_is_refused_naming_what_it_was_asked_and_the_clock_stays(string command, string refusal)
    {
        Linux();
        using var world = new InstallWorld("scripted-clock-refused");
        world.UseScriptedClock();

        var result = await Shell(world, command);

        result.Exit.Should().Be(2, $"the scripted clock refuses `{command}`:\n{result.Stdout}\n{result.Stderr}");
        result.Stderr.Should().Be($"scripted clock: {refusal}\n");
        result.Stdout.Should().BeEmpty();
        world.ClockSeconds.Should().Be(InstallWorld.ScriptedClockStart, "a refused call moves nothing");
    }
}
