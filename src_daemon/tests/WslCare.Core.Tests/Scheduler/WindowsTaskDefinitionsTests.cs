using System.Text;
using System.Xml.Linq;

using FluentAssertions;

using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Scheduler;

namespace WslCare.Core.Tests.Scheduler;

/// <summary>
/// WR-D1 (PLAN_windows_runner.md §3.4, §6): the two task XMLs are a pure function of the configuration, the user's SID and the
/// staged exe's path — the principal is the user (InteractiveToken, LeastPrivilege), the action is the ABSOLUTE staged path with
/// <c>tasks run &lt;name&gt; --json</c>, the cadence and the deadline come from their keys, and the summary lines are the shape the
/// extension's <c>SUMMARY_FUNCTION</c> prints over Task Scheduler's read-back.
/// </summary>
public sealed class WindowsTaskDefinitionsTests
{
    private const string Sid = "S-1-5-21-1000000000-2000000000-3000000000-1001";

    private const string Digest = "a14ad2eb0c6f1d9e8b7a6c5d4e3f2a1b0c9d8e7f6a5b4c3d2e1f0a9b8c7d6e5f";

    private static readonly string Staged = $"C:\\Users\\user\\AppData\\Local\\wsl-care\\bin\\{Digest}\\wsl-care.exe";

    private static readonly ConfigLayerFile Machine = new(ConfigLayer.Machine, "C:\\ProgramData\\wsl-care\\config.json");

    private static readonly ConfigLayerFile User = new(ConfigLayer.User, "C:\\Users\\user\\AppData\\Roaming\\wsl-care\\config.json");

    private static EffectiveConfig Config(string machine = "{}", string user = "{}")
    {
        var loaded = ConfigLoader.Load(
        [
            (ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults())),
            (Machine, new FileReadResult.Content(Encoding.UTF8.GetBytes(machine))),
            (User, new FileReadResult.Content(Encoding.UTF8.GetBytes(user))),
        ]);
        loaded.IsObserveOnly.Should().BeFalse("the fixture configuration must be one the loader takes: {0}", string.Join("; ", loaded.Errors.Select(e => e.Display)));
        loaded.Notices.Should().BeEmpty("every fixture value must be taken, or the test asserts the default instead");
        return loaded.Config;
    }

    [Fact]
    public void The_render_names_the_user_sid_and_the_staged_path()
    {
        var definition = WindowsTaskDefinitions.Render(WindowsTask.Watch, Config(), new TaskIdentity(Sid, Staged));

        definition.Xml.Should().Contain($"<Principal id=\"Author\"><UserId>{Sid}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal>")
            .And.Contain($"<Exec><Command>{Staged}</Command><Arguments>tasks run watch --json</Arguments></Exec>")
            .And.Contain("<Actions Context=\"Author\">");
        var trigger = Element(definition.Xml, "LogonTrigger");
        trigger.Element(Ns + "UserId")!.Value.Should().Be(Sid, "an unelevated user may register a logon trigger for their OWN account only");
        definition.Summary.Should().Equal(
            $"principal={Sid} logon=3 level=0",
            "trigger=logon enabled=True delay=PT60S interval=PT5M",
            "settings limit=PT20M instances=2 whenAvailable=True battery=False enabled=True",
            $"action type=0 path={Staged} args=tasks run watch --json");
    }

    [Fact]
    public void The_watch_task_repeats_every_period_minutes_and_the_archive_every_n_hours()
    {
        var config = Config(
            machine: """{ "mcpWatchdog": { "periodMinutes": 7, "runLimitMinutes": 12 }, "timer": { "runLimitMinutes": 300 } }""",
            user: """{ "windowsTasks": { "watchLogonDelaySeconds": 90, "archiveLogonDelayMinutes": 0, "archiveEveryHours": 6 } }""");

        var watch = WindowsTaskDefinitions.Render(WindowsTask.Watch, config, new TaskIdentity(Sid, Staged));
        var archive = WindowsTaskDefinitions.Render(WindowsTask.Archive, config, new TaskIdentity(Sid, Staged));

        Element(watch.Xml, "Interval").Value.Should().Be("PT7M");
        Element(watch.Xml, "Delay").Value.Should().Be("PT90S");
        Element(watch.Xml, "ExecutionTimeLimit").Value.Should().Be("PT22M", "the deadline (mcpWatchdog.runLimitMinutes) plus the run margin");
        watch.Summary.Should().Contain(["trigger=logon enabled=True delay=PT90S interval=PT7M", "settings limit=PT22M instances=2 whenAvailable=True battery=False enabled=True"]);
        watch.Numbers.Should().Equal(
            new TaskNumber("windowsTasks.watchLogonDelaySeconds", 90), new TaskNumber("mcpWatchdog.periodMinutes", 7), new TaskNumber("mcpWatchdog.runLimitMinutes", 12));
        watch.ExecutionTimeLimitMinutes.Should().Be(12 + NumberRules.RunMarginMinutes);

        Element(archive.Xml, "Interval").Value.Should().Be("PT6H");
        Doc(archive.Xml).Descendants(Ns + "Delay").Should().BeEmpty("a delay of 0 is no Delay element, as the guard's task writes it");
        Element(archive.Xml, "ExecutionTimeLimit").Value.Should().Be("PT310M", "the deadline (timer.runLimitMinutes) plus the run margin");
        Element(archive.Xml, "Arguments").Value.Should().Be("tasks run archive --json");
        archive.Summary.Should().Contain(["trigger=logon enabled=True delay= interval=PT6H", "settings limit=PT310M instances=2 whenAvailable=True battery=False enabled=True"]);
        archive.Numbers.Should().Equal(
            new TaskNumber("windowsTasks.archiveLogonDelayMinutes", 0), new TaskNumber("windowsTasks.archiveEveryHours", 6), new TaskNumber("timer.runLimitMinutes", 300));
    }

    [Fact]
    public void The_archive_task_starts_at_logon_plus_its_delay_by_default()
    {
        var archive = WindowsTaskDefinitions.Render(WindowsTask.Archive, Config(), new TaskIdentity(Sid, Staged));

        Element(archive.Xml, "Delay").Value.Should().Be("PT20M");
        Element(archive.Xml, "Interval").Value.Should().Be("PT4H");
        archive.Summary[1].Should().Be("trigger=logon enabled=True delay=PT20M interval=PT4H");
    }

    /// <summary>§3.4: the settings every task carries — one at a time, started when it was missed, never stopped by a battery,
    /// never woken for, never only when idle, below normal priority (the family's local-CPU-budget rule).</summary>
    [Fact]
    public void Both_tasks_carry_the_plan_s_settings_and_are_well_formed_task_xml()
    {
        foreach (var task in WindowsTask.All)
        {
            var settings = Element(WindowsTaskDefinitions.Render(task, Config(), new TaskIdentity(Sid, Staged)).Xml, "Settings");

            Value(settings, "MultipleInstancesPolicy").Should().Be("IgnoreNew");
            Value(settings, "StartWhenAvailable").Should().Be("true");
            Value(settings, "DisallowStartIfOnBatteries").Should().Be("false");
            Value(settings, "StopIfGoingOnBatteries").Should().Be("false");
            Value(settings, "WakeToRun").Should().Be("false");
            Value(settings, "RunOnlyIfIdle").Should().Be("false");
            Value(settings, "Hidden").Should().Be("false");
            Value(settings, "Priority").Should().Be("7");
            Value(settings, "Enabled").Should().Be("true");
        }
    }

    /// <summary>A path or a SID is element TEXT: escaped in the XML, said as it is in the summary.</summary>
    [Fact]
    public void A_path_with_xml_characters_is_escaped_in_the_xml_and_plain_in_the_summary()
    {
        const string odd = "C:\\Users\\user\\R&D <lab>\\AppData\\Local\\wsl-care\\bin\\x\\wsl-care.exe";

        var definition = WindowsTaskDefinitions.Render(WindowsTask.Watch, Config(), new TaskIdentity(Sid, odd));

        definition.Xml.Should().Contain("<Command>C:\\Users\\user\\R&amp;D &lt;lab&gt;\\AppData");
        Element(definition.Xml, "Command").Value.Should().Be(odd);
        definition.Summary[^1].Should().Be($"action type=0 path={odd} args=tasks run watch --json");
    }

    [Fact]
    public void The_closed_set_of_tasks_is_watch_and_archive()
    {
        WindowsTask.All.Select(t => t.TaskPath).Should().Equal("\\wsl-care\\watch", "\\wsl-care\\archive");
        WindowsTask.Find("watch").Should().Be(WindowsTask.Watch);
        WindowsTask.Find("Watch").Should().BeNull("a task name is matched exactly");
        WindowsTask.Find("collect").Should().BeNull("\\wsl-care\\collect is E11's, not this plan's");
    }

    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static XDocument Doc(string xml) => XDocument.Parse(xml);

    private static XElement Element(string xml, string name) => Doc(xml).Descendants(Ns + name).Single();

    private static string Value(XElement parent, string name) => parent.Element(Ns + name)!.Value;
}
