using System.Text;
using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Health;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Records;
using WslCare.Core.Collectors;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Health;

/// <summary>
/// E15.S10 A3: <c>{state}/defender-cpu.jsonl</c> — one line per full run, appended and never rewritten except to drop the lines older
/// than <c>defender.localRetentionDays</c> when the file is next written; the temp file of that rewrite is made IN <c>{state}</c>,
/// beside the ledger. A missing or malformed file is "no baseline", never an error.
/// </summary>
public sealed class DefenderCpuLedgerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private readonly TempRoot _root = new("defender-ledger");
    private readonly LinuxHostPaths _paths;
    private readonly PhysicalFileSystem _files;

    public DefenderCpuLedgerTests()
    {
        _paths = new LinuxHostPaths(LinuxEnvironment.Sandboxed(_root.Path));
        _files = new PhysicalFileSystem(_paths);
    }

    public void Dispose() => _root.Dispose();

    private DefenderCpuLedger Ledger(IFileSystem? files = null) => new(_paths, files ?? _files);

    private static DefenderLedgerLine Line(DateTimeOffset at, long cpu = 100_000_000, int pid = 6964, long start = 134_365_236_258_808_755) =>
        new(at.ToFileTime(), cpu, pid, start);

    private static string Json(DefenderLedgerLine line) => JsonSerializer.Serialize(line, WslCareJsonContext.Compact.DefenderLedgerLine);

    private string Text() => File.ReadAllText(Ledger().File);

    private void Put(params string[] lines)
    {
        Directory.CreateDirectory(_paths.StateDirectory);
        File.WriteAllText(Ledger().File, string.Join('\n', lines) + "\n");
    }

    [Fact]
    public void The_ledger_is_defender_cpu_jsonl_in_the_state_directory()
    {
        Ledger().File.Should().Be(_paths.Rules.Join(_paths.StateDirectory, "defender-cpu.jsonl"));
        Path.GetFileName(Ledger().File).Should().Be("defender-cpu.jsonl");
        Path.GetFullPath(Path.GetDirectoryName(Ledger().File)!).Should().Be(Path.GetFullPath(_paths.StateDirectory));
    }

    [Fact]
    public void An_appended_line_is_read_back_as_the_same_reading_and_the_span_it_carries()
    {
        var line = Line(Now, cpu: 993_513_593_750) with { From = Now.AddHours(-4).ToFileTime(), Cores = 0.7612 };

        Ledger().Append(line, Now).Should().BeEmpty("recorded");
        var read = Ledger().Read();

        read.Problem.Should().BeEmpty();
        read.Skipped.Should().BeEmpty();
        read.Lines.Should().Equal(line);
        read.Newest.Should().BeOfType<Reading<DefenderReading>.Available>().Which.Value.Should().Be(line.ToReading());
        read.Spans.Should().ContainSingle().Which.Should().Match<DefenderSpan>(s => s.Start == Now.AddHours(-4) && s.End == Now && s.Cores == 0.7612);
    }

    [Fact]
    public void Lines_are_appended_in_order_and_the_newest_is_the_baseline()
    {
        Ledger().Append(Line(Now.AddHours(-8), pid: 1), Now);
        Ledger().Append(Line(Now.AddHours(-4), pid: 2), Now);

        var read = Ledger().Read();

        read.Lines.Select(l => l.Pid).Should().Equal(1, 2);
        read.Newest.Should().BeOfType<Reading<DefenderReading>.Available>().Which.Value.Pid.Should().Be(2);
    }

    [Fact]
    public void A_missing_file_is_no_baseline_never_an_error()
    {
        var read = Ledger().Read();

        read.Lines.Should().BeEmpty();
        read.Problem.Should().BeEmpty();
        read.Newest.IsAvailable.Should().BeFalse();
        read.Newest.ReasonOrEmpty.Should().Be(DefenderLedgerRead.NoBaseline);
        read.Spans.Should().BeEmpty();
    }

    [Fact]
    public void A_malformed_line_is_skipped_and_named_and_the_rest_still_reads()
    {
        Put(Json(Line(Now.AddHours(-8))), "{ this is not json", Json(Line(Now.AddHours(-4), pid: 2)), "{\"ts\":1}", "[]");

        var read = Ledger().Read();

        read.Lines.Select(l => l.Pid).Should().Equal(6964, 2);
        read.Skipped.Should().HaveCount(3);
        read.Skipped[0].Should().Contain("line 2").And.Contain("defender-cpu.jsonl");
        read.Skipped.Should().Contain(s => s.Contains("line 4", StringComparison.Ordinal)).And.Contain(s => s.Contains("line 5", StringComparison.Ordinal));
        read.Newest.Should().BeOfType<Reading<DefenderReading>.Available>().Which.Value.Pid.Should().Be(2);
    }

    [Fact]
    public void A_torn_last_line_without_its_newline_is_a_write_in_progress_not_a_line()
    {
        Directory.CreateDirectory(_paths.StateDirectory);
        File.WriteAllText(Ledger().File, Json(Line(Now.AddHours(-4))) + "\n" + Json(Line(Now))[..20]);

        var read = Ledger().Read();

        read.Lines.Should().ContainSingle();
        read.Skipped.Should().BeEmpty("a line still being written is neither parsed nor counted as damage");
    }

    [Fact]
    public void A_line_older_than_the_retention_is_dropped_at_the_next_write()
    {
        var aged = Line(Now.AddDays(-401), pid: 1);
        var kept = Line(Now.AddDays(-399), pid: 2);
        Put(Json(aged), Json(kept));

        Ledger().Append(Line(Now, pid: 3), Now).Should().BeEmpty();

        var read = Ledger().Read();
        read.Lines.Select(l => l.Pid).Should().Equal([2, 3], "400 days are kept: the line of 401 days ago went, the line of 399 stayed");
        Text().Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(2);
    }

    [Fact]
    public void The_retention_follows_the_key()
    {
        Put(Json(Line(Now.AddDays(-40), pid: 1)), Json(Line(Now.AddDays(-20), pid: 2)));

        using (Tuning.Use(Config("""{ "defender": { "localRetentionDays": 30 } }""")))
        {
            Ledger().Append(Line(Now, pid: 3), Now);
        }

        Ledger().Read().Lines.Select(l => l.Pid).Should().Equal(2, 3);
    }

    [Fact]
    public void A_burst_of_appends_inside_the_retention_loses_nothing_and_rewrites_nothing()
    {
        for (var i = 0; i < 40; i++)
        {
            Ledger().Append(Line(Now.AddMinutes(-40 + i), cpu: 1_000_000 * (i + 1)), Now);
        }

        Ledger().Read().Lines.Should().HaveCount(40);
    }

    /// <summary>Plan round, finding 6: the rewrite's temp file is created IN <c>{state}</c>, beside the ledger — a same-directory rename,
    /// never a copy across filesystems from <c>/tmp</c>. The ledger asks the seam to rewrite its own path, judged by a scope rooted at the
    /// state directory, and leaves no temp file behind.</summary>
    [Fact]
    public void The_rewrite_is_made_beside_the_ledger_in_the_state_directory_and_leaves_no_temp_file()
    {
        Put(Json(Line(Now.AddDays(-401), pid: 1)));
        var spy = new RewriteSpy(_files);

        Ledger(spy).Append(Line(Now, pid: 2), Now);

        spy.Rewrites.Should().ContainSingle();
        spy.Rewrites[0].Path.Should().Be(Ledger().File);
        Path.GetFullPath(Path.GetDirectoryName(spy.Rewrites[0].Path)!).Should().Be(Path.GetFullPath(_paths.StateDirectory), "beside the ledger: its own directory");
        spy.Rewrites[0].Scope.Root.Should().Be(_paths.StateDirectory, "the deletion policy judges the replace inside {state}");
        Directory.GetFiles(_paths.StateDirectory).Select(Path.GetFileName).Should().OnlyContain(n => n == "defender-cpu.jsonl" || n == "defender-cpu.jsonl.lock", "no stray temp file");
        Directory.GetFiles(Path.GetTempPath(), "defender-cpu*").Should().BeEmpty("never in the system temp folder");
    }

    [Fact]
    public void A_line_is_about_160_bytes_and_a_year_of_a_run_an_hour_fits_in_two_megabytes()
    {
        var widest = new DefenderLedgerLine(long.MaxValue, long.MaxValue, int.MaxValue, long.MaxValue) { From = long.MaxValue, Cores = 12.345678 };

        var bytes = Encoding.UTF8.GetByteCount(Json(widest)) + 1;

        bytes.Should().BeLessThanOrEqualTo(200, "the plan projects ~160 B a line: four 19-digit numbers, a span, an instant");
        (bytes * 24L * 400).Should().BeLessThan(2_000_000, "one full run an hour for the 400 days kept");
    }

    [Fact]
    public void The_cores_of_a_span_are_written_to_six_decimals()
    {
        Ledger().Append(Line(Now) with { From = Now.AddHours(-1).ToFileTime(), Cores = 0.123456789012 }, Now);

        File.Exists(Ledger().File).Should().BeTrue("the line was appended to the ledger");
        Text().Should().Contain("\"cores\":0.123457");
    }

    private static EffectiveConfig Config(string machine)
    {
        var loaded = ConfigLoader.Load(
        [
            (ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults())),
            (new ConfigLayerFile(ConfigLayer.Machine, "/etc/wsl-care/config.json"), new FileReadResult.Content(Encoding.UTF8.GetBytes(machine))),
        ]);
        loaded.Errors.Should().BeEmpty();
        return loaded.Config;
    }

    /// <summary>The real file system, noting what the ledger asks to rewrite.</summary>
    private sealed class RewriteSpy(IFileSystem inner) : DelegatingFileSystem(inner)
    {
        public List<(string Path, DeletionScope Scope)> Rewrites { get; } = [];

        public override DeletionVerdict RewriteLines(string path, Func<IReadOnlyList<string>, IReadOnlyList<string>> keep, DeletionScope scope, TimeSpan lockTimeout)
        {
            Rewrites.Add((path, scope));
            return base.RewriteLines(path, keep, scope, lockTimeout);
        }
    }
}
