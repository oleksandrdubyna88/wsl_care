using System.Security.Principal;

using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Actions.WindowsTemp;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Hosting;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Collect;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Actions.WindowsTemp;

/// <summary>
/// WR-T1 on the Windows legs: A23 through the REAL action engine, the real file system and the real deletion policy — over a
/// sandbox whose <c>%TEMP%</c> lies under a <see cref="TempRoot"/> (<c>WindowsEnvironment.Sandboxed</c>). The real <c>%TEMP%</c> is
/// never resolved: every path the product sees comes from the sandbox layout, and a test asserts it.
/// </summary>
public sealed class WindowsTempEngineTests : IDisposable
{
    private const int Pid = 4242;
    private const string WindowsOnly = "the real Windows file system, owner and reparse points: the Windows legs";

    private readonly SandboxHost _host = new("temp-engine");
    private readonly ManualTimeProvider _clock = new(DateTimeOffset.UtcNow);

    public void Dispose() => _host.Dispose();

    private string Temp => _host.Paths.TempDirectory;

    private string Make(string relative, int daysOld, params (string File, int Days)[] files)
    {
        var folder = Path.Combine(Temp, relative);
        Directory.CreateDirectory(folder);
        foreach (var (file, days) in files)
        {
            var path = Path.Combine(folder, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "scratch");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-days));
        }

        foreach (var dir in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).Append(folder))
        {
            Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow.AddDays(-daysOld));
        }

        return folder;
    }

    private static string RealSid => OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().User!.Value : string.Empty;

    private ActionEngine Engine(IFileSystem? files = null, string? sid = null, string userConfig = "{}")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_host.Paths.UserConfigFile)!);
        File.WriteAllText(_host.Paths.UserConfigFile, userConfig);
        var fs = files ?? _host.Files;
        return new ActionEngine(new EngineContext(_host.Paths, fs, new RecordingCommandRunner { Policy = CommandPolicy.Product }, _clock, new FakeProbe(HostSide.Windows, _clock),
            ConfigLoader.Load(_host.Paths, fs), new FakeProcessTable().Alive(Pid, DateTimeOffset.UtcNow.AddMinutes(-1)), Pid, new ActionRegistry([new WindowsTempAction()]))
        {
            UserSid = sid ?? RealSid,
        });
    }

    private static ActRequest Run(RunTrigger trigger) => new([ActionId.Find("A23")!], trigger, Execute: true);

    private static ActResult.Done Done(ActResult result) => result.Should().BeOfType<ActResult.Done>().Subject;

    [Fact]
    public void The_root_the_product_sees_is_the_sandbox_and_never_the_real_temp_folder()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        var root = TempRoots.Resolve(_host.Paths).Should().BeOfType<TempRootResult.Resolved>().Subject.Root;

        root.Should().StartWith(_host.Root.Path);
        root.Should().NotBe(Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\'));
    }

    [Fact]
    public async Task A_run_deletes_the_old_allowed_tree_and_keeps_the_young_one_and_records_itself()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var old = Make("coai-old", 30, ("a.txt", 30), (@"sub\b.txt", 30));
        var young = Make("coai-young", 30, ("fresh.txt", 0));
        var other = Make("sometool-old", 30, ("x.txt", 30));

        var done = Done(await Engine().ExecuteAsync(Run(RunTrigger.Cli), TestContext.Current.CancellationToken));

        done.Detail.Actions.Should().ContainSingle().Which.Status.Should().Be(ActionStatus.Ran);
        Directory.Exists(old).Should().BeFalse();
        Directory.Exists(young).Should().BeTrue("something inside it is new");
        Directory.Exists(other).Should().BeTrue("not on the allow-list");
        done.Recording.Should().Be(Recording.Recorded);
        done.Detail.Side.Should().Be("windows");
    }

    [Fact]
    public async Task While_dry_the_run_deletes_nothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var old = Make("coai-old", 30, ("a.txt", 30));

        var done = Done(await Engine(userConfig: """{ "dryRun": false }""").ExecuteAsync(Run(RunTrigger.Timer), TestContext.Current.CancellationToken));

        done.Detail.DryRun.Should().BeTrue("the first week of the timer is dry whatever dryRun says");
        done.Detail.Actions.Should().ContainSingle().Which.Status.Should().Be(ActionStatus.DryRun);
        Directory.Exists(old).Should().BeTrue();
    }

    [Fact]
    public async Task An_interrupted_run_reads_interrupted_in_the_engine_s_record_not_completed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        Make("coai-a", 30, ("one.txt", 30), ("two.txt", 30));
        var other = Make("coai-b", 30, ("x.txt", 30));
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Engine(new CancelAfterFirstDelete(_host.Files, cts)).ExecuteAsync(Run(RunTrigger.Cli), cts.Token));

        var line = RunHistory.Read(_host.Paths, _host.Files).Records.Should().ContainSingle().Subject;
        line.Outcome.Should().Be(RunOutcome.Interrupted, "the engine records the cut-off run before it rethrows");
        line.Actions.Should().ContainSingle().Which.Status.Should().Be(ActionStatus.Interrupted);
        Directory.Exists(other).Should().BeTrue("nothing after the signal was touched");
        TempCursors.Read(_host.Paths, _host.Files).InProgress.Name.Should().Be("coai-a");
    }

    [Fact]
    public async Task A_junction_inside_a_tree_is_retained_and_its_target_is_untouched()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var outside = Path.Combine(_host.Root.Path, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "precious.txt"), "keep");
        var tree = Make("coai-j", 30, ("a.txt", 30));
        Assert.SkipUnless(DirectoryLinks.TryCreate(Path.Combine(tree, "link"), outside), "this machine cannot make a directory link");
        var linked = Make("coai-direct", 30, ("a.txt", 30));
        Directory.Delete(linked, recursive: true);
        Assert.SkipUnless(DirectoryLinks.TryCreate(linked, outside), "this machine cannot make a directory link");

        await Engine().ExecuteAsync(Run(RunTrigger.Cli), TestContext.Current.CancellationToken);

        File.Exists(Path.Combine(outside, "precious.txt")).Should().BeTrue("a reparse point is never followed");
        Directory.Exists(tree).Should().BeTrue("a tree that holds a reparse point is retained");
        Directory.Exists(linked).Should().BeTrue("a candidate that is itself a link is retained, not entered");
    }

    [Fact]
    public async Task A_file_held_open_without_sharing_is_kept_and_counted_and_its_neighbours_go()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var tree = Make("coai-busy", 30, ("a-free.txt", 30), ("held.txt", 30));
        using var held = new FileStream(Path.Combine(tree, "held.txt"), FileMode.Open, FileAccess.Read, FileShare.None);

        var done = Done(await Engine().ExecuteAsync(Run(RunTrigger.Cli), TestContext.Current.CancellationToken));

        File.Exists(Path.Combine(tree, "held.txt")).Should().BeTrue();
        File.Exists(Path.Combine(tree, "a-free.txt")).Should().BeFalse();
        done.Detail.Actions.Single().Run!.Notes.Should().Contain(n => n.Contains("kept in use 1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_entry_owned_by_another_account_is_left_alone()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var old = Make("coai-old", 30, ("a.txt", 30));

        await Engine(sid: "S-1-5-21-1-2-3-9999").ExecuteAsync(Run(RunTrigger.Cli), TestContext.Current.CancellationToken);

        Directory.Exists(old).Should().BeTrue("the real owner of the folder is this account, not that one");
    }

    [Fact]
    public async Task The_claude_folder_survives_and_the_deletion_policy_would_refuse_it_even_if_asked()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var claude = Make("claude", 30, (@"s\log.txt", 30));

        await Engine().ExecuteAsync(Run(RunTrigger.Cli), TestContext.Current.CancellationToken);
        var direct = _host.Files.DeleteDirectory(claude, new DeletionScope(Temp, "A23"));

        Directory.Exists(claude).Should().BeTrue();
        direct.Should().BeOfType<DeletionVerdict.Refused>().Which.Rule.Should().Be(DeletionRule.ClaudeTemp);
    }

    private sealed class CancelAfterFirstDelete(IFileSystem inner, CancellationTokenSource cts) : DelegatingFileSystem(inner)
    {
        public override DeletionVerdict DeleteFile(string path, DeletionScope scope)
        {
            var verdict = base.DeleteFile(path, scope);
            cts.Cancel();
            return verdict;
        }
    }
}
