using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Files;
using WslCare.Core.Json;
using WslCare.Core.Status;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary>
/// E14 S6: <c>wsl-care busy [--json]</c> — the "machine busy" signal agents poll before a heavy step. It reads
/// <c>/proc/pressure/{cpu,io,memory}</c> and <c>/proc/loadavg</c> and nothing else; exit <c>0</c> is calm or unknown (go),
/// <see cref="ExitCode.MachineBusy"/> is busy (wait), naming which pressure crossed which key.
/// </summary>
public sealed class BusyCommandTests : IDisposable
{
    private readonly TempRoot _root = new("busy");

    public void Dispose() => _root.Dispose();

    private Core.Hosting.LinuxHostPaths Paths => ProcfsFixture.PathsAt(_root.Path);

    private void Pressure(string resource, double avg10, double avg60) =>
        _root.File($"proc/pressure/{resource}", FormattableString.Invariant($"some avg10={avg10:0.00} avg60={avg60:0.00} avg300=1.00 total=1000\nfull avg10=0.00 avg60=0.00 avg300=0.00 total=0\n"));

    private (int Exit, string Stdout, string Stderr) Busy(params string[] args) =>
        CliRun.Over(new CliHost(Paths, new PhysicalFileSystem(Paths), new FixedTimeProvider(), new RecordingCommandRunner()), ["busy", .. args]);

    private static BusyReport Report(string stdout) => JsonSerializer.Deserialize(stdout, WslCareJsonContext.Default.BusyReport)!;

    /// <summary>Every path read or listed, with the distro's separators — what "reads no process" is checked against.</summary>
    private sealed class RecordingReads(IFileSystem inner, List<string> paths) : DelegatingFileSystem(inner), IFileSystem
    {
        public override FileReadResult ReadFile(string path)
        {
            paths.Add(path.Replace('\\', '/'));
            return base.ReadFile(path);
        }

        public override IReadOnlyList<string> ListDirectories(string path)
        {
            paths.Add(path.Replace('\\', '/') + "/");
            return base.ListDirectories(path);
        }
    }

    [Fact]
    public void Busy_answers_83_with_its_reasons_and_calm_answers_0()
    {
        Pressure("cpu", 31, 28.5);
        Pressure("io", 5, 1.2);
        Pressure("memory", 0, 0);
        _root.File("proc/loadavg", "36.10 30.02 22.50 3/2101 99999\n");

        var (busyExit, busyJson, busyErr) = Busy("--json");
        Pressure("cpu", 0.1, 4.18);
        var (calmExit, calmJson, _) = Busy("--json");
        var (_, text, _) = Busy();

        busyExit.Should().Be((int)ExitCode.MachineBusy, busyErr);
        var busy = Report(busyJson);
        busy.State.Should().Be("busy");
        busy.Reasons.Should().ContainSingle().Which.Should().Be(new BusyReasonReport("cpu", "avg60", 28.5, 20, "thresholds.cpuPressureWarnPercent"));
        busy.Load.Should().Be(new LoadReport(true, null, 36.10, 30.02, 22.50));
        calmExit.Should().Be((int)ExitCode.Ok);
        Report(calmJson).Should().Match<BusyReport>(r => r.State == "calm" && r.Reasons.Count == 0 && r.Pressure.Cpu.Available);
        text.Should().StartWith("calm").And.Contain("cpu 4.18").And.NotContain("\n\n");
    }

    /// <summary>coai code round 2026-10-09 (session 79b51f53), finding 6: the report's lists are never null — a reader that
    /// meets an answer without them (an older or a partial one) reads empty lists.</summary>
    [Fact]
    public void A_report_without_its_lists_reads_them_empty_never_null()
    {
        var report = JsonSerializer.Deserialize("""{ "state": "calm" }""", WslCareJsonContext.Default.BusyReport)!;

        report.Reasons.Should().NotBeNull().And.BeEmpty();
        report.Unread.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void An_unreadable_pressure_is_unknown_and_exits_0()
    {
        Pressure("cpu", 0.1, 4.18);
        Pressure("io", 0.1, 1);

        var (exit, json, _) = Busy("--json");

        exit.Should().Be((int)ExitCode.Ok, "unknown is go: an agent never waits on a kernel that cannot answer");
        var report = Report(json);
        report.State.Should().Be("unknown");
        report.Unread.Should().ContainSingle().Which.Should().StartWith("memory: ");
        report.Load.Available.Should().BeFalse();
    }

    [Fact]
    public void Busy_reads_no_process_and_writes_nothing()
    {
        Pressure("cpu", 31, 28.5);
        Pressure("io", 0, 0);
        Pressure("memory", 0, 0);
        _root.File("proc/4242/status", "Name:\tbomb\n");
        var reads = new List<string>();
        var files = new RecordingReads(new PhysicalFileSystem(Paths), reads);
        var before = Directory.GetFiles(_root.Path, "*", SearchOption.AllDirectories).Select(f => (f, File.GetLastWriteTimeUtc(f))).ToList();

        var (exit, _, stderr) = CliRun.Over(new CliHost(Paths, files, new FixedTimeProvider(), new RecordingCommandRunner()), "busy", "--json");

        exit.Should().Be((int)ExitCode.MachineBusy, stderr);
        reads.Where(path => path.Contains("/proc/", StringComparison.Ordinal)).Should()
            .OnlyContain(path => path.Contains("/proc/pressure/", StringComparison.Ordinal) || path.EndsWith("/proc/loadavg", StringComparison.Ordinal), "no process is read");
        Directory.GetFiles(_root.Path, "*", SearchOption.AllDirectories).Select(f => (f, File.GetLastWriteTimeUtc(f))).Should().Equal(before, "busy writes nothing");
    }
}
