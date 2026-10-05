using System.Reflection;
using System.Text.RegularExpressions;

using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Agents;
using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Actions;

/// <summary>
/// Review M8 and B2 (plan §15q R2.1, R2.2): every user-scoped action DECLARES the home folders it cleans — by delete or by a
/// command the deletion policy cannot see into — and an action whose folder overlaps an AI agent's folder (a catalogue one, or
/// a manual one, accepted or not) refuses; a manual agent's folder is protected by the run's deletion policy.
/// </summary>
public sealed partial class ActionHomeRootsTests : IDisposable
{
    /// <summary>User-scoped actions that clean no home folder, each with why.</summary>
    private static readonly IReadOnlyDictionary<string, string> NoHomeFolder = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["A3"] = "it shuts down build servers by their own command; no folder is cleaned",
    };

    private readonly LinuxSandbox _sandbox = new("home-roots");

    public void Dispose() => _sandbox.Dispose();

    [Fact]
    public void Every_user_scoped_action_declares_the_home_folders_it_cleans()
    {
        var undeclared = ActionRegistry.Product.Actions
            .Where(a => a.Scope == CommandScope.User && a.HomeRoots.Count == 0 && !NoHomeFolder.ContainsKey(a.Id.Text))
            .Select(a => a.Id.Text);

        undeclared.Should().BeEmpty("a user-home action that does not say what it cleans cannot be kept off an AI agent's folder (review M8)");
        NoHomeFolder.Keys.Should().OnlyContain(id => ActionRegistry.Product.Find(ActionId.Find(id)!)!.HomeRoots.Count == 0, "a declared exception that now declares folders is stale");
    }

    /// <summary>Every <c>CacheFolders.UnderHome(context, "…", …)</c> spelt with literals in an action's source is one of the folders
    /// that action declares — so a new folder written into the code without its declaration fails here, naming it.</summary>
    [Fact]
    public void Every_home_folder_an_action_names_in_its_source_is_declared()
    {
        var undeclared = new List<string>();
        foreach (var action in ActionRegistry.Product.Actions.Where(a => a.HomeRoots.Count > 0))
        {
            var declared = action.HomeRoots.Select(r => r.Display).ToHashSet(StringComparer.Ordinal);
            undeclared.AddRange(Spelt(SourceOf(action)).Where(f => !declared.Contains(f)).Select(f => $"{action.Id.Text}: {f}"));
        }

        undeclared.Should().BeEmpty();
    }

    /// <summary>The companion: the source scan finds the folders A8 and A12 spell — a scan that finds nothing would pass forever.</summary>
    [Fact]
    public void The_source_scan_finds_the_folders_the_actions_spell()
    {
        Spelt(SourceOf(ActionRegistry.Product.Find(ActionId.Find("A12")!)!)).Should().Contain(["~/.cache/ms-playwright", "~/.local/share/NuGet/http-cache"]);
        Spelt("CacheFolders.UnderHome(context, \".planted\", \"folder\")").Should().Equal("~/.planted/folder");
    }

    [Fact]
    public void No_catalogue_agent_folder_overlaps_a_folder_an_action_cleans()
    {
        var home = _sandbox.Paths.Home;
        var cleaned = ExtraAgentRules.CleanupRoots(ActionRegistry.Product, home, PathRules.ForThisOs);

        var clashes = from agent in AgentCatalogue.LinuxFolders(home)
                      from root in cleaned
                      where ExtraAgentRules.Overlaps(Path.GetFullPath(agent), Path.GetFullPath(root.Path))
                      select $"{agent} ~ {root.Whose}";

        clashes.Should().BeEmpty();
    }

    /// <summary>Review B2: an action whose cleanup folder overlaps an AI agent's folder — here a manual agent's, inside A12's
    /// Playwright folder — refuses with the overlap named, and never runs.</summary>
    [Fact]
    public async Task An_action_whose_folder_overlaps_an_agent_folder_refuses_and_does_not_run()
    {
        _sandbox.Write("/etc/passwd", "root:x:0:0::/root:/bin/bash\nme:x:1000:1000::/home/me:/bin/bash\n");
        _sandbox.Load(0.1, 0.1, 0.1, cpus: 4);
        var journal = new List<string>();
        var a12 = new ScriptedAction("A12", journal) { Scope = CommandScope.User, HomeRoots = [HomeFolder.Of(".cache", "ms-playwright")] };
        var paths = _sandbox.Paths.WithExtraAgentRoots([_sandbox.Paths.DistroPath("/home/me/.cache/ms-playwright/agent-data")]);
        var clock = new FixedTimeProvider();
        var context = new EngineContext(paths, _sandbox.Files, new RecordingCommandRunner { Policy = CommandPolicy.Product }, clock, new LinuxProbe(_sandbox.Files, paths, clock),
            ConfigLoader.Load(paths, _sandbox.Files), new FakeProcessTable().Alive(4242, FixedTimeProvider.DefaultNow.AddMinutes(-1)), 4242, new ActionRegistry([a12]));

        var result = await new ActionEngine(context).ExecuteAsync(new ActRequest([ActionId.Find("A12")!], RunTrigger.Cli, Execute: true), CancellationToken.None);

        var outcome = result.Should().BeOfType<ActResult.Done>().Subject.Detail.Actions.Single();
        outcome.Status.Should().Be(ActionStatus.Refused);
        outcome.Reason.Should().Contain("~/.cache/ms-playwright overlaps the AI agent folder").And.Contain("agent-data");
        journal.Should().NotContain("run A12");
    }

    /// <summary>Review B2 / M1: the run's deletion policy holds a manual agent's folder — a delete under it is refused, the same
    /// delete without the extra is not.</summary>
    [Fact]
    public void The_deletion_policy_built_over_the_extras_refuses_a_delete_under_a_manual_agent_folder()
    {
        var inside = _sandbox.Write("/home/me/.cache/ms-playwright/agent-data/x/file.bin", "x");
        var folder = Path.GetDirectoryName(inside)!;
        var root = _sandbox.Paths.DistroPath("/home/me/.cache/ms-playwright");
        var withExtra = new PhysicalFileSystem(_sandbox.Paths.WithExtraAgentRoots([_sandbox.Paths.DistroPath("/home/me/.cache/ms-playwright/agent-data")])) { OwnersAreThisProcess = true };

        withExtra.DeleteDirectory(folder, new DeletionScope(root, "A12")).Should().BeOfType<DeletionVerdict.Refused>();
        Directory.Exists(folder).Should().BeTrue();
        _sandbox.Files.DeleteDirectory(folder, new DeletionScope(root, "A12")).Should().NotBeOfType<DeletionVerdict.Refused>("without the extra the same delete is allowed — the refusal above is the extra's");
    }

    private static string SourceOf(ICleanupAction action)
    {
        var root = typeof(ActionHomeRootsTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "WslCare.SourceRoot").Value!;
        var file = Directory.EnumerateFiles(root, action.GetType().Name + ".cs", SearchOption.AllDirectories).Single(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));
        return File.ReadAllText(file);
    }

    private static IEnumerable<string> Spelt(string source) =>
        UnderHomeCall().Matches(source).Select(m => "~/" + string.Join('/', Literal().Matches(m.Groups["args"].Value).Select(l => l.Groups["s"].Value)));

    [GeneratedRegex(@"CacheFolders\s*\.\s*UnderHome\s*\(\s*context\s*,(?<args>(\s*""[^""]*""\s*,?)+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex UnderHomeCall();

    [GeneratedRegex(@"""(?<s>[^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex Literal();
}
