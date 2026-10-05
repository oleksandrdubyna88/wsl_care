using FluentAssertions;

using WslCare.Core.Agents;
using WslCare.Core.Hosting;
using WslCare.Core.Processes.Policy;

namespace WslCare.Core.Tests.Agents;

/// <summary>
/// The agent catalogue (plan §4.6, §15q E7.S1) is data — and its safety half is that every folder it names is PROTECTED: the
/// deletion policy's agent roots on both sides and the never-list's argv rule are derived from it, never typed beside it.
/// </summary>
public sealed class AgentCatalogueTests
{
    private const string Home = "/home/me";

    private static readonly WindowsEnvironment Windows = new(@"C:\Users\me", @"C:\Users\me\AppData\Roaming", @"C:\Users\me\AppData\Local", @"C:\ProgramData", @"C:\Users\me\AppData\Local\Temp");

    [Fact]
    public void Every_entry_is_complete_its_ids_unique_and_its_folders_spelt_from_a_known_root()
    {
        var agents = AgentCatalogue.Agents;

        agents.Should().HaveCountGreaterThanOrEqualTo(7).And.OnlyContain(a => a.Id.Length > 0 && a.Name.Length > 0 && a.Binaries.Count > 0);
        agents.Select(a => a.Id).Should().OnlyHaveUniqueItems();
        agents.SelectMany(a => a.Linux).Should().OnlyContain(f => f.StartsWith("~/", StringComparison.Ordinal));
        agents.SelectMany(a => a.Windows).Select(f => AgentCatalogue.WindowsFolder(f, Windows)).Should().OnlyContain(f => f.StartsWith(@"C:\Users\me\", StringComparison.Ordinal));
    }

    [Fact]
    public void A_session_layout_starts_in_one_of_its_agents_own_folders_and_only_confirmed_agents_count_sessions()
    {
        foreach (var agent in AgentCatalogue.Agents.Where(a => a.Sessions is not null))
        {
            agent.Confirmed.Should().BeTrue($"{agent.Id} counts sessions only over a confirmed layout (plan §15q D2)");
            (agent.Sessions!.LinuxUnder.Length == 0 || agent.Linux.Contains(agent.Sessions.LinuxUnder)).Should().BeTrue(agent.Id);
            (agent.Sessions.WindowsUnder.Length == 0 || agent.Windows.Contains(agent.Sessions.WindowsUnder)).Should().BeTrue(agent.Id);
        }

        AgentCatalogue.Agents.Where(a => a.Sessions is not null).Select(a => a.Id).Should().BeEquivalentTo(["claude-code", "codex", "gemini-cli", "antigravity"]);
    }

    [Fact]
    public void Every_catalogue_folder_is_a_protected_agent_root_on_its_side()
    {
        var linux = new LinuxHostPaths(new LinuxEnvironment(Home, "/etc", "/var", "/tmp", Home + "/.config"));
        var windows = new WindowsHostPaths(Windows);

        linux.AgentRoots.Should().BeEquivalentTo(AgentCatalogue.LinuxFolders(Home));
        windows.AgentRoots.Should().BeEquivalentTo(AgentCatalogue.WindowsFolders(Windows));
        linux.AgentRoots.Should().Contain(["/home/me/.claude", "/home/me/.codex", "/home/me/.gemini", "/home/me/.cache/antigravity", "/home/me/.copilot", "/home/me/.rovodev", "/home/me/.ollama"],
            "every root protected before the catalogue still is");
    }

    /// <summary>The never-list's argv rule recognises every catalogue folder — the names are derived, and the old hand-typed
    /// list is a subset of them (nothing that was protected stopped being protected).</summary>
    [Fact]
    public void The_never_list_protects_every_catalogue_folder_in_an_argument()
    {
        var unprotected = AgentCatalogue.LinuxFolders(Home).Concat(AgentCatalogue.WindowsFolders(Windows)).Where(f => !NeverList.IsProtectedPath(f)).ToList();

        unprotected.Should().BeEmpty();
        AgentCatalogue.NeverListNames.Should().Contain([".claude", ".codex", ".gemini", ".copilot", ".rovodev", ".ollama", "anthropicclaude", "antigravity", "agy"]);
        AgentCatalogue.NeverListNames.Should().NotContain("claude", "a bare claude segment is the roaming adjacency rule's, not a name");
    }

    [Theory]
    [InlineData("projects/*/*.jsonl", "projects", false)]
    [InlineData("*.jsonl", "a.jsonl", true)]
    [InlineData("session-*.jsonl", "session-1.jsonl", true)]
    [InlineData("session-*.jsonl", "session-1.json", false)]
    [InlineData("rollout-*.jsonl", "rollout-.jsonl", true)]
    [InlineData("?.db", "a.db", true)]
    [InlineData("?.db", "ab.db", false)]
    public void A_glob_name_pattern_matches_star_and_question_mark_only(string pattern, string name, bool matches) =>
        SessionGlob.Matches(pattern, name).Should().Be(matches);
}
