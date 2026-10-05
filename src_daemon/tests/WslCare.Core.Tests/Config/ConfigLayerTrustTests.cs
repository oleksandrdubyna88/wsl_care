using FluentAssertions;

using WslCare.Core.Config;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Config;

/// <summary>
/// Plan §15q R1.1: root reads the user configuration layer every 4 h, and that file is the USER's. So it is read like a file
/// another account controls — regular, nonblocking, capped, never through a link, owner-checked — and a layer that fails is a
/// <see cref="ConfigError"/> naming why (observe-only, plan §15a #1), never a hang and never a followed link.
/// </summary>
public sealed class ConfigLayerTrustTests : IDisposable
{
    private const string UserLayer = "/home/me/.config/wsl-care/config.json";

    private readonly LinuxSandbox _sandbox = new("config-layer-trust");

    public void Dispose() => _sandbox.Dispose();

    [Fact]
    public async Task A_user_layer_that_is_a_fifo_is_refused_at_once_and_never_waited_on()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "a FIFO is a Linux file: run in WSL or on the Linux legs");
        var fifo = _sandbox.Paths.DistroPath(UserLayer);
        Directory.CreateDirectory(Path.GetDirectoryName(fifo)!);
        (await ChildProcess.RunAsync("mkfifo", [fifo], new Dictionary<string, string?>())).Exit.Should().Be(0);

        var load = Task.Run(() => ConfigLoader.Load(_sandbox.Paths, _sandbox.Files), TestContext.Current.CancellationToken);
        var finished = await Task.WhenAny(load, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)) == load;
        if (!finished)
        {
            await using var writer = new FileStream(fifo, FileMode.Open, FileAccess.Write); // releases the blocked reader
        }

        finished.Should().BeTrue("the user layer is read without waiting on a FIFO");
        var result = await load;
        result.Should().BeOfType<ConfigLoadResult.ObserveOnly>();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("not a regular file");
    }

    [Fact]
    public void A_user_layer_that_is_a_link_is_never_followed()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "a file link needs no privilege on Linux; run in WSL or on the Linux legs");
        var elsewhere = _sandbox.Write("/root/secret.json", """{ "dryRun": false }""");
        var link = _sandbox.Paths.DistroPath(UserLayer);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(link, elsewhere);

        var result = ConfigLoader.Load(_sandbox.Paths, _sandbox.Files);

        result.Should().BeOfType<ConfigLoadResult.ObserveOnly>();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("link").And.Contain("never followed");
        result.Config.Bool(ConfigKeys.DryRun).Should().BeTrue("nothing the link points at may reach the run");
    }

    [Fact]
    public void A_group_writable_user_layer_is_refused_naming_the_fix()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "file modes are Linux's: run in WSL or on the Linux legs");
        var layer = _sandbox.Write(UserLayer, """{ "dryRun": false }""");
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(layer, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead);
        }

        var result = ConfigLoader.Load(_sandbox.Paths, _sandbox.Files);

        result.Should().BeOfType<ConfigLoadResult.ObserveOnly>();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("writable by group or others").And.Contain("chmod go-w");
        result.Config.Bool(ConfigKeys.DryRun).Should().BeTrue();
    }

    [Fact]
    public void An_ordinary_user_layer_is_still_read()
    {
        _sandbox.Write(UserLayer, """{ "dryRun": false }""");

        var result = ConfigLoader.Load(_sandbox.Paths, _sandbox.Files);

        result.Should().BeOfType<ConfigLoadResult.Valid>();
        result.Config.Bool(ConfigKeys.DryRun).Should().BeFalse();
    }
}
