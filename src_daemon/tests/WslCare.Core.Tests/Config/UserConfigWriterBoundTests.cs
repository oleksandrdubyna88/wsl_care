using FluentAssertions;

using WslCare.Core.Config;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Config;

/// <summary>Plan §15q R1.1 (review minor): <c>config set</c> reads the layer it rewrites through a bounded reader, so a FIFO in
/// the layer's place is moved aside at once — never waited on — and a valid file is written in its place.</summary>
public sealed class UserConfigWriterBoundTests
{
    [Fact]
    public async Task Config_set_over_a_fifo_moves_it_aside_and_never_waits_on_it()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "a FIFO is a Linux file: run in WSL or on the Linux legs");
        using var host = new SandboxHost("writer-fifo");
        var layer = host.Paths.UserConfigFile;
        Directory.CreateDirectory(Path.GetDirectoryName(layer)!);
        (await ChildProcess.RunAsync("mkfifo", [layer], new Dictionary<string, string?>())).Exit.Should().Be(0);

        var write = Task.Run(() => new UserConfigWriter(host.Paths, host.Files, new FixedTimeProvider()).Set(ConfigKeys.DryRun, new ConfigValue.Bool(false)), TestContext.Current.CancellationToken);
        var finished = await Task.WhenAny(write, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)) == write;
        if (!finished)
        {
            await using var writer = new FileStream(layer, FileMode.Open, FileAccess.Write); // releases the blocked reader
        }

        finished.Should().BeTrue("config set never waits on a FIFO");
        (await write).Should().BeOfType<UserConfigWriteResult.Written>().Which.MovedAsideTo.Should().Contain(".broken-");
        ConfigLoader.Load(host.Paths, host.Files).Config.Bool(ConfigKeys.DryRun).Should().BeFalse();
    }
}
