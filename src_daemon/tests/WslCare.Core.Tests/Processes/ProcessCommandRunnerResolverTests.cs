using FluentAssertions;

using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Worktrees;

namespace WslCare.Core.Tests.Processes;

/// <summary>
/// E19.S4: the product runner takes a resolver of its own ("which file is this bare program") WITHOUT giving up the policy — the policy
/// judges the bare name first, a refusal never reaches the resolver, and the file the resolver names is the one started.
/// </summary>
public sealed class ProcessCommandRunnerResolverTests
{
    private static readonly CommandRequest Allowed = new(GitCommands.TopLevel(), TimeSpan.FromSeconds(5));

    [Fact]
    public async Task A_refused_command_never_reaches_the_resolver()
    {
        var asked = new List<string>();
        var runner = new ProcessCommandRunner(CommandPolicy.Product, (name, _) => { asked.Add(name); return new ResolvedExecutable.NotFound("not asked"); });

        var outcome = await runner.RunAsync(new CommandRequest(["git.exe", .. GitCommands.Options, "worktree", "prune"], TimeSpan.FromSeconds(5)), CancellationToken.None);

        outcome.Should().BeOfType<CommandOutcome.Refused>().Which.Reason.Should().Contain("git-worktree-prune");
        asked.Should().BeEmpty("the never-list is asked before any file is looked for");
    }

    [Fact]
    public async Task An_allowed_command_is_resolved_by_the_given_resolver_by_its_bare_name_and_its_answer_is_what_the_runner_reports()
    {
        var asked = new List<string>();
        var runner = new ProcessCommandRunner(CommandPolicy.Product, (name, _) => { asked.Add(name); return new ResolvedExecutable.NotFound("git.exe is not started: test"); });

        var outcome = await runner.RunAsync(Allowed, CancellationToken.None);

        outcome.Should().Be(new CommandOutcome.FailedToStart("git.exe is not started: test"));
        asked.Should().Equal("git.exe");
    }

    [Fact]
    public async Task A_resolver_that_names_a_file_that_is_not_there_is_a_failed_start_not_a_search()
    {
        var missing = Path.Combine(Environment.CurrentDirectory, "testsToDelete-nothing-here", "git.exe");
        var runner = new ProcessCommandRunner(CommandPolicy.Product, (_, _) => new ResolvedExecutable.Found(missing));

        var outcome = await runner.RunAsync(Allowed, CancellationToken.None);

        outcome.Should().BeOfType<CommandOutcome.FailedToStart>().Which.Reason.Should().Contain(missing);
    }

    [Fact]
    public void A_runner_cannot_be_built_without_a_policy_or_without_a_resolver()
    {
        FluentActions.Invoking(() => new ProcessCommandRunner(null!, (_, _) => new ResolvedExecutable.NotFound("x"))).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new ProcessCommandRunner(CommandPolicy.Product, null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task The_runner_the_product_builds_resolves_git_through_the_git_executable_only()
    {
        // With no candidate git.exe the product resolution refuses by name, whatever PATH holds.
        var runner = new ProcessCommandRunner(CommandPolicy.Product, (name, token) => ProductExecutables.Resolve(name, new GitExecutable(() => [], new WindowsAdminOnlyPath()), token));

        var outcome = await runner.RunAsync(Allowed, CancellationToken.None);

        outcome.Should().BeOfType<CommandOutcome.FailedToStart>().Which.Reason.Should().Contain("never looked up on PATH");
    }
}
