using System.Text;

using FluentAssertions;

using WslCare.Core.Actions.Engine;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;
using WslCare.Core.Systemd;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary>
/// The world the in-process detached-run tests share (§15o review O1 — one copy instead of two): a Linux sandbox with a target
/// user and systemd booted, a product-policy runner that answers the journal's size, a root CLI host over them, the request
/// files planted the way <c>--detach</c> writes them, and the records read back.
/// </summary>
internal sealed class DetachedRunHarness : IDisposable
{
    public static readonly ProcessPrivilege Root = new(true, "a test says so");
    public static readonly ProcessPrivilege NotRoot = new(false, "this process does not run as root (a test)");
    public static readonly DateTimeOffset Now = FixedTimeProvider.DefaultNow;

    public DetachedRunHarness(string name)
    {
        Sandbox = new LinuxSandbox(name);
        Sandbox.Write("/etc/passwd", "root:x:0:0::/root:/bin/bash\nme:x:1000:1000::/home/me:/bin/bash\n");
        Directory.CreateDirectory(Sandbox.Paths.DistroPath("/run/systemd/system"));
    }

    public LinuxSandbox Sandbox { get; }

    public RecordingCommandRunner Runner { get; } = new RecordingCommandRunner { Policy = CommandPolicy.Product }
        .Script(SystemdCommands.JournalDiskUsage.Argv, 0, "Archived and active journals take up 1.5G in the file system.");

    public void Dispose() => Sandbox.Dispose();

    /// <summary>A CLI host over the sandbox — root unless said otherwise, <paramref name="stdin"/> on its standard input,
    /// <paramref name="files"/> in place of the sandbox's file system when a test needs one answer changed.</summary>
    public CliHost Host(ProcessPrivilege? privilege = null, string stdin = "", IFileSystem? files = null) =>
        new(Sandbox.Paths, files ?? Sandbox.Files, new FixedTimeProvider(), Runner)
        {
            Privilege = privilege ?? Root,
            Processes = new FakeProcessTable(),
            Probe = new FakeProbe(HostSide.Wsl, new FixedTimeProvider()),
            StandardInput = () => new MemoryStream(Encoding.UTF8.GetBytes(stdin)),
        };

    /// <summary>A request as <c>--detach</c> writes it, <paramref name="age"/> old, under the run id of <paramref name="pid"/>.</summary>
    public RunRequestFile Plant(string kind, IReadOnlyList<string> actions, TimeSpan age, int pid = 77)
    {
        var request = new RunRequestFile(1, RunId.New(Now - age, pid), kind, actions, RunTrigger.Manual, Now - age);
        RunRequests.Create(Sandbox.Paths, Sandbox.Files, request).Should().BeOfType<ExclusiveCreate.Created>();
        return request;
    }

    /// <summary>The machine layer (<c>/etc/wsl-care/config.json</c>) — where a machine-only key such as
    /// <c>requests.lockWaitSeconds</c> is set.</summary>
    public void MachineLayer(string json) => Sandbox.Write("/etc/wsl-care/config.json", json);

    public IReadOnlyList<RunRequestRead> Requests() => RunRequests.List(Sandbox.Paths, Sandbox.Files);

    public IReadOnlyList<RunRecord> History() => RunHistory.Read(Sandbox.Paths, Sandbox.Files).Records;
}
