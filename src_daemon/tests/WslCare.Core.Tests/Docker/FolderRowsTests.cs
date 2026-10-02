using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Docker;
using WslCare.Core.Folders;
using WslCare.Core.Preview;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Docker;

/// <summary>A8 and A9 of the cleanup table (plan §4.3) come from the newest folder sample a full run recorded — with
/// its age in the basis — and stay unavailable with the reason until one exists.</summary>
public sealed class FolderRowsTests : IDisposable
{
    private readonly SandboxHost _sandbox = new("folder-rows");

    public void Dispose() => _sandbox.Dispose();

    private async Task<CleanupPreview> BuildAsync(Reading<AgedPart<FolderSizesSample>> folders)
    {
        var snapshot = await new DockerCollector(new DockerCli(DockerFixture.Runner()), new FixedTimeProvider(DockerFixture.CapturedAt)).CollectAsync(CancellationToken.None);
        return CleanupPreviews.Build(snapshot, VolumeSeenRecord.Empty, ConfigLoader.Load(_sandbox.Paths, _sandbox.Files).Config, DockerFixture.CapturedAt, folders);
    }

    [Fact]
    public async Task A8_and_A9_read_the_newest_folder_sample_with_its_age()
    {
        var sampledAt = DockerFixture.CapturedAt.AddHours(-3);
        var sample = new FolderSizesSample(sampledAt,
        [
            new FolderSize(FolderSizes.NpmCache, "/home/me/.npm", 5_300_000_000, 41_000, true, string.Empty),
            new FolderSize(FolderSizes.AptCache, "/var/cache/apt", 116_815_541, 40, true, string.Empty),
            new FolderSize(FolderSizes.SnapDisabled, "/var/lib/snapd/snaps", 300_000_000, 2, true, string.Empty),
        ]);
        var id = RunId.New(sampledAt, 9);

        var rows = (await BuildAsync(Reading.Of(new AgedPart<FolderSizesSample>(sample, id, sampledAt, TimeSpan.FromHours(3))))).Rows;

        var a8 = rows.Single(r => r.Id == "A8");
        a8.Figures.ValueOr(null!).Should().Match<RowFigures>(f => f.Count == 1 && f.Bytes == 5_300_000_000);
        a8.Basis.Should().Contain(id.Text).And.Contain("3.0 h ago");
        rows.Single(r => r.Id == "A9").Figures.ValueOr(null!).Should().Match<RowFigures>(f => f.Count == 3 && f.Bytes == 416_815_541);
    }

    [Fact]
    public async Task Without_a_folder_sample_A8_and_A9_are_unavailable_with_the_reason()
    {
        var rows = (await BuildAsync(Reading.Missing<AgedPart<FolderSizesSample>>("no recorded full run has sampled the folder sizes yet"))).Rows;

        rows.Single(r => r.Id == "A8").Figures.ReasonOrEmpty.Should().Contain("no recorded full run");
        rows.Single(r => r.Id == "A9").Figures.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task A_folder_the_full_run_could_not_measure_names_why()
    {
        var sampledAt = DockerFixture.CapturedAt.AddHours(-1);
        var sample = new FolderSizesSample(sampledAt, [new FolderSize(FolderSizes.NpmCache, "/home/me/.npm", 0, 0, true, "/home/me/.npm does not exist")]);

        var rows = (await BuildAsync(Reading.Of(new AgedPart<FolderSizesSample>(sample, RunId.New(sampledAt, 1), sampledAt, TimeSpan.FromHours(1))))).Rows;

        rows.Single(r => r.Id == "A8").Figures.ReasonOrEmpty.Should().Contain("does not exist");
    }
}
