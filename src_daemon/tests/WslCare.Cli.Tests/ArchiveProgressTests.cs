using FluentAssertions;

using WslCare.Cli.Commands;
using WslCare.Core.Archive;

namespace WslCare.Cli.Tests;

/// <summary>The coai code round over E9.S2b/S3 (findings 2, 9, 11, 17): the archive verbs' one progress writer.</summary>
public sealed class ArchiveProgressTests
{
    private static readonly TimeSpan Long = TimeSpan.FromHours(1);
    private static readonly ArchiveProgressLine File = new("file", 1, 2048, 3);

    [Fact]
    public void Without_json_a_human_line_goes_to_stderr_and_stdout_keeps_only_the_answer()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        using (var progress = new ArchiveProgress(stdout, stderr, json: false, TimeProvider.System, Long))
        {
            progress.Line(File);
            progress.Answer(() => { stdout.WriteLine("answer"); return 0; }).Should().Be(0);
        }

        stderr.ToString().Should().Contain("archive: 1 file(s)");
        stdout.ToString().Trim().Should().Be("answer");
    }

    [Fact]
    public void With_json_the_line_is_json_on_stdout_and_stderr_stays_silent()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        using (var progress = new ArchiveProgress(stdout, stderr, json: true, TimeProvider.System, Long))
        {
            progress.Line(File);
        }

        stdout.ToString().Should().StartWith("{\"progress\":\"file\"");
        stderr.ToString().Should().BeEmpty();
    }

    [Fact]
    public void Nothing_is_written_after_the_answer()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        using var progress = new ArchiveProgress(stdout, stderr, json: true, TimeProvider.System, Long);

        progress.Answer(() => 0);
        progress.Line(File);

        stdout.ToString().Should().BeEmpty();
    }

    /// <summary>Finding 9: a consumer that went away (a broken pipe) ends the progress — it never crashes the run, and the answer is
    /// still asked for.</summary>
    [Fact]
    public void A_broken_pipe_ends_the_progress_never_the_run()
    {
        using var broken = new BrokenWriter();
        using var stderr = new StringWriter();
        using var progress = new ArchiveProgress(broken, stderr, json: true, TimeProvider.System, Long);

        var act = () => { progress.Line(File); progress.Line(File); };

        act.Should().NotThrow();
        broken.Writes.Should().Be(1, "the first failure closes the progress");
        progress.Answer(() => 7).Should().Be(7);
    }

    [Fact]
    public void A_heartbeat_comes_when_the_silence_runs_out()
    {
        using var stdout = new StringWriter();
        using var stderr = new SignalWriter();
        using var progress = new ArchiveProgress(stdout, stderr, json: false, TimeProvider.System, TimeSpan.FromMilliseconds(20));

        stderr.Written.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).Should().BeTrue("a heartbeat within the silence");
        progress.Answer(() => 0);

        stderr.First.Should().Contain("archive: still working");
    }

    private sealed class SignalWriter : StringWriter
    {
        public ManualResetEventSlim Written { get; } = new();

        public string First { get; private set; } = string.Empty;

        public override void WriteLine(string? value)
        {
            if (!Written.IsSet)
            {
                First = value ?? string.Empty;
                Written.Set();
            }
        }

        protected override void Dispose(bool disposing)
        {
            Written.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class BrokenWriter : StringWriter
    {
        public int Writes { get; private set; }

        public override void Write(string? value)
        {
            Writes++;
            throw new IOException("the pipe is gone");
        }

        public override void WriteLine(string? value) => Write(value);
    }
}
