using System.Text;
using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Json;
using WslCare.Core.Scheduler;

namespace WslCare.Core.Tests.Scheduler;

/// <summary>
/// WR-D1 (PLAN_windows_runner.md §3.7): <c>stage.json</c> is <c>{ version, sha256, stagedAtUtc, source }</c>, read STRICTLY — a record
/// with a member missing, a malformed digest or an unknown member is refused naming why, never taken with a default.
/// </summary>
public sealed class StageRecordsTests
{
    private const string Sha = "a14ad2eb0c6f1d9e8b7a6c5d4e3f2a1b0c9d8e7f6a5b4c3d2e1f0a9b8c7d6e5f";

    private const string Good = $$"""{ "version": "0.4.0", "sha256": "{{Sha}}", "stagedAtUtc": "2026-10-11T08:00:00+00:00", "source": "C:\\ext\\bin\\wsl-care.exe" }""";

    [Fact]
    public void A_whole_record_is_read()
    {
        var read = StageRecords.Parse(Encoding.UTF8.GetBytes(Good));

        read.Should().BeOfType<StageRead.Found>().Which.Record.Should().Be(
            new StageRecord("0.4.0", Sha, new DateTimeOffset(2026, 10, 11, 8, 0, 0, TimeSpan.Zero), "C:\\ext\\bin\\wsl-care.exe"));
    }

    /// <summary>The writer D2 will use and this reader agree on the member names (one source-generated shape, camel case).</summary>
    [Fact]
    public void What_the_context_writes_the_reader_takes()
    {
        var record = new StageRecord("0.4.0", Sha, new DateTimeOffset(2026, 10, 11, 8, 0, 0, TimeSpan.Zero), "C:\\ext\\bin\\wsl-care.exe");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, WslCareJsonContext.Default.StageRecord);

        Encoding.UTF8.GetString(bytes).Should().Contain("\"sha256\"").And.Contain("\"stagedAtUtc\"");
        StageRecords.Parse(bytes).Should().BeOfType<StageRead.Found>().Which.Record.Should().Be(record);
    }

    [Theory]
    [InlineData($$"""{ "sha256": "{{Sha}}", "stagedAtUtc": "2026-10-11T08:00:00+00:00", "source": "C:\\x.exe" }""", "version")]
    [InlineData("""{ "version": "0.4.0", "stagedAtUtc": "2026-10-11T08:00:00+00:00", "source": "C:\\x.exe" }""", "sha256")]
    [InlineData($$"""{ "version": "0.4.0", "sha256": "{{Sha}}", "source": "C:\\x.exe" }""", "stagedAtUtc")]
    [InlineData($$"""{ "version": "0.4.0", "sha256": "{{Sha}}", "stagedAtUtc": "2026-10-11T08:00:00+00:00" }""", "source")]
    [InlineData("""{ "version": "0.4.0", "sha256": "A14AD2EB", "stagedAtUtc": "2026-10-11T08:00:00+00:00", "source": "C:\\x.exe" }""", "sha256")]
    [InlineData($$"""{ "version": "0.4.0", "sha256": "{{Sha}}", "stagedAtUtc": "2026-10-11T08:00:00+00:00", "source": "C:\\x.exe", "extra": 1 }""", "extra")]
    [InlineData("""[ 1, 2 ]""", "stage.json")]
    [InlineData("""{ "version": """, "stage.json")]
    public void A_record_that_is_not_whole_is_refused_naming_why(string json, string named)
    {
        var read = StageRecords.Parse(Encoding.UTF8.GetBytes(json));

        read.Should().BeOfType<StageRead.Refused>().Which.Why.Should().Contain(named);
    }
}
