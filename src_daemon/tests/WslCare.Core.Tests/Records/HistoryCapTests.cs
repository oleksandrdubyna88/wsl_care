using FluentAssertions;

using WslCare.Core.Actions.Engine;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Records;

/// <summary>
/// E7.S2b/S2c review C-H1 and C-M7: a history past <c>records.maxHistoryBytes</c> reads as a PROBLEM, never as "no runs" — so the
/// reconcile writes no false <c>interrupted</c> line (one per detail, more every run), the request sweep keeps every request, and
/// the retention's rewrite neither reads the whole file nor loses it.
/// </summary>
public sealed class HistoryCapTests : IDisposable
{
    private static readonly DateTimeOffset Now = FixedTimeProvider.DefaultNow;
    private readonly SandboxHost _sandbox = new("history-cap");

    public void Dispose() => _sandbox.Dispose();

    /// <summary>A history one byte past the smallest cap the key allows, its lines all well formed.</summary>
    private string HistoryPastTheCap()
    {
        var line = new string('x', 1000);
        var path = RunHistory.File(_sandbox.Paths);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var writer = new StreamWriter(path))
        {
            var lineBytes = line.Length + 1;
            for (long written = 0; written <= ConfigKeys.Records.MaxHistoryBytes.Min; written += lineBytes)
            {
                writer.Write(line + "\n");
            }
        }

        return path;
    }

    private static EffectiveConfig SmallestCap() => ConfigLoader.Load(
    [
        (ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults())),
        (new ConfigLayerFile(ConfigLayer.Machine, "/etc/wsl-care/config.json"), new FileReadResult.Content(System.Text.Encoding.UTF8.GetBytes($$"""{ "records": { "maxHistoryBytes": {{ConfigKeys.Records.MaxHistoryBytes.Min}} } }"""))),
    ]).Config;

    [Fact]
    public void A_history_past_its_cap_is_a_problem_and_the_reconcile_writes_no_interrupted_line()
    {
        var path = HistoryPastTheCap();
        var size = new FileInfo(path).Length;
        RunDetailStore.Write(_sandbox.Paths, _sandbox.Files, RunId.New(Now.AddHours(-1), 7), """{"schemaVersion":1}"""u8);

        using (Tuning.Use(SmallestCap()))
        {
            RunHistory.Read(_sandbox.Paths, _sandbox.Files).Problem.Should().Contain("larger than");
            var report = RunReconcile.Apply(_sandbox.Paths, _sandbox.Files, Now);

            report.Interrupted.Should().BeEmpty("review C-H1: an unreadable history names no run, so no detail is an orphan");
            report.Problem.Should().Contain("reconcile skipped");
        }

        new FileInfo(path).Length.Should().Be(size, "nothing was appended");
    }

    [Fact]
    public async Task With_a_history_past_its_cap_the_request_sweep_keeps_every_request()
    {
        HistoryPastTheCap();
        var request = new RunRequestFile(1, RunId.New(Now.AddHours(-1), 9), "act", ["A10"], RunTrigger.Manual, Now.AddHours(-1));
        RunRequests.Create(_sandbox.Paths, _sandbox.Files, request);

        using (Tuning.Use(SmallestCap()))
        {
            var notes = await RequestSweep.ApplyAsync(_sandbox.Paths, _sandbox.Files, new RecordingCommandRunner(), new FakeProcessTable(), Now, own: null);

            notes.Should().ContainSingle().Which.Should().Contain("the request sweep is skipped");
        }

        File.Exists(RunRequests.File(_sandbox.Paths, request.RunId)).Should().BeTrue("the request is kept until the history reads again");
    }

    [Fact]
    public void The_retention_rewrite_of_a_history_past_its_cap_is_refused_and_the_file_kept()
    {
        var path = HistoryPastTheCap();
        var size = new FileInfo(path).Length;

        using (Tuning.Use(SmallestCap()))
        {
            var report = RunRetention.Sweep(_sandbox.Paths, _sandbox.Files, Now);

            report.Problems.Should().Contain(p => p.Contains("is not rewritten", StringComparison.Ordinal), "review C-M7: read under the cap, never whole");
        }

        new FileInfo(path).Length.Should().Be(size);
    }
}
