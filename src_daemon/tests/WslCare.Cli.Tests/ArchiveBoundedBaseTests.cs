using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Archive;
using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary>
/// Plan §15r E9.S4 own review round S-M1: the children root starts BEFORE the long run — <c>archive reach</c> and <c>archive list
/// --restorable</c> — touch the base only inside the bounded reachability window, and the reach only after it holds the side's lock.
/// A share that stops answering blocks the reader in the kernel; before the fix such a child hung in its base judgement, before the
/// lock, unbounded, and every timer run and every A20 preview added another.
/// </summary>
public sealed class ArchiveBoundedBaseTests : IDisposable
{
    private const string Base = "/mnt/v/ai-archive";

    private const string MountInfo =
        "523 504 8:96 / / rw,relatime - ext4 /dev/sdg rw,discard,errors=remount-ro,data=ordered\n" +
        "479 523 0:154 / /mnt/v rw,relatime - 9p V: rw,aname=drvfs;path=V:;uid=1000;gid=1000;metadata;symlinkroot=/mnt/,cache=0x5,access=client,msize=65536,trans=fd,rfd=3,wfd=3\n";

    /// <summary>How long the test waits for an answer that must come within <c>archive.reachabilitySeconds</c> (1 here).</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly LinuxSandbox _sandbox = new("archive-bounded-base");
    private readonly ManualResetEventSlim _release = new(false);

    public ArchiveBoundedBaseTests()
    {
        _sandbox.Write("/proc/self/mountinfo", MountInfo);
        Directory.CreateDirectory(_sandbox.Paths.DistroPath(Base));
        Written(_sandbox.Paths.MachineConfigFile, """{ "archive": { "reachabilitySeconds": 1 } }""");
        Written(_sandbox.Paths.UserConfigFile, $$"""{ "archive": { "baseFolder": "{{Base}}" } }""");
    }

    private static void Written(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        _release.Set();
        _release.Dispose();
        _sandbox.Dispose();
    }

    [Theory]
    [InlineData("reach")]
    [InlineData("list")]
    public async Task A_base_that_stops_answering_holds_the_child_only_for_the_reachability_window(string verb)
    {
        var files = new BlockingBase(_sandbox.Files, _sandbox.Paths.DistroPath(Base), _release);
        string[] args = verb == "reach" ? ["archive", "reach", "--json"] : ["archive", "list", "--restorable", "--json"];
        var run = Task.Run(() => CliRun.Over(Host(files), args), TestContext.Current.CancellationToken);

        (await Answered(run)).Should().BeTrue($"archive {verb} must answer within archive.reachabilitySeconds even when the base blocks its reader in the kernel");
        var (exit, stdout, stderr) = await run;
        exit.Should().Be((int)ExitCode.RunFailed, stdout + stderr);
        stdout.Should().Contain(RunOutcomes.Unreachable);
    }

    /// <summary>The S4 gate round, finding 2: a reach whose base check timed out leaves that check behind, still blocked in the kernel —
    /// so it keeps the side's lock for as long as its process lives. The next reach answers busy at once and never touches the base
    /// beside it; stuck checks do not pile up.</summary>
    [Fact]
    public async Task A_reach_that_timed_out_keeps_the_sides_lock_so_the_next_answers_busy_without_touching_the_base()
    {
        var journal = new List<string>();
        var files = new BlockingBase(_sandbox.Files, _sandbox.Paths.DistroPath(Base), _release) { Journal = journal };

        var first = Task.Run(() => CliRun.Over(Host(files), "archive", "reach", "--json"), TestContext.Current.CancellationToken);
        (await Answered(first)).Should().BeTrue("the first reach answers within its window");
        var second = Task.Run(() => CliRun.Over(Host(files), "archive", "reach", "--json"), TestContext.Current.CancellationToken);
        (await Answered(second)).Should().BeTrue("the second reach answers too");

        (await first).Stdout.Should().Contain(RunOutcomes.Unreachable);
        var (exit, stdout, stderr) = await second;
        exit.Should().Be(ArchiveExits.Busy, stdout + stderr);
        lock (journal)
        {
            journal.Count(j => j == "base").Should().Be(1, "only the first reach touched the base");
        }
    }

    /// <summary>The S4 gate round, finding 4: <c>archive run</c> takes the side's lock BEFORE it judges the base, as the reach does — a
    /// side whose lock another run holds answers busy at once, the base untouched.</summary>
    [Fact]
    public async Task A_run_beside_a_held_lock_answers_busy_without_touching_the_base()
    {
        var journal = new List<string>();
        var files = new BlockingBase(_sandbox.Files, _sandbox.Paths.DistroPath(Base), _release) { Journal = journal };
        var lockFile = new ArchiveState(_sandbox.Paths, _sandbox.Files).LockFile;
        Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
        using var held = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var run = Task.Run(() => CliRun.Over(Host(files), "archive", "run", "--json"), TestContext.Current.CancellationToken);

        (await Answered(run)).Should().BeTrue("the run answers at once");
        var (exit, stdout, stderr) = await run;
        exit.Should().Be(ArchiveExits.Busy, stdout + stderr);
        lock (journal)
        {
            journal.Should().NotContain("base", "a held side answers busy before any base I/O");
        }
    }

    /// <summary>The reach takes the side's lock FIRST: a reach left stuck on the base holds the lock, so the next one answers busy
    /// at once — it never touches the base beside the first.</summary>
    [Fact]
    public async Task The_reach_takes_the_sides_lock_before_it_touches_the_base()
    {
        var journal = new List<string>();
        var files = new BlockingBase(_sandbox.Files, _sandbox.Paths.DistroPath(Base), _release) { Journal = journal };

        var run = Task.Run(() => CliRun.Over(Host(files), "archive", "reach", "--json"), TestContext.Current.CancellationToken);

        (await Answered(run)).Should().BeTrue("the reach answers within its window");
        lock (journal)
        {
            journal.Should().Contain("lock");
            journal.Should().Contain("base");
            journal.IndexOf("lock").Should().BeLessThan(journal.IndexOf("base"), "the side's lock comes before any base I/O");
        }
    }

    /// <summary>The window is widened here (the other tests keep 1 s to time a base that never answers): an ANSWERING base must not
    /// be timed against one second on a loaded CI runner, where the reach's task can start late (win-x64, 2026-10-10: "unreachable").</summary>
    [Fact]
    public void A_base_that_answers_is_reached_and_listed()
    {
        Written(_sandbox.Paths.MachineConfigFile, """{ "archive": { "reachabilitySeconds": 30 } }""");
        var reach = CliRun.Over(Host(_sandbox.Files), "archive", "reach", "--json");
        var list = CliRun.Over(Host(_sandbox.Files), "archive", "list", "--restorable", "--json");

        reach.Exit.Should().Be((int)ExitCode.Ok, reach.Stdout + reach.Stderr);
        JsonSerializer.Deserialize(reach.Stdout, WslCareJsonContext.Default.ArchiveRunReport)!.Outcome.Should().Be(RunOutcomes.Done);
        list.Exit.Should().Be((int)ExitCode.Ok, list.Stdout + list.Stderr);
        JsonSerializer.Deserialize(list.Stdout, WslCareJsonContext.Default.ArchiveListReport)!.Outcome.Should().Be(RunOutcomes.Done);
    }

    /// <summary>C-1: a side whose lock another run holds answers busy — with its answer line and the exit root reads by the same
    /// name (<see cref="ArchiveExits.Busy"/>, the command line's own <see cref="ExitCode.Busy"/>).</summary>
    [Fact]
    public void A_held_side_answers_busy_with_the_exit_root_reads_by_the_same_name()
    {
        var lockFile = new ArchiveState(_sandbox.Paths, _sandbox.Files).LockFile;
        Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
        using var held = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var reach = CliRun.Over(Host(_sandbox.Files), "archive", "reach", "--json");

        reach.Exit.Should().Be(ArchiveExits.Busy, reach.Stdout + reach.Stderr);
        new[] { (int)ExitCode.Ok, (int)ExitCode.RunFailed, (int)ExitCode.Busy }.Should().Equal(ArchiveExits.Ok, ArchiveExits.RunFailed, ArchiveExits.Busy);
        JsonSerializer.Deserialize(reach.Stdout, WslCareJsonContext.Default.ArchiveRunReport)!.Outcome.Should().Be(RunOutcomes.Busy);
    }

    /// <summary>C-8: root reads a child's answer as its LAST line — so every verb root starts answers on ONE line. An indented answer's
    /// last line is a lone brace: A13's preview and A20's list could never be believed (found by the round trip over the built CLI).</summary>
    [Theory]
    [InlineData("archive", "preview", "--json")]
    [InlineData("archive", "list", "--restorable", "--json")]
    [InlineData("archive", "reach", "--json")]
    public void Every_verb_root_starts_answers_on_one_line(params string[] args)
    {
        var run = CliRun.Over(Host(_sandbox.Files), args);

        run.Stdout.Trim().Should().NotBeEmpty(run.Stderr).And.NotContain("\n", "the answer is one line, the last");
    }

    /// <summary>Whether <paramref name="run"/> ended within <see cref="Patience"/>.</summary>
    private static async Task<bool> Answered(Task run) =>
        await Task.WhenAny(run, Task.Delay(Patience, TestContext.Current.CancellationToken)) == run;

    /// <summary>A host of THIS user — never the privilege the test process happens to have (CI's Windows runner is elevated).</summary>
    private CliHost Host(IFileSystem files) =>
        new(_sandbox.Paths, files, new FixedTimeProvider(), new RecordingCommandRunner()) { Privilege = new ProcessPrivilege(false, "a test says so") };

    /// <summary>The sandbox's disk, except that every look at the base blocks until the test ends — a share that stopped answering.</summary>
    private sealed class BlockingBase(IFileSystem inner, string baseOnDisk, ManualResetEventSlim release) : DelegatingFileSystem(inner), IArchiveFiles
    {
        private IArchiveFiles Archive => (IArchiveFiles)Inner;

        public List<string> Journal { get; init; } = [];

        private T Blocked<T>(string path, Func<T> look)
        {
            if (path.Replace('\\', '/').StartsWith(baseOnDisk.Replace('\\', '/'), StringComparison.Ordinal))
            {
                Note("base");
                release.Wait();
            }

            return look();
        }

        private void Note(string what)
        {
            lock (Journal)
            {
                Journal.Add(what);
            }
        }

        public override bool DirectoryExists(string path) => Blocked(path, () => base.DirectoryExists(path));

        public override bool FileExists(string path) => Blocked(path, () => base.FileExists(path));

        public override RealPathResult ResolvePath(string path) => Blocked(path, () => base.ResolvePath(path));

        public override WriteAccess ProbeWriteAccess(string directory) => Blocked(directory, () => base.ProbeWriteAccess(directory));

        public override ExclusiveLock TryLockExclusive(string lockPath)
        {
            Note("lock");
            return base.TryLockExclusive(lockPath);
        }
        public SourceOpen OpenSource(string layoutRoot, string path) => Archive.OpenSource(layoutRoot, path);

        public FolderBeneath OpenFolderBeneath(string baseFolder, IReadOnlyList<string> levels, DeletionScope scope) => Archive.OpenFolderBeneath(baseFolder, levels, scope);

        public FolderBeneath OpenExistingFolderBeneath(string baseFolder, IReadOnlyList<string> levels) => Archive.OpenExistingFolderBeneath(baseFolder, levels);

        public DurableAppend AppendDurably(BeneathFolder folder, string name, ReadOnlySpan<byte> bytes, DeletionScope scope) => Archive.AppendDurably(folder, name, bytes, scope);

        public FileReadResult ReadCapped(BeneathFolder folder, string name, int maxBytes) => Archive.ReadCapped(folder, name, maxBytes);

        public VerifiedRemoval RemoveIfUnchanged(BeneathFolder folder, string name, string expectedSha256, DeletionScope scope) => Archive.RemoveIfUnchanged(folder, name, expectedSha256, scope);

        public ExclusiveFile CreateExclusive(BeneathFolder folder, string name, DeletionScope scope) => Archive.CreateExclusive(folder, name, scope);

        public FileHash ReadBack(BeneathFolder folder, string name) => Archive.ReadBack(folder, name);

        public VerifiedRemoval RemoveOwnCopy(BeneathFolder folder, string name, FileIdentity created, DeletionScope scope) => Archive.RemoveOwnCopy(folder, name, created, scope);

        public FolderFlush FlushFolder(BeneathFolder folder) => Archive.FlushFolder(folder);

        public NoReplaceRename QuarantineRename(string layoutRoot, string path, string quarantinedName, DeletionScope scope) => Archive.QuarantineRename(layoutRoot, path, quarantinedName, scope);

        public NoReplaceRename RenameBack(string layoutRoot, string quarantinedPath, string originalName, DeletionScope scope) => Archive.RenameBack(layoutRoot, quarantinedPath, originalName, scope);

        public NoReplaceRename PromoteRestored(string layoutRoot, string temporaryPath, string finalName, DeletionScope scope) => Archive.PromoteRestored(layoutRoot, temporaryPath, finalName, scope);

        public VerifiedRemoval RemoveVerified(string layoutRoot, string path, string expectedSha256, string archivedCopy, DeletionScope scope) => Archive.RemoveVerified(layoutRoot, path, expectedSha256, archivedCopy, scope);

        public VerifiedRemoval RemoveEmptyFolder(string layoutRoot, string folder, DeletionScope scope) => Archive.RemoveEmptyFolder(layoutRoot, folder, scope);
    }
}
