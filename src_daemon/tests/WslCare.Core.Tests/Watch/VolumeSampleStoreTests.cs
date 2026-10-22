using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Health;
using WslCare.Core.Watch;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Watch;

/// <summary>
/// E19.S2: <c>volumes.json</c> — the Windows watch's native volume sample. Written atomically in the state directory, read back with a REASON
/// whenever it cannot be (a missing, unreadable, foreign-version or half-shaped file is never an empty table), and an unread figure
/// survives the round trip as unread — never as 0.
/// </summary>
public sealed class VolumeSampleStoreTests : IDisposable
{
    private const long Gib = 1L << 30;

    private static readonly DateTimeOffset At = new(2026, 10, 22, 9, 30, 0, TimeSpan.Zero);

    private readonly SandboxHost _sandbox = new("volume-sample");

    public void Dispose() => _sandbox.Dispose();

    private VolumeSampleStore Store => new(_sandbox.Paths, _sandbox.Files);

    private static WindowsVolume Volume(string letter, Reading<long> size, Reading<long> free, string fileSystem = "NTFS", string label = "") =>
        new(letter, Reading.Missing<int>("not read natively"), fileSystem, size, free, label);

    private void WriteRaw(string json)
    {
        Directory.CreateDirectory(_sandbox.Paths.StateDirectory);
        File.WriteAllText(Store.File, json);
    }

    [Fact]
    public void A_sample_written_is_read_back_whole_in_the_state_directory()
    {
        var written = Store.TryWrite(VolumeSample.Of(At, [Volume("C", Reading.Of(953 * Gib), Reading.Of(114 * Gib), "NTFS", "System"), Volume("E", Reading.Of(30_000_000L), Reading.Of(12_000_000L), "FAT")]));

        written.Should().BeEmpty();
        Store.File.Should().Be(_sandbox.Paths.Rules.Join(_sandbox.Paths.StateDirectory, "volumes.json"));
        var sample = Store.Read().Should().BeOfType<Reading<VolumeSample>.Available>().Subject.Value;
        sample.SampledAt.Should().Be(At);
        sample.SchemaVersion.Should().Be(SchemaVersion.Current);
        var volumes = sample.ToVolumes();
        volumes.Select(v => v.Letter).Should().Equal("C", "E");
        volumes[0].SizeBytes.Should().Be(Reading.Of(953 * Gib));
        volumes[0].FreeBytes.Should().Be(Reading.Of(114 * Gib));
        (volumes[0].FileSystem, volumes[0].Label).Should().Be(("NTFS", "System"));
        Directory.GetFiles(_sandbox.Paths.StateDirectory).Should().ContainSingle("the atomic write leaves no temp file behind");
    }

    [Fact]
    public void An_unread_figure_stays_unread_with_its_reason_across_the_file()
    {
        Store.TryWrite(VolumeSample.Of(At, [Volume("F", Reading.Missing<long>("F: is not ready"), Reading.Missing<long>("F: is not ready"))])).Should().BeEmpty();

        var volume = Store.Read().Should().BeOfType<Reading<VolumeSample>.Available>().Subject.Value.ToVolumes().Single();

        volume.SizeBytes.Should().Be(Reading.Missing<long>("F: is not ready"));
        volume.FreeBytes.Should().Be(Reading.Missing<long>("F: is not ready"));
        File.ReadAllText(Store.File).Should().NotContain("\"bytes\": 0", "an unread figure is never written as 0");
    }

    [Fact]
    public void An_unknown_schema_version_is_unreadable()
    {
        WriteRaw("""{ "schemaVersion": 99, "sampledAt": "2026-10-22T09:30:00+00:00", "volumes": [] }""");

        var read = Store.Read();

        read.IsAvailable.Should().BeFalse("a file of a version this build does not know is not guessed at");
        read.ReasonOrEmpty.Should().Contain("schema 99").And.Contain(Store.File);
    }

    [Fact]
    public void A_missing_file_is_unreadable_and_says_it_does_not_exist()
    {
        var read = Store.Read();

        read.IsAvailable.Should().BeFalse();
        read.ReasonOrEmpty.Should().Contain("does not exist");
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("null")]
    [InlineData("""{ "schemaVersion": 1, "sampledAt": "2026-10-22T09:30:00+00:00" }""")]
    [InlineData("""{ "schemaVersion": 1, "sampledAt": "2026-10-22T09:30:00+00:00", "volumes": [ null ] }""")]
    [InlineData("""{ "schemaVersion": 1, "sampledAt": "2026-10-22T09:30:00+00:00", "volumes": [ { "letter": "C" } ] }""")]
    public void A_file_that_is_not_a_volume_sample_is_unreadable_never_an_empty_table(string content)
    {
        WriteRaw(content);

        var read = Store.Read();

        read.IsAvailable.Should().BeFalse();
        read.ReasonOrEmpty.Should().Contain(Store.File);
    }

    [Fact]
    public void A_write_the_file_system_refuses_is_a_reason_not_an_exception()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_sandbox.Paths.StateDirectory)!);
        File.WriteAllText(_sandbox.Paths.StateDirectory, "a file where the state directory should be");

        Store.TryWrite(VolumeSample.Of(At, [])).Should().NotBeEmpty();
    }
}
