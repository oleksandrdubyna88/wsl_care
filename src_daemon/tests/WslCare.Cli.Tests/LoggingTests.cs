using FluentAssertions;

using Serilog.Events;
using Serilog.Parsing;

using WslCare.Cli.Logging;
using WslCare.Core.Files.Deletion;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary>
/// The family logging rule's measurable half: escapes on a redirected writer (control included),
/// a file per run that segments at UTC midnight, and retention that deletes only expired day
/// folders — through the file-system seam.
/// </summary>
public sealed class LoggingTests
{
    private static LogEvent Event(DateTimeOffset at, LogEventLevel level, string message) =>
        new(at, level, exception: null, new MessageTemplateParser().Parse(message), []);

    private static int Escapes(string text) => text.Count(c => c == '\x1b');

    /// <summary>Reads a file the sink still holds open for writing — as <c>tail -f</c> would — by
    /// asking for read/write sharing rather than <c>File.ReadAllText</c>'s read-only sharing.</summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void The_console_sink_writes_escapes_on_a_redirected_writer()
    {
        // The control first: this writer preserves escapes, or the sink's count proves nothing.
        var control = new StringWriter();
        control.Write("\x1b[38;5;42mINF\x1b[0m");
        Escapes(control.ToString()).Should().Be(2);

        var output = new StringWriter();
        new AnsiConsoleSink(output).Emit(Event(DateTimeOffset.UtcNow, LogEventLevel.Information, "vault opened"));

        var line = output.ToString();
        Escapes(line).Should().BeGreaterThanOrEqualTo(4);
        line.Should().Contain("vault opened").And.Contain("INF");
    }

    [Fact]
    public void The_console_sink_colours_levels_differently_and_renders_the_message_unquoted()
    {
        var output = new StringWriter();
        var sink = new AnsiConsoleSink(output);
        sink.Emit(Event(DateTimeOffset.UtcNow, LogEventLevel.Warning, "w"));
        sink.Emit(Event(DateTimeOffset.UtcNow, LogEventLevel.Error, "e"));
        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, new MessageTemplateParser().Parse("database {Name}"), [new LogEventProperty("Name", new ScalarValue("qln"))]));

        var text = output.ToString();
        text.Should().Contain("\x1b[38;5;214mWRN").And.Contain("\x1b[38;5;203mERR");
        text.Should().Contain("database qln").And.NotContain("\"qln\"");
    }

    [Fact]
    public void The_run_file_segments_at_utc_midnight_into_the_next_days_folder_with_the_same_pid()
    {
        using var root = new TempRoot("logsink");
        var started = new DateTime(2026, 10, 2, 23, 59, 58, DateTimeKind.Utc);
        using var sink = new DailyRunFileSink(root.Path, "wsl-care", "{Message:lj}{NewLine}", started);
        var first = sink.CurrentPath;

        sink.Emit(Event(new DateTimeOffset(started), LogEventLevel.Information, "before midnight"));
        sink.Emit(Event(new DateTimeOffset(new DateTime(2026, 10, 3, 0, 0, 2, DateTimeKind.Utc)), LogEventLevel.Information, "after midnight"));
        var second = sink.CurrentPath;

        first.Should().Contain(Path.Combine("2026-10-02", $"wsl-care-23-59-58-{Environment.ProcessId}.log"));
        second.Should().Contain(Path.Combine("2026-10-03", $"wsl-care-00-00-00-{Environment.ProcessId}.log"));
        ReadShared(first).Should().Contain("before midnight").And.NotContain("after midnight");
        ReadShared(second).Should().Contain("after midnight");
    }

    [Fact]
    public void The_run_file_never_rolls_backward_on_a_clock_correction()
    {
        using var root = new TempRoot("logsink-back");
        var started = new DateTime(2026, 10, 3, 0, 0, 5, DateTimeKind.Utc);
        using var sink = new DailyRunFileSink(root.Path, "wsl-care", "{Message:lj}{NewLine}", started);
        var path = sink.CurrentPath;

        sink.Emit(Event(new DateTimeOffset(new DateTime(2026, 10, 2, 23, 59, 59, DateTimeKind.Utc)), LogEventLevel.Information, "late line"));

        sink.CurrentPath.Should().Be(path);
        ReadShared(path).Should().Contain("late line");
    }

    [Fact]
    public void Retention_selects_only_expired_day_folders_and_never_today()
    {
        var today = new DateOnly(2026, 10, 2);
        var folders = new[] { "2026-10-02", "2026-09-19", "2026-09-18", "2026-09-17", "2026-07-01", "not-a-date" };

        LogRetention.FoldersToPrune(folders, today, retainDays: 14).Should().Equal("2026-09-17", "2026-07-01");
        LogRetention.FoldersToPrune(["2000-01-01"], today, retainDays: 0).Should().BeEmpty("0 disables the sweep");
    }

    [Fact]
    public void Retention_deletes_through_the_file_system_seam_and_leaves_today_and_strangers_alone()
    {
        using var sandbox = new SandboxHost("retention");
        var logRoot = sandbox.Paths.LogDirectory;
        Directory.CreateDirectory(Path.Combine(logRoot, "2026-01-01"));
        File.WriteAllText(Path.Combine(logRoot, "2026-01-01", "wsl-care-10-00-00-1.log"), "old");
        Directory.CreateDirectory(Path.Combine(logRoot, "2026-10-02"));
        Directory.CreateDirectory(Path.Combine(logRoot, "keep-me"));

        var report = LogRetention.Prune(sandbox.Files, logRoot, new DateOnly(2026, 10, 2), 14);

        report.Deleted.Should().ContainSingle().Which.Should().EndWith("2026-01-01");
        report.Refused.Should().BeEmpty();
        report.Failed.Should().BeEmpty();
        Directory.Exists(Path.Combine(logRoot, "2026-01-01")).Should().BeFalse();
        Directory.Exists(Path.Combine(logRoot, "2026-10-02")).Should().BeTrue();
        Directory.Exists(Path.Combine(logRoot, "keep-me")).Should().BeTrue();
    }

    [Fact]
    public void Retention_over_a_missing_root_is_a_no_op()
    {
        using var sandbox = new SandboxHost("retention-missing");

        var report = LogRetention.Prune(sandbox.Files, sandbox.Paths.LogDirectory, new DateOnly(2026, 10, 2), 14);

        report.Deleted.Should().BeEmpty();
    }

    [Fact]
    public void Retention_refuses_an_expired_folder_that_is_a_link_into_a_protected_place()
    {
        using var sandbox = new SandboxHost("retention-link");
        var logRoot = sandbox.Paths.LogDirectory;
        Directory.CreateDirectory(logRoot);
        var git = sandbox.Paths.GitRoots[0];
        Directory.CreateDirectory(git);
        File.WriteAllText(Path.Combine(git, "precious.txt"), "x");
        Assert.SkipUnless(DirectoryLinks.TryCreate(Path.Combine(logRoot, "2020-01-01"), git), "this account can create neither a symbolic link nor a junction");

        var report = LogRetention.Prune(sandbox.Files, logRoot, new DateOnly(2026, 10, 2), 14);

        report.Deleted.Should().BeEmpty();
        report.Refused.Should().ContainSingle().Which.Should().Contain(DeletionRule.GitFolder.ToString());
        File.Exists(Path.Combine(git, "precious.txt")).Should().BeTrue();
    }

    [Fact]
    public void Starting_the_logger_writes_one_file_per_run_under_the_hosts_log_directory_at_the_configured_level()
    {
        // The run starts NOW (E3.S2 fix): Serilog stamps each event with the real clock, and the sink opens a new day's segment
        // for an event dated after the run's start day — a fixed 2026-10-02 start made this test expire on 2026-10-03.
        using var sandbox = new SandboxHost("logging-start");
        var started = DateTimeOffset.UtcNow;
        var host = new CliHost(sandbox.Paths, sandbox.Files, new FixedTimeProvider(started), new RecordingCommandRunner());
        var config = Core.Config.ConfigLoader.Load(host.Paths, host.Files).Config;
        var console = new StringWriter();

        using (var logger = WslCareLogging.Start(host, config, "wsl-care", console))
        {
            logger.Information("hello from the test");
            logger.Debug("not at Information");
        }

        var day = Path.Combine(sandbox.Paths.LogDirectory, started.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        var files = Directory.GetFiles(day);
        files.Should().ContainSingle().Which.Should().EndWith($"wsl-care-{started.UtcDateTime:HH-mm-ss}-{Environment.ProcessId}.log");
        var written = string.Concat(Directory.GetFiles(sandbox.Paths.LogDirectory, "*.log", SearchOption.AllDirectories).Select(File.ReadAllText));
        written.Should().Contain("hello from the test").And.NotContain("not at Information");
        console.ToString().Should().Contain("hello from the test").And.Contain("\x1b[");
    }

    [Fact]
    public void Level_names_map_to_serilog_levels_and_default_to_information()
    {
        WslCareLogging.Level("Warning").Should().Be(LogEventLevel.Warning);
        WslCareLogging.Level("Verbose").Should().Be(LogEventLevel.Verbose);
        WslCareLogging.Level("Information").Should().Be(LogEventLevel.Information);
    }
}
