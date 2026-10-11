using System.Text.Json.Nodes;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Archive;
using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// E10.S2c (plan §15s D8): an A13 run and an A20 run as <c>runs show &lt;runId&gt; --json</c> answers them — what the Logs page
/// renders. The built CLI cannot RUN A13 or A20 in a scenario (a test build is not root's alone: <see cref="A13PreviewGolden"/>), so
/// the daemon's own <see cref="ActionEngine"/> runs them over a scenario home: <see cref="ArchiveAction"/> and
/// <see cref="RestoreAction"/>, a manual request with <c>Execute</c>, the product binary as <c>Self</c>. Their children are answered
/// with what the REAL CLI answered in the archive scene of <see cref="GoldenContracts"/> (<see cref="ChildAnswers"/>). The engine
/// writes the run's detail and history line itself (<c>RunRecorder</c>), and the CLI's own <c>runs show</c> reads them back.
/// </summary>
internal static class ArchiveRunGoldens
{
    public const string ArchiveFile = "runs-show-archive.json";
    public const string RestoreFile = "runs-show-restore.json";

    private const string Installed = "/opt/wsl-care/bin/wsl-care";
    private const string Base = "/mnt/v/ai-archive";
    private const int Pid = 4242;
    /// <summary>When the A13 run starts; the A20 run starts an hour later — each run has its own run id (the instant and the pid).</summary>
    private static readonly DateTimeOffset At = new(2000, 1, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset RestoreAt = At.AddHours(1);

    /// <summary>The real CLI's answers in the archive scene, each the child's final JSON line, with that scene's sandbox root taken
    /// out (the paths are the distro's own, as the child prints them on a machine).</summary>
    public sealed record ChildAnswers(string Preview, string Reach, string Run, string RestorableList, string Restore, IReadOnlyList<string> RestorableIds);

    /// <summary>The synthetic entry ids, in the order the list names the real ones — the archive-list golden's own placeholder first.</summary>
    private static readonly IReadOnlyList<string> SyntheticIds = ["0123456789abcdef", "fedcba9876543210", "00000000000000aa", "00000000000000bb"];

    /// <summary>The two <c>runs show</c> answers, from a fresh home that claims root (the engine's records are root's).</summary>
    public static async Task<(ChildResult Archive, ChildResult Restore, ScenarioHome Home)> RunAsync(ChildAnswers real)
    {
        var answers = Synthetic(real);
        var home = new ScenarioHome("golden-archive-runs") { ClaimsRoot = true };
        Stage(home);
        var archiveRun = await ExecuteAsync(home, answers, new ActRequest([ActionId.Find("A13")!], RunTrigger.Manual, Execute: true));
        var restoreRun = await ExecuteAsync(home, answers, new ActRequest([ActionId.Find("A20")!], RunTrigger.Manual, Execute: true) { ShownEntries = ShownList.Of(answers.RestorableIds) });

        return (await home.RunAsync("runs", "show", archiveRun, "--json"), await home.RunAsync("runs", "show", restoreRun, "--json"), home);
    }

    /// <summary>
    /// The answers with every entry id replaced by a synthetic one (the own review's #1): an entry id hashes the SIDE's name, which is
    /// built from the machine's host name and its distro, so the real ones differ on every machine — and A20 copies them into its
    /// items, its notes and the child's <c>--entry</c> argument, where the goldens' key-based normaliser cannot reach. The mapping
    /// is applied before the engine runs, so every place the run writes an id writes the same synthetic one.
    /// </summary>
    private static ChildAnswers Synthetic(ChildAnswers real)
    {
        var ids = JsonNode.Parse(real.RestorableList)!["entries"]!.AsArray().Select(e => (string)e!["entryId"]!).Distinct(StringComparer.Ordinal).ToList();
        ids.Count.Should().BeLessThanOrEqualTo(SyntheticIds.Count, "one synthetic id per real one");
        string Mapped(string text) => ids.Select((id, i) => (id, i)).Aggregate(text, (t, p) => t.Replace(p.id, SyntheticIds[p.i], StringComparison.Ordinal));

        return real with
        {
            RestorableList = Mapped(real.RestorableList),
            Restore = Mapped(real.Restore),
            RestorableIds = [.. real.RestorableIds.Select(Mapped)],
        };
    }

    /// <summary>The distro the engine needs: the target user (uid 1000, home /home/user — the anonymised account every golden names), the boot id, runuser's PAM stack, the CPU
    /// figures, and the user's base folder.</summary>
    private static void Stage(ScenarioHome home)
    {
        var paths = (LinuxHostPaths)home.Paths;
        Write(paths, "/etc/passwd", "root:x:0:0::/root:/bin/bash\nuser:x:1000:1000::/home/user:/bin/bash\n");
        Write(paths, "/proc/sys/kernel/random/boot_id", "6d1c1c5e-0000-4000-8000-0000000000c2\n");
        Write(paths, "/etc/pam.d/runuser", "auth sufficient pam_rootok.so\nsession optional pam_keyinit.so revoke\nsession required pam_limits.so\nsession required pam_unix.so\n");
        Write(paths, "/proc/loadavg", "0.10 0.10 0.10 1/123 4567\n");
        Write(paths, "/proc/stat", "cpu  1 2 3 4\ncpu0 1 2 3 4\ncpu1 1 2 3 4\nbtime 1790948339\n");
        Directory.CreateDirectory(Path.GetDirectoryName(home.Paths.UserConfigFile)!);
        File.WriteAllText(home.Paths.UserConfigFile, $$"""{ "archive": { "baseFolder": "{{Base}}" } }""");
    }

    private static void Write(LinuxHostPaths paths, string linuxPath, string text)
    {
        var path = paths.DistroPath(linuxPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>One engine run of <paramref name="request"/>; its run id.</summary>
    private static async Task<string> ExecuteAsync(ScenarioHome home, ChildAnswers answers, ActRequest request)
    {
        // The sandbox's owner model (`LinuxSandbox`): this test process stands in for root, so the files it staged — runuser's PAM stack
        // among them — are root's to the engine, as on a machine.
        var files = new PhysicalFileSystem(home.Paths) { TrustedStateOwner = RegularFiles.EffectiveUid(), OwnersAreThisProcess = true };
        var runner = new RecordingCommandRunner { Policy = CommandPolicy.Over(CommandCatalogue.Product, Self) }
            .Script(argv => Child(argv, "preview"), RecordingCommandRunner.Exited(0, answers.Preview))
            .Script(argv => Child(argv, "reach"), RecordingCommandRunner.Exited(0, answers.Reach))
            .Script(argv => Child(argv, "list"), RecordingCommandRunner.Exited(0, answers.RestorableList));
        runner.Stream(new StreamScript([request.Ids[0].Text == "A13" ? Ours(answers.Run) : answers.Restore], RecordingCommandRunner.Exited(0)));
        var at = request.Ids[0].Text == "A13" ? At : RestoreAt;
        var processes = new FakeProcessTable().Alive(Pid, at.AddMinutes(-1));
        var context = new EngineContext(home.Paths, files, runner, new ManualTimeProvider(at), new LinuxProbe(files, (LinuxHostPaths)home.Paths, new ManualTimeProvider(at)), ConfigLoader.Load(home.Paths, files), processes, Pid, new ActionRegistry([new ArchiveAction(), new RestoreAction()]))
        {
            Self = Self,
        };

        var result = await new ActionEngine(context).ExecuteAsync(request, CancellationToken.None);

        var done = result.Should().BeOfType<ActResult.Done>($"the engine runs {request.Ids[0].Text}").Subject;
        done.Detail.Actions.Should().ContainSingle().Which.Status.Should().Be(ActionStatus.Ran, $"{request.Ids[0].Text} ran over the CLI's own answers: {done.Detail.Actions[0].Reason}");
        return done.Detail.RunId.Text;
    }

    private static SelfBinaryResult Self() => new SelfBinaryResult.Found(Installed);

    /// <summary>The CLI's run answer named by the run id ROOT gives this child (`--run-id`): A13 believes no answer naming another
    /// run (`ArchiveChildAnswers.RunIdProblem`), and the scene's CLI run was started without one. And the rate this machine measured
    /// fixed at 0, as the archive-run golden's normaliser fixes it — A13 writes it into its note, where no key-based rule reaches.</summary>
    private static string Ours(string run)
    {
        var node = JsonNode.Parse(run)!.AsObject();
        node["runId"] = RunId.New(At, Pid).Text;
        node["filesPerSecond"] = 0;
        node["megabytesPerSecond"] = 0;
        return node.ToJsonString();
    }

    private static bool Child(IReadOnlyList<string> argv, string verb) => TargetUserArgv.Parse(argv) is { Arguments: ["archive", var v, ..] } && v == verb;

    /// <summary>A child's last JSON line (the lines before it are progress), the archive scene's sandbox root taken out.</summary>
    public static string Answer(ChildResult result, string sandboxRoot)
    {
        result.Exit.Should().Be(0, result.Stderr);
        var last = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1];
        JsonNode.Parse(last).Should().NotBeNull("the child's answer is JSON");
        return last.Replace(sandboxRoot, string.Empty, StringComparison.Ordinal);
    }
}
