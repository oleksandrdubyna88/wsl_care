using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Agents;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Agents;

/// <summary>
/// The manual AI agents in discovery and the walk (plan §15q D4, R2): an accepted entry is walked like a catalogue agent —
/// <c>memory</c> never entered, its own session glob (with <c>**</c>) counted by listing; a refused one stays in the answer with
/// its refusal and nothing under it is entered; and the entry's <c>cli</c> is never a file-system argument.
/// </summary>
public sealed class ExtraAgentsTests : IDisposable
{
    private const string Cli = "/home/me/.local/bin/mycli";

    private readonly LinuxSandbox _sandbox = new("extra-agents");

    public void Dispose() => _sandbox.Dispose();

    private EffectiveConfig Config(string extras)
    {
        _sandbox.Write("/home/me/.config/wsl-care/config.json", $$"""{ "aiAgents": { "extra": {{extras}} } }""");
        var loaded = ConfigLoader.Load(_sandbox.Paths, _sandbox.Files);
        loaded.Errors.Should().BeEmpty();
        return loaded.Config;
    }

    private static string Entry(string name, string folder, string glob = "") =>
        $$"""{ "cli": "{{Cli}}", "side": "wsl", "name": "{{name}}", "dataFolders": ["{{folder}}"], "sessionGlob": "{{glob}}" }""";

    private AgentsSample Walk(IFileSystem files, EffectiveConfig config) =>
        new AgentWalk(files, new FixedTimeProvider()).Measure(
            [.. ExtraAgents.Discover(_sandbox.Paths, files, config, ActionRegistry.Product).Select(p => p.Target)], AgentWalk.CollectBudget, withNames: true, CancellationToken.None);

    [Fact]
    public void An_accepted_manual_agent_is_walked_without_memory_and_its_sessions_counted_by_its_own_glob()
    {
        _sandbox.Sized("/home/me/.mycli/sessions/2026/a.log", 100, FixedTimeProvider.DefaultNow);
        _sandbox.Sized("/home/me/.mycli/sessions/b.log", 50, FixedTimeProvider.DefaultNow);
        _sandbox.Sized("/home/me/.mycli/memory/m.log", 9_000, FixedTimeProvider.DefaultNow);

        var size = Walk(_sandbox.Files, Config($"[{Entry("mycli", "/home/me/.mycli", "sessions/**")}]")).Find("manual:mycli")!;

        size.TotalBytes.Should().Be(150, "memory/ is never entered for ANY agent, a manual one too (plan §15q H2)");
        size.Sessions.Count.Should().Be(2, "** matches the folder it is in and every folder below it");
    }

    [Fact]
    public void A_refused_manual_agent_stays_in_the_answer_with_its_refusal_and_is_not_walked()
    {
        _sandbox.Sized("/home/me/.npm/_cacache/big.bin", 5_000, FixedTimeProvider.DefaultNow);
        var config = Config($"[{Entry("npm-ish", "/home/me/.npm")}]");

        var found = ExtraAgents.Discover(_sandbox.Paths, _sandbox.Files, config, ActionRegistry.Product).Single();
        var size = Walk(_sandbox.Files, config).Find("manual:npm-ish")!;

        found.DetectedBy.Should().Equal(ExtraAgents.Manual);
        found.Refusal.Should().Contain("A8's cleanup folder ~/.npm");
        size.TotalBytes.Should().Be(0);
        size.Folders.Single().Reason.Should().StartWith("not walked:").And.Contain("A8's cleanup folder");
        size.Sessions.Counted.Should().BeFalse();
    }

    /// <summary>Plan §15q D4: root reads only the entry's folders and glob — the CLI's path is never handed to the file system,
    /// not by discovery, not by the walk (a recording double sees every path asked).</summary>
    [Fact]
    public void The_cli_of_a_manual_agent_is_never_a_file_system_argument()
    {
        _sandbox.Executable("/home/me/.local/bin", "mycli");
        _sandbox.Sized("/home/me/.mycli/s.log", 10, FixedTimeProvider.DefaultNow);
        var recording = new RecordingPaths(_sandbox.Files);

        Walk(recording, Config($"[{Entry("mycli", "/home/me/.mycli", "*")}]")).Find("manual:mycli")!.TotalBytes.Should().Be(10);

        recording.Asked.Should().NotBeEmpty("the double did see the walk");
        recording.Asked.Should().NotContain(p => p.Replace('\\', '/').Contains(".local/bin/mycli", StringComparison.Ordinal));
    }

    [Fact]
    public void A_windows_entry_is_neither_listed_nor_walked_by_the_distros_binary()
    {
        var config = Config("""[{ "cli": "C:\\x.exe", "side": "windows", "name": "win", "dataFolders": ["C:\\Users\\me\\.x"], "sessionGlob": "" }]""");

        ExtraAgents.Discover(_sandbox.Paths, _sandbox.Files, config, ActionRegistry.Product).Should().BeEmpty("a Windows agent is the Windows binary's (E7.S5b)");
    }

    /// <summary>Every path the file system is asked about, by any read the walk or discovery may use.</summary>
    private sealed class RecordingPaths(IFileSystem inner) : DelegatingFileSystem(inner)
    {
        public List<string> Asked { get; } = [];

        private T Note<T>(string path, Func<T> answer)
        {
            Asked.Add(path);
            return answer();
        }

        public override bool FileExists(string path) => Note(path, () => base.FileExists(path));

        public override bool DirectoryExists(string path) => Note(path, () => base.DirectoryExists(path));

        public override LinkReadResult ReadLink(string path) => Note(path, () => base.ReadLink(path));

        public override RealPathResult ResolvePath(string path) => Note(path, () => base.ResolvePath(path));

        public override (uint Major, uint Minor)? DeviceOf(string path) => Note(path, () => base.DeviceOf(path));

        public override IReadOnlyList<FileEntry> ListEntries(string path) => Note(path, () => base.ListEntries(path));

        public override TreeMeasure WalkTree(string path, TreeLimits limits, TreeRules rules, CancellationToken cancellationToken) =>
            Note(path, () => base.WalkTree(path, limits, rules, cancellationToken));

        public override FileReadResult ReadRegularFile(string path, int maxBytes) => Note(path, () => base.ReadRegularFile(path, maxBytes));

        public override FileReadResult ReadFile(string path) => Note(path, () => base.ReadFile(path));
    }
}
