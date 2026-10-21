using FluentAssertions;

using WslCare.Core.Actions.BuildServers;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Actions.Suspects;
using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Files;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Processes.Redaction;

/// <summary>E15.S7 §1, road R8: three matchers read the SHOWN command line as input — <c>AgentOrphans.Script</c> (the word after the
/// program when it starts with <c>/</c>), <c>IdleGate.IsBuild</c> and <c>BuildServerShutdown.IsDotnetBuild</c> (a split on spaces).
/// The widened rule must keep paths and program words intact, or they regress: their answers over the captured tree's 51 processes
/// are frozen in <c>fixtures/redaction/matchers-before-e15s7.txt</c>, taken on <c>a432e970</c> with the OLD rule.</summary>
public sealed class MatchersOverTheShownLineTests
{
    private static IReadOnlyList<ProcessEntry> Processes()
    {
        var paths = ProcfsFixture.PathsAt(ProcfsFixture.Root);
        var files = ProcfsFixture.LinkOverlay(new PhysicalFileSystem(paths), ProcfsFixture.Root);
        var containers = ContainerCgroups.Read(files, paths.CgroupRoot).ValueOr(new ContainerSet([]));
        return new ProcessCollector(files, paths, new FixedTimeProvider(ProcfsFixture.CapturedAt))
            .Read(Reading.Of(new KernelFacts(100, 4096)), containers, CancellationToken.None)
            .Should().BeOfType<Reading<ProcessSnapshot>.Available>().Subject.Value.All;
    }

    private static string Line(ProcessEntry p) =>
        string.Join('\t', p.Pid, AgentOrphans.Script(p), IdleGate.IsBuild(p.CommandLine), BuildServerShutdown.IsDotnetBuild(p.CommandLine));

    [Fact]
    public void The_three_matchers_answer_over_the_captured_tree_what_they_answered_before_the_rule_was_widened()
    {
        var before = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "fixtures", "redaction", "matchers-before-e15s7.txt")).Where(l => l.Length > 0 && l[0] != '#').ToList();

        var now = Processes().OrderBy(p => p.Pid).Select(Line).ToList();

        now.Should().HaveCount(51).And.Equal(before, "the matchers read paths and program words, which the widened rule leaves whole");
    }

    [Theory]
    [InlineData(new[] { "dotnet", "build", "/home/user/git/project-a/x.sln" }, true)]
    [InlineData(new[] { "dotnet", "test", "--no-build", "/home/user/git/project-a/tests" }, true)]
    [InlineData(new[] { "/usr/bin/docker", "buildx", "build", "-t", "image:latest", "." }, true)]
    [InlineData(new[] { "npm", "ci" }, true)]
    [InlineData(new[] { "/usr/share/dotnet/dotnet", "/usr/share/dotnet/sdk/10.0.100/MSBuild.dll", "/nodeReuse:true" }, false)]
    [InlineData(new[] { "node", "/home/user/git/project-a/server.js", "--port=80" }, false)]
    public void A_build_command_line_is_still_recognised_after_the_widened_rule_has_shown_it(string[] argv, bool isBuild)
    {
        var shown = WslCare.Core.Processes.Redaction.CommandLineRedaction.Shown(argv, RedactionCases.Limits);

        IdleGate.IsBuild(shown).Should().Be(isBuild, shown);
        BuildServerShutdown.IsDotnetBuild(shown).Should().Be(argv[0].EndsWith("dotnet", StringComparison.Ordinal) && argv[1] is "build" or "test", shown);
    }
}
