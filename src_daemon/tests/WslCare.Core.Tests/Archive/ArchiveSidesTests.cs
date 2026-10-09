using System.Text;

using FluentAssertions;

using WslCare.Core.Actions.Engine;
using WslCare.Core.Archive;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Archive;

/// <summary>
/// Plan §15r D2, E9.S5's row — two SIDES (the distro's and Windows') and two HOSTS archive into ONE base without meeting: each writes
/// only under its own <c>&lt;agent&gt;/&lt;yyyy&gt;/&lt;MM&gt;/&lt;side&gt;/</c> folder, each keeps its own month index there, and no
/// index names another side's entry. Three homes (each with its own state), one base.
/// </summary>
public sealed class ArchiveSidesTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] Sides = ["wsl-host-a-ubuntu", "windows-host-a", "windows-host-b"];

    private readonly TempRoot _base = new("archive-sides-base");
    private readonly List<LinuxSandbox> _homes = [];

    public void Dispose()
    {
        foreach (var home in _homes)
        {
            home.Dispose();
        }

        _base.Dispose();
    }

    [Fact]
    public void Two_sides_and_two_hosts_write_disjoint_folders_and_indexes_under_one_base()
    {
        var reports = Sides.Select((side, i) => ArchiveRun.Run(Input(Home(i), side))).ToList();

        reports.Should().OnlyContain(r => r.Outcome == RunOutcomes.Done && r.Agents.Single().Copied == 1);
        var month = Path.Combine(_base.Path, "claude-code", "2026", "08");
        Directory.GetDirectories(month).Select(Path.GetFileName).Should().BeEquivalentTo(Sides, "each side writes only its own folder");
        foreach (var side in Sides)
        {
            var index = File.ReadAllText(Path.Combine(month, side, ArchiveIndex.FileName));
            index.Should().Contain($"\"side\":\"{side}\"");
            Sides.Where(other => other != side).Should().OnlyContain(other => !index.Contains(other, StringComparison.Ordinal), "an index names only its own side's entries");
            Directory.EnumerateFiles(Path.Combine(month, side), "*", SearchOption.AllDirectories).Should().Contain(f => f.EndsWith($"s{Array.IndexOf(Sides, side)}.jsonl", StringComparison.Ordinal));
        }
    }

    /// <summary>One home: its own state and one due Claude Code session, named after it.</summary>
    private LinuxSandbox Home(int i)
    {
        var home = new LinuxSandbox($"archive-side-{i}");
        _homes.Add(home);
        var path = home.Write($"/home/me/.claude/projects/p/s{i}.jsonl", $"the transcript of home {i}");
        File.SetLastWriteTimeUtc(path, Now.AddDays(-60).UtcDateTime);
        return home;
    }

    private ArchiveRunInput Input(LinuxSandbox home, string side)
    {
        var config = ConfigLoader.Load([
            (ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults())),
            (new ConfigLayerFile(ConfigLayer.User, "user.json"), new FileReadResult.Content(Encoding.UTF8.GetBytes($$"""{ "archive": { "baseFolder": "{{_base.Path.Replace("\\", "\\\\", StringComparison.Ordinal)}}" } }"""))),
        ]).Config;
        var judged = new BaseFolderReport(1, "wsl", _base.Path, true, _base.Path, string.Empty, string.Empty, new BaseMountReport("/mnt/v", "9p", "drvfs"), [], []);
        return new ArchiveRunInput(home.Paths, home.Files, home.Files, config, new ManualTimeProvider(Now), new GoneProcesses(), TimeZoneInfo.Utc, judged, $"r-{side}", TimeSpan.FromMinutes(30), static _ => null, TestContext.Current.CancellationToken)
        {
            OnlyAgent = "claude-code",
            SideFolder = side,
            InUseScan = static _ => InUseView.Complete(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
            Me = new LeaseRecord(1, side, "boot-1", 4242, 1000, Now, string.Empty, Now),
        };
    }

    private sealed class GoneProcesses : IProcessTable
    {
        public ProcessLookup Lookup(int pid) => new ProcessLookup.Gone();
    }
}
