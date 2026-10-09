using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Archive;
using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Processes;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// Plan §15r E9.S4 own review round C-8: A13 and A20 driven by root's side IN this process, their children the BUILT CLI run as this
/// (non-root) account — the real answers and the real exit codes, never ones a test scripted by hand. The command root would start
/// (<c>runuser -u me -- &lt;wsl-care&gt; archive …</c>) is run as the words after the binary; nothing else stands in.
/// </summary>
public sealed class ArchiveRoundTripFlows
{
    private const string Installed = "/opt/wsl-care/bin/wsl-care";
    private const string Main = "/home/me/.claude/projects/p/s1.jsonl";

    /// <summary>preview → the modal's entry ids → run with <c>--entry</c> → the built child restores → A20's measured result.</summary>
    [Fact]
    public async Task A20_restores_what_its_preview_showed_through_the_built_cli()
    {
        using var home = await ArchiveRunFlows.Archived("a20-round-trip");
        (await home.RunAsync("archive", "run", "--agent", "claude-code", "--json")).Exit.Should().Be(ArchiveExits.Ok);
        ArchiveRunFlows.ADayLater(home);
        (await home.RunAsync("archive", "run", "--agent", "claude-code", "--json")).Exit.Should().Be(ArchiveExits.Ok);
        var paths = (LinuxHostPaths)home.Paths;
        File.Exists(paths.DistroPath(Main)).Should().BeFalse("the second run removed the session at its source");
        var action = new RestoreAction();
        var runner = new BuiltCli(home);

        var shown = action.Shown(await action.PreviewAsync(Root(home), Commands(action, runner, Root(home)), CancellationToken.None));
        var context = Root(home) with { ShownEntries = ShownList.Of(shown) };
        var commands = Commands(action, runner, context);
        var preview = await action.PreviewAsync(context, commands, CancellationToken.None);
        var run = await action.RunAsync(context, preview, commands, CancellationToken.None);

        shown.Should().ContainSingle();
        run.Succeeded.Should().BeTrue(run.Failure);
        run.Count.Should().Be(1);
        File.ReadAllText(paths.DistroPath(Main)).Should().Be("the transcript");
    }

    /// <summary>preview → reach → run, the built child moving a due session; then — the side's lock held, as by the user's own run — the
    /// reach answers busy with the CLI's own exit (75), and A13 does nothing this time and says so (C-1).</summary>
    [Fact]
    public async Task A13_archives_through_the_built_cli_and_does_nothing_on_a_busy_side()
    {
        using var home = await ArchiveRunFlows.Archived("a13-round-trip");
        var action = new ArchiveAction();
        var runner = new BuiltCli(home);

        var (preview, moved) = await PreviewAndRun(action, home, runner);
        var lockFile = new ArchiveState(home.Paths, Files(home)).LockFile;
        Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
        ActionRun busy;
        using (new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            (_, busy) = await PreviewAndRun(action, home, runner, previewFirst: false);
        }

        preview.Available.Should().BeTrue(preview.Reason);
        preview.Count.Should().Be(1, "the child's own preview names the one due session");
        moved.Succeeded.Should().BeTrue(moved.Failure);
        moved.Removed.Should().Contain(agent => agent.Name == "claude-code" && agent.Note.StartsWith("copied 1 session(s)", StringComparison.Ordinal));
        moved.Count.Should().BePositive();
        busy.Succeeded.Should().BeTrue(busy.Failure);
        busy.Count.Should().Be(0);
        busy.FreedBasis.Should().Contain("holds its lock");
        runner.Exits.Should().Contain(ArchiveExits.Busy, "the reach child answered with the command line's own busy exit");
    }

    private static async Task<(ActionPreview Preview, ActionRun Run)> PreviewAndRun(ArchiveAction action, ScenarioHome home, BuiltCli runner, bool previewFirst = true)
    {
        var context = Root(home);
        var commands = Commands(action, runner, context);
        var preview = previewFirst ? await action.PreviewAsync(context, commands, CancellationToken.None) : ActionPreview.Of("archive", 1, 0, "the earlier preview", new Dictionary<string, long>(), string.Empty, []);
        return (preview, await action.RunAsync(context, preview, commands, CancellationToken.None));
    }

    private static PhysicalFileSystem Files(ScenarioHome home) =>
        new(home.Paths) { TrustedStateOwner = RegularFiles.EffectiveUid(), OwnersAreThisProcess = true };

    /// <summary>Root's side over the scenario's tree: its own state, the target user <c>me</c>, Ubuntu's runuser stack.</summary>
    private static ActionContext Root(ScenarioHome home)
    {
        var paths = (LinuxHostPaths)home.Paths;
        var stack = paths.DistroPath("/etc/pam.d/runuser");
        Directory.CreateDirectory(Path.GetDirectoryName(stack)!);
        File.WriteAllText(stack, "auth sufficient pam_rootok.so\nsession required pam_unix.so\n");
        var files = Files(home);
        return new ActionContext(paths, files, TimeProvider.System, ConfigLoader.Load(paths, files).Config, RunTrigger.Manual, new TargetUserResult.Found(new TargetUser("me", 1000, paths.Home), "test"))
        {
            Processes = _ => Reading.Of(new ProcessSnapshot(0, 0, 0, 0, 0, [], [])),
            RunId = RunId.New(DateTimeOffset.UtcNow, Environment.ProcessId).Text,
            RunStarted = DateTimeOffset.UtcNow,
        };
    }

    private static ActionCommands Commands(ICleanupAction action, ICommandRunner runner, ActionContext context) =>
        new(action, runner, context.TargetUser, []) { Self = () => new SelfBinaryResult.Found(Installed) };

    /// <summary>Runs what root asks for — <c>runuser -u me -- &lt;wsl-care&gt; archive …</c> — as the built CLI's <c>archive …</c>, as
    /// this account, and answers with its real stdout and exit; a streamed child's lines are handed over one by one.</summary>
    private sealed class BuiltCli(ScenarioHome home) : ICommandRunner
    {
        private readonly List<int> _exits = [];

        public IReadOnlyList<int> Exits => [.. _exits];

        public async Task<CommandOutcome> RunAsync(CommandRequest request, CancellationToken cancellationToken)
        {
            var result = await Child(request);
            return new CommandOutcome.Exited(result.Exit, new CapturedText(result.Stdout, false), new CapturedText(result.Stderr, false), TimeSpan.Zero);
        }

        public async Task<CommandOutcome> StreamAsync(CommandRequest request, Action<string> onStdoutLine, CancellationToken cancellationToken)
        {
            var result = await Child(request);
            foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                onStdoutLine(line.TrimEnd('\r'));
            }

            return new CommandOutcome.Exited(result.Exit, CapturedText.Empty, new CapturedText(result.Stderr, false), TimeSpan.Zero);
        }

        private async Task<ChildResult> Child(CommandRequest request)
        {
            request.Argv.Take(5).Should().Equal(["runuser", "-u", "me", "--", Installed], "root starts only the product's own binary, as the user");
            var result = await home.RunAsync([.. request.Argv.Skip(5)]);
            _exits.Add(result.Exit);
            return result;
        }
    }
}
