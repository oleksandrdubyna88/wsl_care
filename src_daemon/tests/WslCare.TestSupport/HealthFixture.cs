using System.Globalization;

using WslCare.Core.Health;
using WslCare.Core.Systemd;

namespace WslCare.TestSupport;

/// <summary>
/// The real <c>systemctl</c> / <c>journalctl</c> / <c>timedatectl</c> / <c>snap</c> / <c>powershell.exe</c> answers CAPTURED
/// from WSL <c>Ubuntu</c> on 2026-10-02 by the live contract (<c>src_daemon/tests/fixtures/health/ubuntu-2026-10-02</c>; its
/// <c>SOURCE.txt</c> says how and what was redacted), each paired with the product's OWN command.
/// </summary>
public static class HealthFixture
{
    public const string Name = "ubuntu-2026-10-02";

    public static string Root
    {
        get
        {
            var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "health", Name);
            return Directory.Exists(path) ? path : throw new DirectoryNotFoundException($"the health fixture should have been copied to {path}; does the project link ../fixtures?");
        }
    }

    public static DateTimeOffset CapturedAt =>
        DateTimeOffset.Parse(File.ReadAllText(PathOf("captured-at.txt")).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    public static string PathOf(string file) => Path.Combine(Root, file);

    public static string Read(string file) => File.ReadAllText(PathOf(file));

    /// <summary>Every exact-argv health command with the file its captured answer is in.</summary>
    public static IReadOnlyList<(IReadOnlyList<string> Argv, string File)> Answers =>
    [
        (SystemdCommands.FailedUnits.Argv, "systemctl-failed.out"),
        (SystemdCommands.ListBoots.Argv, "journalctl-list-boots.out"),
        (SystemdCommands.TimeSync.Argv, "timedatectl-show.out"),
        (SystemdCommands.Version.Argv, "systemctl-version.out"),
        (SystemdCommands.ShowUnit("fstrim.timer").Argv, "systemctl-show-fstrim.out"),
        (SystemdCommands.ShowUnit("wsl-pro.service").Argv, "systemctl-show-wsl-pro.out"),
        (HealthCommands.SnapList.Argv, "snap-list-all.out"),
        (HealthCommands.WindowsClock.Argv, "powershell-clock.out"),
    ];

    /// <summary>Whether <paramref name="argv"/> is the product's journal search of <paramref name="scope"/>, whatever its <c>--since</c>.</summary>
    public static bool IsSearch(IReadOnlyList<string> argv, string scope) =>
        argv is ["journalctl", "--since", _, "--no-pager", "--quiet", "--output=cat", var s, _] && s == scope;

    /// <summary>Adds the captured health answers to <paramref name="runner"/> (the journal searches by predicate: their
    /// argv carries the instant).</summary>
    public static RecordingCommandRunner Script(RecordingCommandRunner runner)
    {
        foreach (var (argv, file) in Answers)
        {
            runner.Script(argv, 0, Read(file));
        }

        runner.Script(SystemdCommands.JournalDiskUsage.Argv, 0, DockerFixture.Read("journalctl-disk-usage.out"));
        runner.Script(a => IsSearch(a, "--unit=systemd-resolved"), RecordingCommandRunner.Exited(0, Read("journalctl-search-clock.out")));
        return runner.Script(a => IsSearch(a, "--dmesg"), RecordingCommandRunner.Exited(0, Read("journalctl-search-kernel.out")));
    }
}
