using System.Text.Json;
using System.Text.Json.Nodes;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Archive;
using WslCare.Core.Collectors;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Mcp;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary>
/// Plan §15r E9.S5 — the Windows side of the archive through the command line, on the Windows legs: <c>archive preview</c> lists a
/// due Claude Code session; held open by a process (with NO sharing — any open by the product would fail) it stays <c>in-use</c>,
/// the holder named; <c>archive run</c> copies it, phase 2 keeps it while it is held again, removes it once it is not, and
/// <c>archive restore</c> puts it back. The Restart Manager is the real one; the process table is this test's (no Claude Code runs).
/// </summary>
public sealed class ArchiveWindowsFlowTests : IDisposable
{
    private const string WindowsOnly = "the Windows side's Restart Manager and file semantics";
    private const string Key = "projects/p/s1.jsonl";

    private readonly SandboxHost _sandbox = new("archive-windows-flow");

    public void Dispose() => _sandbox.Dispose();

    [Fact]
    public void A_due_session_moves_on_windows_only_while_no_process_holds_it_and_comes_back()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var session = Session();
        var baseFolder = Directory.CreateDirectory(Path.Combine(_sandbox.Root.Path, "archive-base")).FullName;
        _sandbox.WriteUserConfig(new JsonObject { ["archive"] = new JsonObject { ["baseFolder"] = baseFolder } }.ToJsonString());

        var free = Preview();
        ArchivePreviewReport held;
        using (Hold(session))
        {
            held = Preview();
        }

        Claude(free).DueUnits.Should().Be(1, Note(free));
        Claude(held).DueUnits.Should().Be(0);
        Claude(held).Skipped.Should().Contain(s => s.Rule == SkipRule.InUse && s.FirstWhy.Contains($"pid {Environment.ProcessId}", StringComparison.Ordinal));

        Run().Agents.Single(a => a.Id == "claude-code").Copied.Should().Be(1);
        ADayLater();
        using (Hold(session))
        {
            Run();
        }

        File.Exists(session).Should().BeTrue("phase 2 asks again before it removes, and the session is held");
        Run().Agents.Single(a => a.Id == "claude-code").Removed.Should().Be(1);
        File.Exists(session).Should().BeFalse();

        var restore = CliRun.Over(Host(), "archive", "restore", "--agent", "claude-code", "--session", Key, "--json");

        restore.Exit.Should().Be((int)ExitCode.Ok, restore.Stdout + restore.Stderr);
        File.ReadAllText(session).Should().Be("the transcript");
    }

    /// <summary>The plan round's finding 4: the Windows user layer (%APPDATA%\wsl-care\config.json) takes the archive keys a user may
    /// set — the base folder — and leaves a machine-only archive key to the machine layer, saying so.</summary>
    [Fact]
    public void The_windows_user_layer_takes_the_base_folder_and_leaves_a_machine_only_archive_key()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var baseFolder = Directory.CreateDirectory(Path.Combine(_sandbox.Root.Path, "archive-base")).FullName;
        _sandbox.WriteUserConfig(new JsonObject { ["archive"] = new JsonObject { ["baseFolder"] = baseFolder, ["reachabilitySeconds"] = 5 } }.ToJsonString());

        var loaded = Core.Config.ConfigLoader.Load(_sandbox.Paths, _sandbox.Files);

        loaded.Errors.Should().BeEmpty();
        loaded.Config.Text(Core.Config.ConfigKeys.Archive.BaseFolder).Should().Be(baseFolder);
        loaded.Config.Int(Core.Config.ConfigKeys.Archive.ReachabilitySeconds).Should().NotBe(5, "a machine-only key is not the user's");
        loaded.Notices.Should().Contain(n => n.Key == Core.Config.ConfigKeys.Archive.ReachabilitySeconds.Name);
    }

    private string Session()
    {
        var path = Path.Combine(_sandbox.Paths.Home, ".claude", "projects", "p", "s1.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "the transcript");
        File.SetLastWriteTimeUtc(path, FixedTimeProvider.DefaultNow.UtcDateTime.AddDays(-40));
        return path;
    }

    private static FileStream Hold(string path) => new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    private ArchivePreviewReport Preview()
    {
        var run = CliRun.Over(Host(), "archive", "preview", "--agent", "claude-code", "--json");
        run.Exit.Should().Be((int)ExitCode.Ok, run.Stderr);
        return JsonSerializer.Deserialize(run.Stdout, WslCareJsonContext.Default.ArchivePreviewReport)!;
    }

    private ArchiveRunReport Run()
    {
        var run = CliRun.Over(Host(), "archive", "run", "--agent", "claude-code", "--json");
        var answer = run.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last();
        return JsonSerializer.Deserialize(answer, WslCareJsonContext.Default.ArchiveRunReport) ?? throw new InvalidOperationException(run.Stderr);
    }

    private static AgentPreviewReport Claude(ArchivePreviewReport report) => report.Agents.Single(a => a.Id == "claude-code");

    private static string Note(ArchivePreviewReport report) => $"{report.InUse.State}: {report.InUse.Note}; {string.Join("; ", Claude(report).Skipped.Select(s => $"{s.Rule} {s.FirstWhy}"))}";

    /// <summary>Every archived in-flight entry dated two days back, so the next run may remove it.</summary>
    private void ADayLater()
    {
        var inflight = Path.Combine(new ArchiveState(_sandbox.Paths, _sandbox.Files).Folder, "inflight.json");
        var node = JsonNode.Parse(File.ReadAllText(inflight))!;
        foreach (var entry in node["entries"]!.AsArray())
        {
            entry!["archivedAtUtc"] = FixedTimeProvider.DefaultNow.AddDays(-2).ToString("O");
        }

        File.WriteAllText(inflight, node.ToJsonString());
    }

    /// <summary>A host of THIS user, the real Restart Manager, and a process table in which no Claude Code runs.</summary>
    private CliHost Host() =>
        new(_sandbox.Paths, _sandbox.Files, new FixedTimeProvider(), new RecordingCommandRunner())
        {
            Privilege = new ProcessPrivilege(false, "a test says so"),
            WindowsProcesses = new NoClaude(),
        };

    private sealed class NoClaude : IWindowsProcessTable
    {
        public Reading<IReadOnlyList<WindowsProcessEntry>> List() => Reading.Of<IReadOnlyList<WindowsProcessEntry>>([new WindowsProcessEntry(10, 4, "explorer.exe")]);

        public WindowsProcessDetails Details(int pid) => WindowsProcessDetails.Unopenable("not asked");
    }
}
