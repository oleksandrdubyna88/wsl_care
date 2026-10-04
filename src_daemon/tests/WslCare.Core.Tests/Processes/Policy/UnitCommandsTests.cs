using System.Text.RegularExpressions;

using FluentAssertions;

using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;
using WslCare.Core.Systemd;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Processes.Policy;

/// <summary>
/// The systemctl commands of a detached run (E6.S1, plan §15j B2 / M4): <c>start --no-block</c>, <c>stop</c> and <c>show</c>,
/// each with a CLOSED unit slot. Hostile unit names — another service, a glob, a path, a second word, a malformed run id, a
/// newline — never match a template and the product policy refuses them; only <c>wsl-care-act@&lt;runId&gt;.service</c> (and,
/// for a stop, <c>wsl-care.service</c>) passes. Plus a seeded property over mutated unit names.
/// </summary>
public sealed partial class UnitCommandsTests
{
    private static readonly RunId Run = RunId.New(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero), 4321);

    private static string Systemctl => UnitCommands.Start(Run).Argv[0];

    private static bool Allowed(params string[] arguments) =>
        CommandPolicy.Product.Review(new CommandRequest([Systemctl, .. arguments], TimeSpan.FromSeconds(1))).IsAllowed;

    public static TheoryData<string> Hostile() => [.. HostileInputs.HostileUnitNames];

    [Fact]
    public void The_act_unit_of_a_run_is_started_shown_and_stopped_and_the_timer_s_service_only_stopped()
    {
        var unit = SlotKind.ActUnit.Of(Run);

        unit.Should().Be("wsl-care-act@20261004T120000Z-4321.service");
        Allowed("start", "--no-block", unit).Should().BeTrue();
        Allowed("show", "--property=ActiveState", "--property=Job", unit).Should().BeTrue();
        Allowed("stop", unit).Should().BeTrue();
        Allowed("stop", UnitCommands.TimerService).Should().BeTrue("a wedged timer run is stopped through its own unit");
        Allowed("start", "--no-block", UnitCommands.TimerService).Should().BeFalse("the timer's service is started by its timer only");
        Allowed("show", "--property=ActiveState", "--property=Job", UnitCommands.TimerService).Should().BeFalse();
        Allowed("start", unit).Should().BeFalse("a start always queues without waiting (--no-block)");
    }

    [Theory]
    [MemberData(nameof(Hostile))]
    public void A_hostile_unit_name_matches_no_template_and_the_policy_refuses_it(string name)
    {
        string[][] shapes = [["start", "--no-block", name], ["stop", name], ["show", "--property=ActiveState", "--property=Job", name]];
        UnitCommands.All.SelectMany(t => shapes.Where(t.Matches).Select(s => $"{t.Name}: {string.Join(' ', s)}")).Should().BeEmpty();
        Allowed("start", "--no-block", name).Should().BeFalse();
        Allowed("stop", name).Should().BeFalse();
        Allowed("show", "--property=ActiveState", "--property=Job", name).Should().BeFalse();
    }

    [Fact]
    public void A_stop_takes_exactly_one_unit_never_a_second_word()
    {
        Allowed("stop", SlotKind.ActUnit.Of(Run), "ssh.service").Should().BeFalse();
        Allowed("stop", UnitCommands.TimerService, SlotKind.ActUnit.Of(Run)).Should().BeFalse();
        Allowed("stop").Should().BeFalse();
    }

    [Fact]
    public void Every_unit_name_the_slot_accepts_out_of_a_hundred_thousand_mutations_is_exactly_an_act_unit_of_a_valid_run_id()
    {
        var inputs = new HostileInputs(20261004);
        var valid = SlotKind.ActUnit.Of(Run);
        var accepted = 0;
        const string alphabet = "wsl-care-act@.service0123456789TZ*/;$ \n\t-_.:ACT";
        for (var i = 0; i < 100_000; i++)
        {
            var chars = valid.ToCharArray().ToList();
            for (var edits = 1 + inputs.Next(3); edits > 0; edits--)
            {
                var at = inputs.Next(chars.Count + 1);
                switch (inputs.Next(3))
                {
                    case 0 when at < chars.Count:
                        chars.RemoveAt(at);
                        break;
                    case 1 when at < chars.Count:
                        chars[at] = alphabet[inputs.Next(alphabet.Length)];
                        break;
                    default:
                        chars.Insert(at, alphabet[inputs.Next(alphabet.Length)]);
                        break;
                }
            }

            var name = new string([.. chars]);
            if (!new SlotKind.ActUnit().Accepts(name))
            {
                continue;
            }

            accepted++;
            ActUnitShape().IsMatch(name).Should().BeTrue($"\"{name}\" was accepted, so it must be wsl-care-act@<yyyyMMddTHHmmssZ>-<pid>.service");
            RunId.TryParse(name["wsl-care-act@".Length..^".service".Length]).Should().NotBeNull();
        }

        accepted.Should().BePositive("some mutations (a digit for a digit) stay valid — the property is not judged on nothing");
    }

    [Theory]
    [InlineData("ActiveState=inactive\nJob=\n", false)]
    [InlineData("ActiveState=failed\n", false)]
    [InlineData("ActiveState=inactive\nJob=0\n", false)]
    [InlineData("", false)]
    [InlineData("ActiveState=inactive\nJob=12\n", true)]
    [InlineData("ActiveState=activating\n", true)]
    [InlineData("ActiveState=active\n", true)]
    [InlineData("ActiveState=deactivating\n", true)]
    [InlineData("ActiveState=reloading\n", true)]
    public void A_unit_is_busy_while_it_has_a_queued_job_or_any_active_state(string show, bool busy)
    {
        UnitCommands.Busy(show).Should().Be(busy);
    }

    [GeneratedRegex(@"^wsl-care-act@\d{8}T\d{6}Z-(0|[1-9]\d*)\.service$")]
    private static partial Regex ActUnitShape();
}
