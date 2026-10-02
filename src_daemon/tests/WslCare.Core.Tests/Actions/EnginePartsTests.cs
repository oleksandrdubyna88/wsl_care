using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Config;
using WslCare.Core.Hosting;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;

namespace WslCare.Core.Tests.Actions;

/// <summary>The small parts the engine and the policy are built from: slot shapes, binding, the closed set of action ids,
/// the execution order, the privilege answer, the build detector.</summary>
public sealed class EnginePartsTests
{
    [Theory]
    [InlineData("30d", true)]
    [InlineData("1d", true)]
    [InlineData("3650d", true)]
    [InlineData("0d", false)]
    [InlineData("3651d", false)]
    [InlineData("030d", false)]
    [InlineData("-1d", false)]
    [InlineData("30", false)]
    [InlineData("30d;", false)]
    [InlineData("3e1d", false)]
    [InlineData("30 d", false)]
    [InlineData("--all", false)]
    public void A_number_slot_takes_canonical_digits_in_range_and_its_suffix_only(string value, bool accepted)
    {
        new SlotKind.Number(1, 3650, "d").Accepts(value).Should().Be(accepted);
    }

    [Theory]
    [InlineData("me", true)]
    [InlineData("_svc", true)]
    [InlineData("a-b1", true)]
    [InlineData("Me", false)]
    [InlineData("-me", false)]
    [InlineData("me;id", false)]
    [InlineData("", false)]
    [InlineData("ünï", false)]
    public void A_user_name_slot_takes_posix_account_names_only(string value, bool accepted)
    {
        new SlotKind.UserName().Accepts(value).Should().Be(accepted);
    }

    [Theory]
    [InlineData("Clock change detected", true)]
    [InlineData("page allocation failure|invoked oom-killer", true)]
    [InlineData("-e x", false)]
    [InlineData("a;b", false)]
    [InlineData("$(id)", false)]
    [InlineData("/home/me/git", false)]
    [InlineData("vm.drop_caches=3", false)]
    [InlineData("a\nb", false)]
    public void A_text_slot_takes_plain_search_text_and_nothing_that_could_be_a_path_a_setting_or_a_command(string value, bool accepted)
    {
        new SlotKind.Text(256).Accepts(value).Should().Be(accepted);
    }

    [Fact]
    public void Binding_names_the_slot_a_value_does_not_fit_and_refuses_extra_values()
    {
        var template = JournalVacuum.Vacuum;

        template.Bind(["--vacuum-time=30d"]).Should().BeOfType<TemplateBinding.Bound>().Which.Arguments.Should().Equal("--vacuum-time=30d");
        template.Bind(["--vacuum-time=0d"]).Should().BeOfType<TemplateBinding.Refused>().Which.Reason.Should().Contain("keep must be --vacuum-time=<1..3650>d");
        template.Bind(["--vacuum-time=30d", "--all"]).Should().BeOfType<TemplateBinding.Refused>().Which.Reason.Should().Contain("more than the template has slots for");
        template.Bind([]).Should().BeOfType<TemplateBinding.Refused>().Which.Reason.Should().Contain("no value for keep");
    }

    [Fact]
    public void A_catalogue_refuses_a_template_whose_executable_is_a_path_or_whose_repeat_is_not_last()
    {
        var pathed = () => new CommandCatalogue([new CommandTemplate("x", CommandScope.Machine, "/usr/bin/journalctl", [], TimeSpan.FromSeconds(1), 1)]);
        var misplaced = () => new CommandCatalogue([new CommandTemplate("y", CommandScope.Machine, "docker", [new ArgPart.Repeat("ids", new SlotKind.Hex(64), 1, 2), new ArgPart.Literal("x")], TimeSpan.FromSeconds(1), 1)]);

        pathed.Should().Throw<ArgumentException>().WithMessage("*bare name*");
        misplaced.Should().Throw<ArgumentException>().WithMessage("*last part*");
    }

    [Fact]
    public void The_action_ids_are_exactly_the_auto_switches_and_the_execution_order_holds_each_once()
    {
        ActionId.All.Select(id => id.AutoSwitch.Name).Should().Equal(ConfigKeys.All.Select(k => k.Name).Where(n => n.StartsWith("auto.", StringComparison.Ordinal)));
        ActionId.ExecutionOrder.Should().BeEquivalentTo(ActionId.All).And.OnlyHaveUniqueItems();
        ActionId.ExecutionOrder.Select(id => id.Text).Should().ContainInOrder("A5", "A4", "A6", "A7", "A8", "A9").And.ContainInOrder("A1", "A2");
    }

    [Theory]
    [InlineData("A10", true)]
    [InlineData("A5,A4", true)]
    [InlineData("A5Testcontainers", true)]
    [InlineData("a10", false)]
    [InlineData("A18", false)]
    [InlineData("A4,A4", false)]
    [InlineData("A4,", false)]
    [InlineData("", false)]
    public void An_action_list_is_known_ids_each_once(string text, bool parses)
    {
        (ActionId.Parse(text) is ActionIdList.Parsed).Should().Be(parses);
    }

    [Fact]
    public void An_unknown_id_is_refused_naming_the_legal_values()
    {
        ActionId.Parse("A99").Should().BeOfType<ActionIdList.Refused>().Which.Reason.Should().Contain("A1, A2, A3").And.Contain("A17");
    }

    [Theory]
    [InlineData("/tmp/x", "1", true, false, true)]
    [InlineData("/tmp/x", "0", false, false, false)]
    [InlineData(null, "1", false, false, false)]
    [InlineData("", "1", false, false, false)]
    [InlineData(null, null, true, false, true)]
    [InlineData(null, null, true, true, true)]
    public void Root_is_the_operating_system_s_answer_and_a_sandbox_alone_may_claim_it(string? sandbox, string? claim, bool privileged, bool windows, bool root)
    {
        ProcessPrivilege.Decide(sandbox, claim, privileged, windows).IsRoot.Should().Be(root);
    }

    [Theory]
    [InlineData("dotnet build src/x.sln", true)]
    [InlineData("/usr/share/dotnet/dotnet test", true)]
    [InlineData("docker buildx build .", true)]
    [InlineData("docker compose build", true)]
    [InlineData("npm ci", true)]
    [InlineData("node /usr/lib/node_modules/npm/bin/npm-cli.js ci", true)]
    [InlineData("dotnet /usr/share/dotnet/sdk/10.0.100/MSBuild.dll /nodemode:1", false)]
    [InlineData("dotnet exec testhost.dll", false)]
    [InlineData("docker ps", false)]
    [InlineData("npm", false)]
    public void A_build_is_docker_build_dotnet_build_or_test_or_npm_ci(string commandLine, bool build)
    {
        IdleGate.IsBuild(commandLine).Should().Be(build);
    }

    [Theory]
    [InlineData(IdleRule.Never, RunTrigger.Timer, false)]
    [InlineData(IdleRule.TimerOnly, RunTrigger.Timer, true)]
    [InlineData(IdleRule.TimerOnly, RunTrigger.Cli, false)]
    [InlineData(IdleRule.Always, RunTrigger.Manual, true)]
    public void The_idle_rule_says_which_triggers_wait(IdleRule rule, RunTrigger trigger, bool waits)
    {
        IdleGate.Applies(rule, trigger).Should().Be(waits);
    }
}
